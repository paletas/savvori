using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Savvori.Shared;

namespace Savvori.WebApi.Modeling;

public static class ModelHttp
{
    /// <summary>
    /// Sends a request through the shared <see cref="GatewayThrottle"/> (when given) and maps every failure:
    /// transport errors, timeouts, 5xx and 404 to <see cref="ModelUnavailableException"/>; a 429 to
    /// <see cref="ModelThrottledException"/> (a proxy-limiter 429 is first retried in place after sleeping
    /// Retry-After); a gateway 429 that is not time-bound and other 4xx to <see cref="ModelResponseException"/>.
    /// Cancellation by the caller is rethrown untouched. <paramref name="makeRequest"/> is called once per send,
    /// because a request message cannot be reused. <paramref name="throttle"/> is null for OpenRouter (no limiter,
    /// no in-place retry). Records one ModelCalls row per HTTP attempt when given a recorder (and a call context);
    /// a successful attempt is recorded by <see cref="ReadAsync{T}(HttpResponseMessage, ModelCallContext?, IModelCallRecorder?, TimeSpan, Func{T, JudgeUsage?}?, CancellationToken)"/>
    /// once the body (and so the usage) is known.
    /// </summary>
    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient http, Func<HttpRequestMessage> makeRequest, GatewayThrottle? throttle, TimeProvider time,
        CancellationToken ct, ModelCallContext? call = null, IModelCallRecorder? recorder = null) =>
        (await SendTimedAsync(http, makeRequest, throttle, time, ct, call, recorder)).Response;

    /// <summary><see cref="SendAsync"/> that also returns the wall time of the successful attempt.</summary>
    public static async Task<(HttpResponseMessage Response, TimeSpan Elapsed)> SendTimedAsync(
        HttpClient http, Func<HttpRequestMessage> makeRequest, GatewayThrottle? throttle, TimeProvider time,
        CancellationToken ct, ModelCallContext? call = null, IModelCallRecorder? recorder = null)
    {
        var o = throttle?.Options ?? new GatewayThrottleOptions();
        Task Record(ModelCallStatus st, int? httpStatus, TimeSpan latency) =>
            call is null || recorder is null ? Task.CompletedTask : recorder.RecordAsync(call, st, httpStatus, latency, null);
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage response;
            var lease = throttle is null ? null : await throttle.AcquireAsync(ct);
            var sw = Stopwatch.StartNew();
            try
            {
                using var request = makeRequest();
                response = await http.SendAsync(request, ct);
                throttle?.Observe(response);
            }
            catch (HttpRequestException ex)
            {
                await Record(ModelCallStatus.Unavailable, null, sw.Elapsed);
                throw new ModelUnavailableException($"Model server unreachable: {ex.Message}", ex);
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                await Record(ModelCallStatus.Unavailable, null, sw.Elapsed);
                throw new ModelUnavailableException("Model request timed out.", ex);
            }
            finally
            {
                // Release the slot before any sleeping below.
                lease?.Dispose();
            }

            if (response.IsSuccessStatusCode) return (response, sw.Elapsed);
            var attemptElapsed = sw.Elapsed;

            var status = response.StatusCode;
            var now = time.GetUtcNow();
            var hdr = ParseRetryAfter(response, now);
            string? code = null;
            long? retryAtMs = null;
            if (status == HttpStatusCode.TooManyRequests)
            {
                var body = await ReadBodyAsync(response, ct);
                (code, retryAtMs) = ParseErrorBody(body);
            }
            else if ((int)status >= 500)
            {
                var body = await ReadBodyAsync(response, ct);
                (code, _) = ParseErrorBody(body);
            }
            response.Dispose();

            if (status == HttpStatusCode.TooManyRequests)
            {
                await Record(ModelCallStatus.Throttled, 429, attemptElapsed);
                var retryAt = retryAtMs is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) - now : (TimeSpan?)null;
                var dflt = TimeSpan.FromSeconds(o.DefaultRetryAfterSeconds);

                if (code is null)
                {
                    // Proxy limiter (per source IP): wait for the window and retry in place.
                    var wait = hdr ?? dflt;
                    var proxyMax = TimeSpan.FromSeconds(o.ProxyMaxRetryAfterSeconds);
                    if (throttle is not null && wait <= proxyMax && attempt < o.ProxyRetryMax)
                    {
                        await throttle.SleepAsync(wait + TimeSpan.FromMilliseconds(o.PaddingMs), ct);
                        continue;
                    }
                    throw new ModelThrottledException(wait, wait <= proxyMax ? ThrottleKind.Proxy : ThrottleKind.Routing,
                        "Gateway rate limit (429) persisted after retries.");
                }

                if (code == "rate_limit_exceeded")
                {
                    var after = Max(hdr ?? TimeSpan.Zero, retryAt ?? TimeSpan.Zero);
                    if (after <= TimeSpan.Zero) after = dflt;
                    throw new ModelThrottledException(
                        Clamp(after, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(o.MaxDeferSeconds)),
                        ThrottleKind.Routing, "Gateway routes are rate limited (429 rate_limit_exceeded).");
                }

                if (code == "quota_exceeded")
                {
                    var after = retryAt ?? hdr ?? TimeSpan.FromHours(24);
                    throw new ModelThrottledException(
                        Clamp(after, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(o.QuotaMaxDeferSeconds)),
                        ThrottleKind.Quota, "Gateway monthly quota exhausted (429 quota_exceeded).");
                }

                throw new ModelResponseException($"Gateway rejected the request: 429 {code}.");
            }

            var unavailable = (int)status >= 500 || status == HttpStatusCode.NotFound;
            await Record(unavailable ? ModelCallStatus.Unavailable : ModelCallStatus.Error, (int)status, attemptElapsed);
            if (unavailable)
                throw new ModelUnavailableException(
                    $"Model server returned {(int)status} {status}." + (code is null ? "" : $" ({code})"));

            throw new ModelResponseException($"Model server rejected the request with {(int)status} {status}.");
        }
    }

    /// <summary>Retry-After as a delay: delta-seconds or an HTTP date; negative becomes zero; absent or unparsable is null.</summary>
    public static TimeSpan? ParseRetryAfter(HttpResponseMessage r, DateTimeOffset now)
    {
        var ra = r.Headers.RetryAfter;
        if (ra?.Delta is { } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (ra?.Date is { } date)
        {
            var d = date - now;
            return d < TimeSpan.Zero ? TimeSpan.Zero : d;
        }
        if (r.Headers.TryGetValues("Retry-After", out var values) &&
            double.TryParse(values.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var secs))
            return secs < 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(secs);
        return null;
    }

    /// <summary>
    /// Sends a body-less request (ping, key probe) and records it as Ok with no items. Failures are recorded by
    /// <see cref="SendTimedAsync"/>.
    /// </summary>
    public static async Task SendAndRecordAsync(
        HttpClient http, Func<HttpRequestMessage> makeRequest, GatewayThrottle? throttle, TimeProvider time,
        CancellationToken ct, ModelCallContext call, IModelCallRecorder recorder)
    {
        var (response, elapsed) = await SendTimedAsync(http, makeRequest, throttle, time, ct, call, recorder);
        response.Dispose();
        await recorder.RecordAsync(call, ModelCallStatus.Ok, 200, elapsed, null);
    }

    /// <summary>
    /// <see cref="ReadAsync{T}(HttpResponseMessage, CancellationToken)"/> that then records the attempt:
    /// Ok with the usage from <paramref name="usage"/>, or Error / Unavailable when the body is unusable.
    /// </summary>
    public static async Task<T> ReadAsync<T>(
        HttpResponseMessage response, ModelCallContext? call, IModelCallRecorder? recorder, TimeSpan sendElapsed,
        Func<T, JudgeUsage?>? usage, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        async Task Record(ModelCallStatus st, int? httpStatus, JudgeUsage? u)
        {
            if (call is not null && recorder is not null)
                await recorder.RecordAsync(call, st, httpStatus, sendElapsed + sw.Elapsed, u);
        }
        T body;
        try
        {
            body = await ReadAsync<T>(response, ct);
        }
        catch (ModelResponseException)
        {
            await Record(ModelCallStatus.Error, 200, null);
            throw;
        }
        catch (ModelUnavailableException)
        {
            await Record(ModelCallStatus.Unavailable, 200, null);
            throw;
        }
        await Record(ModelCallStatus.Ok, 200, usage?.Invoke(body));
        return body;
    }

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<T>(ct);
            return body ?? throw new ModelResponseException("Model server returned an empty body.");
        }
        catch (JsonException ex)
        {
            throw new ModelResponseException("Model server returned malformed JSON.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ModelUnavailableException($"Model response was cut off: {ex.Message}", ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ModelUnavailableException("Model response timed out.", ex);
        }
        finally
        {
            response.Dispose();
        }
    }

    private static async Task<string?> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await response.Content.ReadAsStringAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) { return null; }
    }

    /// <summary>Tolerant parse of <c>{"error":{"code":..,"retryAtMs":..}}</c>; anything else yields nulls.</summary>
    private static (string? Code, long? RetryAtMs) ParseErrorBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
                return (null, null);
            string? code = error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            long? retryAt = error.TryGetProperty("retryAtMs", out var r) && r.ValueKind == JsonValueKind.Number &&
                            r.TryGetInt64(out var ms) ? ms : null;
            return (string.IsNullOrEmpty(code) ? null : code, retryAt);
        }
        catch (JsonException) { return (null, null); }
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private static TimeSpan Clamp(TimeSpan v, TimeSpan min, TimeSpan max) => v < min ? min : v > max ? max : v;
}
