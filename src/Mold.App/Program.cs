using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Mold.App;
using Mold.App.Services;
using Mold.Engine;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddSingleton<IGameEngine, GameEngine>();
builder.Services.AddSingleton<FirstBloomReplay>();
builder.Services.AddScoped<BrowserStorage>();
builder.Services.AddScoped<GameSession>();

await builder.Build().RunAsync();
