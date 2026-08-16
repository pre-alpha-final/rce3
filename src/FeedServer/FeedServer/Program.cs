using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace FeedServer;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var initialOptions = builder.Configuration
            .GetSection(FeedServerOptions.SectionName)
            .Get<FeedServerOptions>() ?? new FeedServerOptions();

        builder.Services
            .AddOptions<FeedServerOptions>()
            .Bind(builder.Configuration.GetSection(FeedServerOptions.SectionName))
            .Validate(FeedServerOptions.IsValid, FeedServerOptions.ValidationFailureMessage)
            .ValidateOnStart();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<FeedStore>();
        builder.Services.AddHostedService<FeedExpirationService>();

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = initialOptions.MaxMessageSizeBytes;
            options.Limits.RequestHeadersTimeout = initialOptions.RequestHeadersTimeout;
            options.Limits.MinRequestBodyDataRate = new MinDataRate(
                initialOptions.MinRequestBodyDataRateBytesPerSecond,
                initialOptions.MinRequestBodyDataRateGracePeriod);
        });

        if (!HasConfiguredUrls(builder.Configuration))
        {
            builder.WebHost.UseUrls($"http://0.0.0.0:{initialOptions.Port}");
        }

        var app = builder.Build();

        FeedEndpoints.Map(app);

        app.Run();
    }

    private static bool HasConfiguredUrls(ConfigurationManager configuration)
    {
        return !string.IsNullOrWhiteSpace(configuration["urls"])
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS"));
    }
}
