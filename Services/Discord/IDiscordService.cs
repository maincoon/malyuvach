namespace Malyuvach.Services.Discord;

public interface IDiscordService
{
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}
