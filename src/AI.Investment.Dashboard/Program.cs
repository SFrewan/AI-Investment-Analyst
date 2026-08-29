using AI.Investment.Dashboard;
using AI.Investment.Dashboard.Localization;
using AI.Investment.Dashboard.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// The platform's address is runtime configuration, read from wwwroot/appsettings.json, so the same
// build can be pointed at a different host without being rebuilt. It falls back to the origin the
// dashboard was served from, which is the case when the API hosts it.
var apiBase = builder.Configuration["Api:BaseAddress"];

var baseAddress = string.IsNullOrWhiteSpace(apiBase)
    ? builder.HostEnvironment.BaseAddress
    : apiBase;

builder.Services.AddSingleton(new HttpClient
{
    BaseAddress = new Uri(baseAddress, UriKind.Absolute),

    // A dashboard request that has not answered in fifteen seconds is one the operator should be
    // told about rather than one a spinner should keep spinning for.
    Timeout = TimeSpan.FromSeconds(15),
});

builder.Services.AddSingleton<OperatorSession>();
builder.Services.AddSingleton<PlatformClient>();
builder.Services.AddSingleton<LocalizationState>();
builder.Services.AddSingleton<RefreshState>();

await builder.Build().RunAsync().ConfigureAwait(false);
