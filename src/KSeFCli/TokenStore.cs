using System.Text;
using System.Text.Json;

using KSeF.Client.Core.Models.Authorization;

namespace KSeFCli;

public class TokenStore
{
    public record Data
    {
        public AuthenticationOperationStatusResponse response;
    }

    public record Key(string Nazwa, string Nip, string Environment)
    {
        public string ToCacheKey() => $"{Nip}_{Environment}_{Nazwa}";
    }

    private readonly string _path;
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

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

        try
        {
            var tokens = JsonSerializer.Deserialize<Dictionary<string, Data>>(data, _jsonOptions) ?? new Dictionary<string, Data>();
            tokens.TryGetValue(key.ToCacheKey(), out Data? token);
            return token;
        }
        catch (JsonException)
        {
            Log.LogWarning($"Invalid JSON in token cache file: {_path}. Deleting the file.");
            File.Delete(_path);
            return null;
        }
    }

    public void SetToken(Key key, Data token)
    {
        using (FileStream fs = new FileStream(this._path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Dictionary<string, Data> tokens;
            if (fs.Length > 0)
            {
                byte[] data = new byte[fs.Length];
                fs.ReadExactly(data);
                tokens = JsonSerializer.Deserialize<Dictionary<string, Data>>(data, _jsonOptions) ?? new Dictionary<string, Data>();
            }
            else
            {
                tokens = new Dictionary<string, Data>();
            }

            tokens[key.ToCacheKey()] = token;

            fs.Seek(0, SeekOrigin.Begin);
            fs.SetLength(0);
            byte[] newData = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(tokens, _jsonOptions));
            fs.Write(newData, 0, newData.Length);
            fs.Flush(true);
        }
    }

    public bool RemoveToken(Key key)
    {
        using (FileStream fs = new FileStream(this._path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Dictionary<string, Data> tokens;
            if (fs.Length > 0)
            {
                byte[] data = new byte[fs.Length];
                fs.ReadExactly(data);
                tokens = JsonSerializer.Deserialize<Dictionary<string, Data>>(data, _jsonOptions) ?? new Dictionary<string, Data>();
            }
            else
            {
                return false;
            }

            if (tokens.Remove(key.ToCacheKey()))
            {
                fs.Seek(0, SeekOrigin.Begin);
                fs.SetLength(0);
                byte[] newData = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(tokens, _jsonOptions));
                fs.Write(newData, 0, newData.Length);
                fs.Flush(true);
                return true;
            }
            return false;
        }
    }
}
