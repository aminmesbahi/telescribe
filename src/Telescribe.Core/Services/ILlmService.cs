using Telescribe.Core.Models;

namespace Telescribe.Core.Services;

public interface ILlmService : IDisposable
{
    Task<bool> InitializeAsync();
    Task<string> GenerateTitleAsync(string content);
    Task<List<string>> ExtractHashtagsAsync(string content, int maxHashtags = 5, List<string>? baseHashtags = null);
    Task<LlmProcessingSummary> ProcessPostsAsync(List<TelegramPost> posts, string outputDirectory);
}