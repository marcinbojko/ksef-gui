using System.Text;
using System.Text.Json;
using KSeF.Client.Core.Models.Authorization;

namespace KSeFCli;

public class TokenStore
{
    public class Data : AuthenticationOperationStatusResponse;

    public record Key(string Nazwa, string Nip, string Environment);

    private readonly string _path;

    public TokenStore(string path)
    {
        _path = Environment.ExpandEnvironmentVariables(path);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
    }

    public static TokenStore Default()
    {
        string defaultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache", "ksefcli", "tokenstore.json");
        return new TokenStore(defaultPath);
    }

    public Data? GetToken(Key key)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        using FileStream fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None);
        byte[] data = new byte[fs.Length];
        fs.ReadExactly(data);

        Dictionary<Key, Data> tokens = JsonSerializer.Deserialize<Dictionary<Key, Data>>(data)
                    ?? new Dictionary<Key, Data>();
        tokens.TryGetValue(key, out Data? token);
        return token;
    }

    public void SetToken(Key key, Data token)
    {
        // wyłączny dostęp do pliku przy zapisie, aby chronić przed współbieżnym dostępem.
        using (FileStream fs = new FileStream(this._path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Dictionary<Key, Data> tokens;
            // odczyt aktualnego stanu
            if (fs.Length > 0)
            {
                byte[] data = new byte[fs.Length];
                fs.ReadExactly(data);
                tokens = JsonSerializer.Deserialize<Dictionary<Key, Data>>(data)
                         ?? new Dictionary<Key, Data>();
            }
            else
            {
                tokens = new Dictionary<Key, Data>();
            }

            // modyfikacja
            tokens[key] = token;

            // zapis
            fs.Seek(0, SeekOrigin.Begin);
            fs.SetLength(0);
            byte[] newData = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(tokens, new JsonSerializerOptions { WriteIndented = true }));
            fs.Write(newData, 0, newData.Length);
            fs.Flush(true);
        }

    }

    public bool RemoveToken(Key key)
    {
        // wyłączny dostęp do pliku przy zapisie, aby chronić przed współbieżnym dostępem.
        using (FileStream fs = new FileStream(this._path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Dictionary<Key, Data> tokens;
            if (fs.Length > 0)
            {
                byte[] data = new byte[fs.Length];
                fs.ReadExactly(data);
                tokens = JsonSerializer.Deserialize<Dictionary<Key, Data>>(data)
                         ?? new Dictionary<Key, Data>();
            }
            else
            {
                return false; // File is empty, nothing to remove
            }

            if (tokens.Remove(key))
            {
                // Zapisz zmieniony stan
                fs.Seek(0, SeekOrigin.Begin);
                fs.SetLength(0);
                byte[] newData = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(tokens, new JsonSerializerOptions { WriteIndented = true }));
                fs.Write(newData, 0, newData.Length);
                fs.Flush(true);
                return true;
            }
            return false;
        }
    }
}
