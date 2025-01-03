using System.Text.Json;
using System.Text.Encodings.Web;
using Malyuvach;
using System.Text.Unicode;
using Serilog;

var host = Host.CreateDefaultBuilder(args).
    UseSerilog((context, services, configuration) =>
    {
        configuration.ReadFrom.Configuration(context.Configuration);
    }).
    ConfigureServices(services =>
    {
        services.AddHostedService<Worker>();
        services.Configure<JsonSerializerOptions>(options =>
        {
            options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.WriteIndented = true;
            options.ReadCommentHandling = JsonCommentHandling.Skip;
            options.AllowTrailingCommas = true;
            options.Encoder = JavaScriptEncoder.Create(UnicodeRanges.BasicLatin, UnicodeRanges.Cyrillic);
        });
    })
    .Build();

await host.RunAsync();
