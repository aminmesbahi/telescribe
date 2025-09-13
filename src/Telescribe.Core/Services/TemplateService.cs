namespace Telescribe.Core.Services;

public class TemplateService
{
    private readonly string _templatesPath;
    private readonly Dictionary<string, string> _templateCache = new();

    public TemplateService(string templatesPath = "./templates")
    {
        _templatesPath = templatesPath;
    }

    public async Task<string> RenderTemplateAsync(string templateName, Dictionary<string, string> placeholders)
    {
        var template = await LoadTemplateAsync(templateName);
        return ReplaceePlaceholders(template, placeholders);
    }

    public async Task<string> LoadTemplateAsync(string templateName)
    {
        if (_templateCache.TryGetValue(templateName, out var cached))
        {
            return cached;
        }

        var templatePath = Path.Combine(_templatesPath, templateName);
        
        if (!File.Exists(templatePath))
        {
            throw new FileNotFoundException($"Template not found: {templatePath}");
        }

        var template = await File.ReadAllTextAsync(templatePath);
        _templateCache[templateName] = template;
        
        return template;
    }

    public string ReplaceePlaceholders(string template, Dictionary<string, string> placeholders)
    {
        var result = template;
        
        foreach (var placeholder in placeholders)
        {
            result = result.Replace($"{{{{{placeholder.Key}}}}}", placeholder.Value);
        }
        
        return result;
    }

    public void ClearCache()
    {
        _templateCache.Clear();
    }

    public async Task CopyAssetsAsync(string sourceAssetsPath, string destinationPath)
    {
        if (!Directory.Exists(sourceAssetsPath))
        {
            return;
        }

        var destinationAssetsPath = Path.Combine(destinationPath, "assets");
        Directory.CreateDirectory(destinationAssetsPath);

        foreach (var file in Directory.GetFiles(sourceAssetsPath, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceAssetsPath, file);
            var destinationFile = Path.Combine(destinationAssetsPath, relativePath);
            
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            await File.WriteAllBytesAsync(destinationFile, await File.ReadAllBytesAsync(file));
        }
    }
}