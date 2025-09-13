namespace Telescribe.Core.Models;

public class LlmConfig
{
    public bool EnableProcessing { get; set; } = false;
    public string Provider { get; set; } = "OpenAI"; // OpenAI, DeepSeek, Ollama
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty; // For Ollama or custom endpoints
    public string ModelName { get; set; } = "gpt-5-nano"; // Model to use
    public bool GenerateTitle { get; set; } = true;
    public bool ExtractHashtags { get; set; } = true;
    public int MaxHashtags { get; set; } = 5;
    public string Language { get; set; } = "English"; // Language for processing
    public int MaxTokens { get; set; } = 200;
    public double Temperature { get; set; } = 0.7;
    public int TimeoutSeconds { get; set; } = 30;
    public int MaxRetries { get; set; } = 3;
    public int ProcessingDelayMs { get; set; } = 1000; // Delay between LLM calls
}