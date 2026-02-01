public sealed class TemporaryFile : IDisposable
{
    public string Path { get; }

    public TemporaryFile(string? desiredPath = null, string prefix = "", string extension = "")
    {
        if (!string.IsNullOrEmpty(desiredPath))
        {
            Path = desiredPath;
            // ensure file exists
            using (File.Create(Path)) { }
        }
        else
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + System.IO.Path.GetRandomFileName() + extension);
            using (File.Create(Path)) { }
        }
    }

    public static implicit operator string(TemporaryFile tmp) => tmp.Path;

    public void Dispose()
    {
        try
        {
            if (File.Exists(Path))
                File.Delete(Path);
        }
        catch
        {
            // ignore cleanup failures
        }
    }
}
