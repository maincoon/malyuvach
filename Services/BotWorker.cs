using Malyuvach.Services.Telegram;

namespace Malyuvach.Services;

public class BotWorker : BackgroundService
{
    private readonly ITelegramService _telegramService;
    private readonly ILogger<BotWorker> _logger;

    public BotWorker(
        ITelegramService telegramService,
        ILogger<BotWorker> logger)
    {
        _telegramService = telegramService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _telegramService.StartAsync(stoppingToken);

            // Keep the service running
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(1000, stoppingToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in bot worker");
            throw;
        }
    }
}