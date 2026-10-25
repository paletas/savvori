namespace Savvori.WebApi.Modeling;

/// <summary>The FreeLLMAPI gateway (OpenAI-compatible, per-source-IP rate limited): embeddings, category judge, translator.</summary>
public sealed class GatewayOptions
{
    public string BaseUrl { get; set; } = "http://192.168.1.19:3001/v1";
    /// <summary>Bearer key; set only through the environment (<c>Model__Gateway__ApiKey</c>), never in appsettings.</summary>
    public string ApiKey { get; set; } = string.Empty;
    public GatewayThrottleOptions Throttle { get; set; } = new();
}

/// <summary>OpenRouter, used by the pair judge.</summary>
public sealed class OpenRouterOptions
{
    public string BaseUrl { get; set; } = "https://openrouter.ai";
    /// <summary>Bearer key; set only through the environment (<c>Model__OpenRouter__ApiKey</c>), never in appsettings.</summary>
    public string ApiKey { get; set; } = string.Empty;
}

/// <summary>Client-side pacing for the gateway, which allows 120 requests per source IP per fixed 60 s window.</summary>
public sealed class GatewayThrottleOptions
{
    /// <summary>Local cap on requests per window (the server allows 120).</summary>
    public int WindowRequests { get; set; } = 100;
    /// <summary>Length of the fixed window in seconds.</summary>
    public int WindowSeconds { get; set; } = 60;
    /// <summary>Sleep until the server's Reset when X-RateLimit-Remaining is at or below this.</summary>
    public int LowWater { get; set; } = 10;
    /// <summary>Most requests in flight at once.</summary>
    public int MaxInFlight { get; set; } = 3;
    /// <summary>In-place retries after a proxy-limiter 429.</summary>
    public int ProxyRetryMax { get; set; } = 3;
    /// <summary>A longer Retry-After than this (seconds) is not retried in place.</summary>
    public int ProxyMaxRetryAfterSeconds { get; set; } = 60;
    /// <summary>Milliseconds added to every Retry-After sleep.</summary>
    public int PaddingMs { get; set; } = 500;
    /// <summary>Retry-After assumed when a 429 carries no hint.</summary>
    public int DefaultRetryAfterSeconds { get; set; } = 60;
    /// <summary>Clamp for the deferral after a routing 429, in seconds (24 h).</summary>
    public int MaxDeferSeconds { get; set; } = 86400;
    /// <summary>Clamp for the deferral after quota_exceeded, in seconds (31 d).</summary>
    public int QuotaMaxDeferSeconds { get; set; } = 2678400;
}
