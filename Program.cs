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
    ConfigureAppConfiguration((context, config) =>
    {
        config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
        config.AddJsonFile($"appsettings.{context.HostingEnvironment.EnvironmentName}.json", optional: true, reloadOnChange: true);
    })
    .ConfigureServices((hostContext, services) =>
    {
        var section = hostContext.Configuration.GetSection("Malyuvach");
        services.Configure<MalyuvachOptions>(section);
        services.AddHostedService<Worker>();
    }).
    ConfigureServices((hostContext, services) =>
    {
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
