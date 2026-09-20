using Savvori.WebApp.Services;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddRazorPages();

// Typed HTTP client for WebApi calls
builder.Services.AddHttpClient<SavvoriApiClient>(client =>
{
    var apiUrl = builder.Configuration["services:webapi:https:0"]
        ?? builder.Configuration["services:webapi:http:0"]
        ?? "http://localhost:5000";
    client.BaseAddress = new Uri(apiUrl);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
})
.AddStandardResilienceHandler();

var app = builder.Build();

app.MapDefaultEndpoints();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();

app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();

app.Run();

// Expose Program class for WebApplicationFactory in tests
public partial class Program { }
