using AudioCentral.Web;
using AudioCentral.Web.Services;
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
builder.Services.AddScoped<CultureService>();
builder.Services.AddLocalization();
builder.Services.AddRadzenComponents();

var host = builder.Build();
await CultureService.ApplyStoredCultureAsync(host.Services.GetRequiredService<IJSRuntime>());
await host.RunAsync();
