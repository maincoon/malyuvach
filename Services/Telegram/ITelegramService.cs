using Telegram.Bot;
using Telegram.Bot.Types;

namespace Malyuvach.Services.Telegram;

public interface ITelegramService
{
    Task StartAsync(CancellationToken cancellationToken);
    Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken);
    Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken);
}