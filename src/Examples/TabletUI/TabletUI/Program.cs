using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using TabletUI;
using TabletUI.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");
builder.Services.AddScoped(_ => new HttpClient { Timeout = Timeout.InfiniteTimeSpan });
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<FeedConnection>();
builder.Services.AddScoped<ConnectionStorage>();

await builder.Build().RunAsync();
