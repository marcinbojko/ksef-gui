using System.Text;

namespace KSeFCli;

public class LockFile : IDisposable
{
    public FileStream Fs { get; }

    public LockFile(string path, FileMode mode, FileAccess access, FileShare share)
    {
        bool locked = false;
        while (true)
        {
            try
            {
                Fs = new FileStream(path, mode, access, share);
                break;
            }
            catch (IOException)
            {
                if (!locked)
                {
                    Log.LogInformation($"Waiting for lock on {path}...");
                    locked = true;
                }
                Thread.Sleep(1000);
            }
        }
    }

    public void Dispose()
    {
        Fs?.Dispose();
        GC.SuppressFinalize(this);
    }
}
