using System.Text;
using System.Text.Json;

using KSeF.Client.Core.Models.Authorization;

namespace KSeFCli;

public class TokenStore
{
    public record Data(AuthenticationOperationStatusResponse response);

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
        Log.LogInformation($"Token store loaded from: {_path}");
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

        byte[] data;
        using (FileStream fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            data = new byte[fs.Length];
            fs.ReadExactly(data);
        }

        Dictionary<string, Data> tokens;
        try
        {
            tokens = JsonSerializer.Deserialize<Dictionary<string, Data>>(data, _jsonOptions) ?? new Dictionary<string, Data>();
        }
        catch (JsonException)
        {
            Log.LogWarning($"Invalid JSON in token cache file: {_path}. Overwriting with empty data.");
            using (FileStream fs = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] emptyData = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Dictionary<string, Data>(), _jsonOptions));
                fs.Write(emptyData, 0, emptyData.Length);
                fs.Flush(true);
            }
            return null;
        }

        if (tokens.TryGetValue(key.ToCacheKey(), out Data? token))
        {
            if (token?.response is null || token.response.RefreshToken is null)
            {
                Log.LogWarning($"Invalid token data found in cache for key: {key.ToCacheKey()}. Deleting the entry.");
                tokens.Remove(key.ToCacheKey());
                using (FileStream newFs = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    byte[] newData = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(tokens, _jsonOptions));
                    newFs.Write(newData, 0, newData.Length);
                    newFs.Flush(true);
                }
                return null;
            }
            return token;
        }
        return null;
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

            System.Diagnostics.Trace.Assert(token.response is not null, "token.response is not null");
            System.Diagnostics.Trace.Assert(token.response.AccessToken is not null, "token.response.AccessToken is not null");
            System.Diagnostics.Trace.Assert(token.response.RefreshToken is not null, "token.response.RefreshToken is not null");
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
