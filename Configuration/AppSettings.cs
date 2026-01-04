using Microsoft.Extensions.Options;

namespace Malyuvach.Configuration;

public class AppSettings
{
    public const string Section = "Malyuvach";

    public ImageSettings Image { get; set; } = new();
    public LLMSettings LLM { get; set; } = new();
    public TelegramSettings Telegram { get; set; } = new();
    public DiscordSettings Discord { get; set; } = new();
    public STTSettings STT { get; set; } = new();
}

public static class AppSettingsExtensions
{
    public static IServiceCollection AddAppSettings(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(AppSettings.Section);
        services.Configure<AppSettings>(section);
        services.Configure<ImageSettings>(section.GetSection(nameof(AppSettings.Image)));
        services.Configure<LLMSettings>(section.GetSection(nameof(AppSettings.LLM)));
        services.Configure<TelegramSettings>(section.GetSection(nameof(AppSettings.Telegram)));
        services.Configure<DiscordSettings>(section.GetSection(nameof(AppSettings.Discord)));
        services.Configure<STTSettings>(section.GetSection(nameof(AppSettings.STT)));

        return services;
    }
}