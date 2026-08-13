
namespace FeedServer;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var initialOptions = builder.Configuration
            .GetSection(FeedServerOptions.SectionName)
            .Get<FeedServerOptions>() ?? new FeedServerOptions();

        builder.Services.AddOpenApi();
        builder.Services
            .AddOptions<FeedServerOptions>()
            .Bind(builder.Configuration.GetSection(FeedServerOptions.SectionName))
            .Validate(FeedServerOptions.IsValid, FeedServerOptions.ValidationFailureMessage)
            .ValidateOnStart();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<FeedStore>();

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = initialOptions.MaxMessageSizeBytes;
        });

        if (!HasConfiguredUrls(builder.Configuration))
        {
            builder.WebHost.UseUrls($"http://0.0.0.0:{initialOptions.Port}");
        }

        var app = builder.Build();

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        FeedEndpoints.Map(app);

        app.Run();
    }

    private static bool HasConfiguredUrls(IConfiguration configuration)
    {
        return !string.IsNullOrWhiteSpace(configuration["urls"])
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS"));
    }
}
