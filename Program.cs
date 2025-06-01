using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Malyuvach.Configuration;
using Malyuvach.Services;
using Malyuvach.Services.Image;
using Malyuvach.Services.LLM;
using Malyuvach.Services.STT;
using Malyuvach.Services.Telegram;
using Serilog;

var host = Host.CreateDefaultBuilder(args)
    .UseSerilog((context, services, configuration) =>
    {
        configuration.ReadFrom.Configuration(context.Configuration);
    })
    .ConfigureAppConfiguration((context, config) =>
    {
        config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
        config.AddJsonFile($"appsettings.{context.HostingEnvironment.EnvironmentName}.json", optional: true, reloadOnChange: true);
    })
    .ConfigureServices((hostContext, services) =>
    {
        // Configure settings
        services.AddAppSettings(hostContext.Configuration);

        // Configure JSON serialization
        services.Configure<JsonSerializerOptions>(options =>
        {
            options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.WriteIndented = true;
            options.ReadCommentHandling = JsonCommentHandling.Skip;
            options.AllowTrailingCommas = true;
            options.Encoder = JavaScriptEncoder.Create(UnicodeRanges.BasicLatin, UnicodeRanges.Cyrillic);
        });

        // Register services
        services.AddTransient<HttpClient>();
        services.AddSingleton<ILLMService, LLMService>();
        services.AddSingleton<IImageService, ImageService>();
        services.AddSingleton<ISTTService, STTService>();
        services.AddSingleton<ITelegramService, TelegramService>();

        // Configure hosted service
        services.AddHostedService<BotWorker>();
    })
    .Build();

await host.RunAsync();
