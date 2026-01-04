using Malyuvach.Services.Discord;

namespace Malyuvach.Services;

public class DiscordWorker : BackgroundService
{
    private readonly IDiscordService _discordService;
    private readonly ILogger<DiscordWorker> _logger;

    public DiscordWorker(
        IDiscordService discordService,
        ILogger<DiscordWorker> logger)
    {
        _discordService = discordService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _discordService.StartAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(1000, stoppingToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in Discord worker");
            throw;
        }
        finally
        {
            await _discordService.StopAsync(stoppingToken);
        }
    }
}
