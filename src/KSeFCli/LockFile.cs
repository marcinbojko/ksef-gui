using System.Text;

namespace KSeFCli;

public class LockFile : IDisposable
{
    public FileStream Fs { get; }

    public LockFile(string path, FileAccess access)
    {
        bool locked = false;
        while (true)
        {
            try
            {
                // Determine FileMode based on FileAccess
                FileMode mode = access == FileAccess.Write ? FileMode.Create : FileMode.Open;
                // Use FileShare.Read for Read access to allow concurrent reads, 
                // but FileShare.None for Write access to ensure exclusivity.
                // Note: The previous implementation used FileShare.Read for Read and FileShare.None for Write/ReadWrite.
                FileShare share = access == FileAccess.Read ? FileShare.Read : FileShare.None;

                // Special handling for ReadWrite if needed, but the requirement specifically mentioned Read or Write.
                // If the user meant the existing usages:
                // GetToken (Read): Open, Read, Share.Read
                // GetToken (Write/Cleanup): Create, Write, Share.None
                // SetToken (Write): OpenOrCreate, ReadWrite, Share.None
                // RemoveToken (Write): OpenOrCreate, ReadWrite, Share.None

                // To support all existing cases properly, we might need more flexibility than just FileAccess in constructor
                // or map it carefully.
                // Let's look at usage in TokenStore.cs:
                // 1. GetToken (Read): new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read)
                // 2. GetToken (Write): new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.None)
                // 3. SetToken: new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
                // 4. RemoveToken: new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)

                // The prompt says: "take FileAccess.Read or write mode as constructor argument"
                // This might be simplifying too much if we need OpenOrCreate vs Create.
                
                // Let's refine the constructor to take the necessary parameters to cover all cases, 
                // or infer them intelligently. 
                
                if (access == FileAccess.Read)
                {
                     Fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                }
                else if (access == FileAccess.Write)
                {
                     // Corresponds to the "Clear invalid JSON" case
                     Fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                }
                else // ReadWrite
                {
                     // Corresponds to SetToken/RemoveToken
                     Fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                
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
