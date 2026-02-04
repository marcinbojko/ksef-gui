using System.Text;
using System.Text.Json;

using KSeF.Client.Core.Models.Authorization;

namespace KSeFCli;

public class TokenStore
{
    public record Data(AuthenticationOperationStatusResponse Response);

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

        byte[] data = Array.Empty<byte>();
        bool locked = false;
        while (true)
        {
            try
            {
                using (FileStream fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    data = new byte[fs.Length];
                    fs.ReadExactly(data);
                }
                break;
            }
            catch (IOException)
            {
                if (!locked)
                {
                    Log.LogInformation($"Waiting for lock on {_path}...");
                    locked = true;
                }
                Thread.Sleep(1000);
            }
        }

        Dictionary<string, Data> tokens;
        try
        {
            tokens = JsonSerializer.Deserialize<Dictionary<string, Data>>(data, _jsonOptions) ?? new Dictionary<string, Data>();
        }
        catch (JsonException)
        {
            Log.LogWarning($"Invalid JSON in token cache file: {_path}. Overwriting with empty data.");
            locked = false;
            while (true)
            {
                try
                {
                    using (FileStream fs = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        byte[] emptyData = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Dictionary<string, Data>(), _jsonOptions));
                        fs.Write(emptyData, 0, emptyData.Length);
                        fs.Flush(true);
                    }
                    break;
                }
                catch (IOException)
                {
                    if (!locked)
                    {
                        Log.LogInformation($"Waiting for lock on {_path}...");
                        locked = true;
                    }
                    Thread.Sleep(1000);
                }
            }
            return null;
        }

        if (tokens.TryGetValue(key.ToCacheKey(), out Data? token))
        {
            string invalidReason = token?.Response is null ? "Response is null" :
                                   token.Response.RefreshToken is null ? "RefreshToken is null" :
                                   token.Response.AccessToken is null ? "AccessToken is null" : "";

            if (!string.IsNullOrEmpty(invalidReason))
            {
                Log.LogWarning($"Invalid token data found in cache for key: {key.ToCacheKey()} (reason: {invalidReason}). Deleting the entry.");
                tokens.Remove(key.ToCacheKey());
                locked = false;
                while (true)
                {
                    try
                    {
                        using (FileStream newFs = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            byte[] newData = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(tokens, _jsonOptions));
                            newFs.Write(newData, 0, newData.Length);
                            newFs.Flush(true);
                        }
                        break;
                    }
                    catch (IOException)
                    {
                        if (!locked)
                        {
                            Log.LogInformation($"Waiting for lock on {_path}...");
                            locked = true;
                        }
                        Thread.Sleep(1000);
                    }
                }
                return null;
            }
            return token;
        }
        return null;
    }

    public void SetToken(Key key, Data token)
    {
        bool locked = false;
        while (true)
        {
            try
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

                    System.Diagnostics.Trace.Assert(token.Response is not null, "token.response is not null");
                    System.Diagnostics.Trace.Assert(token.Response.AccessToken is not null, "token.response.AccessToken is not null");
                    System.Diagnostics.Trace.Assert(token.Response.RefreshToken is not null, "token.response.RefreshToken is not null");
                    tokens[key.ToCacheKey()] = token;

                    fs.Seek(0, SeekOrigin.Begin);
                    fs.SetLength(0);
                    byte[] newData = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(tokens, _jsonOptions));
                    fs.Write(newData, 0, newData.Length);
                    fs.Flush(true);
                }
                break;
            }
            catch (IOException)
            {
                if (!locked)
                {
                    Log.LogInformation($"Waiting for lock on {_path}...");
                    locked = true;
                }
                Thread.Sleep(1000);
            }
        }
    }

    public bool RemoveToken(Key key)
    {
        bool locked = false;
        while (true)
        {
            try
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
            catch (IOException)
            {
                if (!locked)
                {
                    Log.LogInformation($"Waiting for lock on {_path}...");
                    locked = true;
                }
                Thread.Sleep(1000);
            }
        }
    }
}
