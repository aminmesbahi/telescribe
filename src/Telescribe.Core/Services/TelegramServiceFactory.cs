using Telescribe.Core.Models;

namespace Telescribe.Core.Services;

public static class TelegramServiceFactory
{
    public static TelegramService CreateService(string phoneNumber, string channelId, int apiId, string apiHash, int summaryCharacterCount = 200)
    {
        var config = new TelegramConfig
        {
            PhoneNumber = phoneNumber,
            ChannelId = channelId,
            ApiId = apiId,
            ApiHash = apiHash,
            SummaryCharacterCount = summaryCharacterCount
        };

        return new TelegramService(config);
    }

    public static TelegramService CreateService(
        TelegramConfig config,
        string? exportPath = null,
        string? channelId = null,
        string? statsHistoryPath = null)
    {
        return new TelegramService(config, exportPath ?? config.ExportDirectory, channelId, statsHistoryPath);
    }
}