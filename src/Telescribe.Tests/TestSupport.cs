namespace Telescribe.Tests;

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "telescribe-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; the OS temp folder gets cleaned eventually.
        }
    }
}

internal static class Templates
{
    /// <summary>Path of a template folder copied next to the test assembly (see the csproj).</summary>
    public static string Path(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "templates", name);
}
