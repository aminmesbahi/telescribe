using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Telescribe.Core.Models;

namespace Telescribe.Core.Services;

public class LlmService : ILlmService, IDisposable
{
    private readonly LlmConfig _config;
    private Kernel? _kernel;
    private readonly HttpClient _httpClient;

    public LlmService(LlmConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _httpClient = new HttpClient();
    }

    public Task<bool> InitializeAsync()
    {
        try
        {
            Console.WriteLine("🤖 Initializing LLM service with provider: " + _config.Provider);

            var builder = Kernel.CreateBuilder();

            switch (_config.Provider.ToLowerInvariant())
            {
                case "openai":
                    if (string.IsNullOrEmpty(_config.ApiKey))
                    {
                        Console.WriteLine("❌ OpenAI API key is required");
                        return Task.FromResult(false);
                    }
                    builder.AddOpenAIChatCompletion(_config.ModelName, _config.ApiKey);
                    _kernel = builder.Build();
                    break;

                case "deepseek":
                    if (string.IsNullOrEmpty(_config.ApiKey))
                    {
                        Console.WriteLine("❌ DeepSeek API key is required");
                        return Task.FromResult(false);
                    }
                    break;

                case "ollama":
                    _config.BaseUrl = string.IsNullOrEmpty(_config.BaseUrl) ? "http://localhost:11434" : _config.BaseUrl;
                    break;

                default:
                    Console.WriteLine("❌ Unsupported LLM provider: " + _config.Provider);
                    return Task.FromResult(false);
            }

            Console.WriteLine("✅ LLM service initialized successfully with " + _config.Provider);
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            Console.WriteLine("❌ Failed to initialize LLM service: " + ex.Message);
            return Task.FromResult(false);
        }
    }

    public async Task<string> GenerateTitleAsync(string content)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(content))
                return string.Empty;

            var cleanContent = CleanContentForPrompt(content);
            if (string.IsNullOrWhiteSpace(cleanContent))
                return string.Empty;

            var prompt = "Generate a concise, engaging title for this social media post in " + _config.Language + ".\n" +
                        "The title should be 3-8 words, capture the main topic, and be suitable for a blog post or article.\n" +
                        "Do not include hashtags, quotes, or special formatting.\n" +
                        "Only return the title text, nothing else.\n\n" +
                        "Post content:\n" + cleanContent;

            var title = await CallLlmAsync(prompt);
            title = CleanGeneratedTitle(title);

            Console.WriteLine("📝 Generated title: '" + title + "'");
            return title;
        }
        catch (Exception ex)
        {
            Console.WriteLine("❌ Error generating title: " + ex.Message);
            return string.Empty;
        }
    }

    public async Task<List<string>> ExtractHashtagsAsync(string content, int maxHashtags = 5, List<string>? baseHashtags = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(content))
                return new List<string>();

            var cleanContent = CleanContentForPrompt(content);
            if (string.IsNullOrWhiteSpace(cleanContent))
                return new List<string>();

            var baseHashtagsText = "";
            if (baseHashtags?.Any() == true)
            {
                baseHashtagsText = "\n\nConsider these base hashtags as guidance (use them if relevant to the content):\n" +
                                  string.Join(", ", baseHashtags);
            }

            var prompt = "Extract " + Math.Min(maxHashtags, _config.MaxHashtags) + " relevant hashtags for this social media post in " + _config.Language + ".\n" +
                        "Return hashtags that are:\n" +
                        "- Relevant to the content\n" +
                        "- Popular and searchable\n" +
                        "- 1-3 words each\n" +
                        "- Without the # symbol\n" +
                        "- One hashtag per line" + baseHashtagsText + "\n\n" +
                        "Only return the hashtag words, one per line, nothing else.\n\n" +
                        "Post content:\n" + cleanContent;

            var response = await CallLlmAsync(prompt);
            var hashtags = ParseHashtagsFromResponse(response, maxHashtags);

            Console.WriteLine("📱 Extracted " + hashtags.Count + " hashtags: " + string.Join(", ", hashtags));
            return hashtags;
        }
        catch (Exception ex)
        {
            Console.WriteLine("❌ Error extracting hashtags: " + ex.Message);
            return new List<string>();
        }
    }

    public async Task<LlmProcessingSummary> ProcessPostsAsync(List<TelegramPost> posts, string outputDirectory)
    {
        var summary = new LlmProcessingSummary
        {
            TotalPosts = posts.Count,
            Provider = _config.Provider,
            ProcessingStarted = DateTime.UtcNow
        };

        Console.WriteLine("🤖 Starting LLM processing of " + posts.Count + " posts with " + _config.Provider + "...");

        Directory.CreateDirectory(outputDirectory);
        var baseHashtags = await LoadBaseHashtagsAsync();

        foreach (var post in posts)
        {
            try
            {
                Console.Write("   📝 Processing post " + post.Id + "... ");

                var hadTitle = !string.IsNullOrEmpty(post.GeneratedTitle);
                var hadHashtags = post.Hashtags.Any();

                if (_config.GenerateTitle && !hadTitle && !string.IsNullOrWhiteSpace(post.Content))
                {
                    post.GeneratedTitle = await GenerateTitleAsync(post.Content);
                }

                if (_config.ExtractHashtags && !hadHashtags && !string.IsNullOrWhiteSpace(post.Content))
                {
                    post.Hashtags = await ExtractHashtagsAsync(post.Content, _config.MaxHashtags, baseHashtags);
                }

                post.IsLlmProcessed = true;
                post.LlmProvider = _config.Provider;

                await SaveProcessedPostAsync(post, outputDirectory);

                summary.ProcessedPosts++;
                if (!hadTitle && !string.IsNullOrEmpty(post.GeneratedTitle))
                    summary.TitlesGenerated++;
                if (!hadHashtags && post.Hashtags.Any())
                    summary.HashtagsExtracted++;

                Console.WriteLine("✅");

                if (_config.ProcessingDelayMs > 0)
                {
                    await Task.Delay(_config.ProcessingDelayMs);
                }
            }
            catch (Exception ex)
            {
                summary.FailedPosts++;
                summary.Errors.Add("Post " + post.Id + ": " + ex.Message);
                Console.WriteLine("❌ " + ex.Message);
            }
        }

        summary.ProcessingCompleted = DateTime.UtcNow;
        await SaveProcessingSummaryAsync(summary, outputDirectory);

        return summary;
    }

    private async Task<List<string>> LoadBaseHashtagsAsync()
    {
        try
        {
            var baseHashtagsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "basehashtags.json");
            if (!File.Exists(baseHashtagsPath))
                return new List<string>();

            var json = await File.ReadAllTextAsync(baseHashtagsPath);
            var hashtagsData = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json);

            if (hashtagsData?.ContainsKey("baseHashtags") == true)
            {
                Console.WriteLine("📚 Loaded " + hashtagsData["baseHashtags"].Count + " base hashtags");
                return hashtagsData["baseHashtags"];
            }

            return new List<string>();
        }
        catch (Exception ex)
        {
            Console.WriteLine("⚠️ Error loading base hashtags: " + ex.Message);
            return new List<string>();
        }
    }

    private async Task SaveProcessedPostAsync(TelegramPost post, string outputDirectory)
    {
        var content = BuildProcessedMarkdownContent(post);
        var fileName = post.Id + ".md";
        var filePath = Path.Combine(outputDirectory, fileName);
        await File.WriteAllTextAsync(filePath, content);
    }

    private string BuildProcessedMarkdownContent(TelegramPost post)
    {
        var content = new StringBuilder();

        content.AppendLine("**Created:** " + post.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss") + " UTC");
        content.AppendLine("**LLM Processed:** " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + " UTC");
        content.AppendLine("**LLM Provider:** " + post.LlmProvider);

        if (!string.IsNullOrEmpty(post.GeneratedTitle))
        {
            content.AppendLine("**Generated Title:** " + post.GeneratedTitle);
        }

        if (post.Hashtags.Any())
        {
            content.AppendLine("**Hashtags:** " + string.Join(", ", post.Hashtags.Select(h => "#" + h)));
        }

        content.AppendLine("**Views:** " + post.Views);
        content.AppendLine("**Reactions:** " + post.Reactions);
        content.AppendLine("**Forwards:** " + post.TotalForwards);
        content.AppendLine();
        content.AppendLine(post.Content);

        return content.ToString();
    }

    private async Task SaveProcessingSummaryAsync(LlmProcessingSummary summary, string outputDirectory)
    {
        var summaryPath = Path.Combine(outputDirectory, "llm_processing_summary.json");
        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(summary, options);
        await File.WriteAllTextAsync(summaryPath, json);
    }

    private async Task<string> CallLlmAsync(string prompt)
    {
        switch (_config.Provider.ToLowerInvariant())
        {
            case "ollama":
                return await CallOllamaAsync(prompt);
            case "openai":
                return await CallSemanticKernelAsync(prompt);
            case "deepseek":
                return await CallDeepSeekAsync(prompt);
            default:
                throw new NotSupportedException("Provider " + _config.Provider + " is not supported");
        }
    }

    private async Task<string> CallSemanticKernelAsync(string prompt)
    {
        if (_kernel == null)
            throw new InvalidOperationException("Semantic Kernel not initialized");

        var executionSettings = new OpenAIPromptExecutionSettings
        {
            MaxTokens = _config.MaxTokens,
            Temperature = _config.Temperature
        };

        var result = await _kernel.InvokePromptAsync(prompt, new KernelArguments(executionSettings));
        return result.GetValue<string>() ?? string.Empty;
    }

    private async Task<string> CallOllamaAsync(string prompt)
    {
        try
        {
            var requestBody = new
            {
                model = _config.ModelName,
                prompt,
                stream = false,
                options = new
                {
                    temperature = _config.Temperature,
                    num_predict = _config.MaxTokens
                }
            };

            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(_config.BaseUrl + "/api/generate", content);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(responseJson);

            return document.RootElement.GetProperty("response").GetString() ?? string.Empty;
        }
        catch (Exception ex)
        {
            throw new Exception("Ollama API call failed: " + ex.Message, ex);
        }
    }

    private async Task<string> CallDeepSeekAsync(string prompt)
    {
        try
        {
            var baseUrl = string.IsNullOrEmpty(_config.BaseUrl) ? "https://api.deepseek.com/v1" : _config.BaseUrl;

            var requestBody = new
            {
                model = _config.ModelName,
                messages = new[]
                {
                    new { role = "user", content = prompt }
                },
                max_tokens = _config.MaxTokens,
                temperature = _config.Temperature,
                stream = false
            };

            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("Authorization", "Bearer " + _config.ApiKey);
            client.DefaultRequestHeaders.Add("User-Agent", "Telescribe/1.0");

            var response = await client.PostAsync(baseUrl + "/chat/completions", content);
            var responseContent = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception("DeepSeek API call failed: " + response.StatusCode + " - " + responseContent);
            }

            using var document = JsonDocument.Parse(responseContent);
            var choices = document.RootElement.GetProperty("choices");
            if (choices.GetArrayLength() > 0)
            {
                var message = choices[0].GetProperty("message");
                return message.GetProperty("content").GetString() ?? string.Empty;
            }

            return string.Empty;
        }
        catch (Exception ex)
        {
            throw new Exception("DeepSeek API call failed: " + ex.Message, ex);
        }
    }

    private string CleanContentForPrompt(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return string.Empty;

        content = Regex.Replace(content, @"!\[([^\]]*)\]\([^)]+\)", "");
        content = Regex.Replace(content, @"\[([^\]]+)\]\([^)]+\)", "$1");
        content = Regex.Replace(content, @"\*\*([^*]+)\*\*", "$1");
        content = Regex.Replace(content, @"\*([^*]+)\*", "$1");
        content = Regex.Replace(content, @"#{1,6}\s*", "");
        content = Regex.Replace(content, @"\s+", " ");
        content = content.Trim();

        if (content.Length > 1000)
        {
            content = content[..1000] + "...";
        }

        return content;
    }

    private string CleanGeneratedTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;

        title = title.Trim('"', '\'', ' ');
        title = Regex.Replace(title, @"^(Title:\s*|Post:\s*)", "", RegexOptions.IgnoreCase);
        title = Regex.Replace(title, @"#\w+", "");
        title = title.Trim();

        if (title.Length > 100)
        {
            title = title[..100] + "...";
        }

        return title;
    }

    private List<string> ParseHashtagsFromResponse(string response, int maxHashtags)
    {
        var hashtags = new List<string>();

        if (string.IsNullOrWhiteSpace(response))
            return hashtags;

        var lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        hashtags.AddRange(lines.Take(maxHashtags)
            .Select(line => line.Trim()
                .Replace("#", "")
                .Replace("-", "")
                .Replace("*", "")
                .Trim())
            .Where(hashtag => !string.IsNullOrWhiteSpace(hashtag) &&
                             hashtag.Length <= 30 &&
                             !hashtag.Contains(' ') &&
                             Regex.IsMatch(hashtag, @"^[a-zA-Z0-9_]+$")));

        return hashtags.Distinct().Take(maxHashtags).ToList();
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
