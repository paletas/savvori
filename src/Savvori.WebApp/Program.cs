using Microsoft.AspNetCore.HttpOverrides;
using Savvori.WebApp.Services;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddRazorPages();
builder.Services.AddHttpContextAccessor();
builder.Services.AddTransient<AcceptLanguageForwardingHandler>();

// Typed HTTP client for WebApi calls
builder.Services.AddHttpClient<SavvoriApiClient>(client =>
{
    var apiUrl = builder.Configuration["services:webapi:https:0"]
        ?? builder.Configuration["services:webapi:http:0"]
        ?? "http://localhost:5000";
    client.BaseAddress = new Uri(apiUrl);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
})
.AddHttpMessageHandler<AcceptLanguageForwardingHandler>()
.AddStandardResilienceHandler();

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    // The reverse proxy lives on the homelab network, not a fixed address.
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

app.MapDefaultEndpoints();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

// TLS is terminated by the reverse proxy in deployment; redirecting inside the container would loop.
app.UseForwardedHeaders();
if (app.Configuration.GetValue("HttpsRedirection:Enabled", true))
{
    app.UseHttpsRedirection();
}
app.UseRouting();

app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();

app.Run();

// Expose Program class for WebApplicationFactory in tests
public partial class Program { }
