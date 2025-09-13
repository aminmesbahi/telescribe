using System.Text.Json;
using Telescribe.Core.Models;

namespace Telescribe.Core.Services;

public class LlmPostProcessor : IDisposable
{
    private readonly LlmConfig _config;
    private readonly string _outputDirectory;
    private readonly TemplateService _templateService;
    private LlmService? _llmService;

    public LlmPostProcessor(LlmConfig config, string outputDirectory = "./llm-output")
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _outputDirectory = outputDirectory;
        _templateService = new TemplateService("./templates");
    }

    public async Task<bool> ProcessExportedPostsAsync()
    {
        if (!_config.EnableProcessing)
        {
            Console.WriteLine("LLM Processing is disabled");
            return false;
        }
        
        return true;
    }

    public void Dispose()
    {
        _llmService?.Dispose();
    }
}
