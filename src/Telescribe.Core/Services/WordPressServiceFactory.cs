using Telescribe.Core.Models;

namespace Telescribe.Core.Services;

public static class WordPressServiceFactory
{
    public static IWordPressService CreateService(WordPressConfig config, string exportDirectory = "./exports")
    {
        return new WordPressService(config, exportDirectory);
    }
}
