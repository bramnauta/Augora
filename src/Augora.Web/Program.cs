using Augora.Web;
using Augora.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.JSInterop;
using Radzen;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<ApiClient>();
builder.Services.AddScoped<PlayerStore>();
builder.Services.AddScoped<BrowserPlayerService>();
builder.Services.AddScoped<CultureService>();

builder.Services.AddAuthorizationCore();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<ServerAuthenticationStateProvider>();
builder.Services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<ServerAuthenticationStateProvider>());

builder.Services.AddLocalization();
builder.Services.AddRadzenComponents();

var host = builder.Build();
await CultureService.ApplyStoredCultureAsync(host.Services.GetRequiredService<IJSRuntime>());
await host.RunAsync();
