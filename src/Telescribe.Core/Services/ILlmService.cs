namespace Telescribe.Core.Services;

public interface ILlmService : IDisposable
{
    string Provider { get; }

    Task<bool> InitializeAsync();

    Task<string> GenerateTitleAsync(string content, CancellationToken cancellationToken = default);

    Task<List<string>> ExtractHashtagsAsync(string content, int maxHashtags = 5, List<string>? baseHashtags = null, CancellationToken cancellationToken = default);
}
