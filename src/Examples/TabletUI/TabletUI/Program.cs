using System.Globalization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using TabletUI;
using TabletUI.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");
builder.Services.AddScoped(_ => new HttpClient { Timeout = Timeout.InfiniteTimeSpan });
builder.Services.AddSingleton(TimeProvider.System);
var previewLimit = int.TryParse(builder.Configuration["TabletUI:MessagePreviewLimit"],
    NumberStyles.Integer, CultureInfo.InvariantCulture, out var configuredLimit) && configuredLimit > 0
        ? configuredLimit : CommunicationEntry.DefaultPreviewLimit;
builder.Services.AddScoped(services => new FeedConnection(
    services.GetRequiredService<HttpClient>(),
    services.GetRequiredService<TimeProvider>(),
    services.GetRequiredService<ConnectionStorage>(),
    messagePreviewLimit: previewLimit));
builder.Services.AddScoped<ConnectionStorage>();

await builder.Build().RunAsync();
