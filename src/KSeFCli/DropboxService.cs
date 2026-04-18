using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KSeFCli;

internal static class DropboxService
{
    private static readonly HttpClient Http = new();

    public static string GenerateCodeVerifier()
    {
        byte[] bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncode(bytes);
    }

    public static string ComputeCodeChallenge(string verifier)
    {
        byte[] hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64UrlEncode(hash);
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static async Task<(string AccessToken, string RefreshToken)> ExchangeCodeAsync(
        string appKey, string code, string verifier, string redirectUri, CancellationToken ct = default)
    {
        FormUrlEncodedContent form = new(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["client_id"] = appKey,
            ["redirect_uri"] = redirectUri,
        });
        HttpResponseMessage resp = await Http.PostAsync("https://api.dropboxapi.com/oauth2/token", form, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return (
            doc.RootElement.GetProperty("access_token").GetString()!,
            doc.RootElement.GetProperty("refresh_token").GetString()!
        );
    }

    public static async Task<string> RefreshAccessTokenAsync(string appKey, string refreshToken, CancellationToken ct = default)
    {
        FormUrlEncodedContent form = new(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = appKey,
        });
        HttpResponseMessage resp = await Http.PostAsync("https://api.dropboxapi.com/oauth2/token", form, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return doc.RootElement.GetProperty("access_token").GetString()!;
    }

    public static async Task UploadFileAsync(string accessToken, string dropboxPath, byte[] content, CancellationToken ct = default)
    {
        using HttpRequestMessage req = new(HttpMethod.Post, "https://content.dropboxapi.com/2/files/upload");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Headers.Add("Dropbox-API-Arg", JsonSerializer.Serialize(new
        {
            path = dropboxPath,
            mode = "overwrite",
            autorename = false,
            mute = false
        }));
        req.Content = new ByteArrayContent(content);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        HttpResponseMessage resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    public static async Task<List<string>> ListFoldersAsync(string accessToken, string path, CancellationToken ct = default)
    {
        using HttpRequestMessage req = new(HttpMethod.Post, "https://api.dropboxapi.com/2/files/list_folder");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Content = new StringContent(
            JsonSerializer.Serialize(new { path, recursive = false }),
            Encoding.UTF8, "application/json");
        HttpResponseMessage resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        List<string> folders = new();
        foreach (JsonElement entry in doc.RootElement.GetProperty("entries").EnumerateArray())
        {
            if (entry.GetProperty(".tag").GetString() == "folder")
            {
                folders.Add(entry.GetProperty("path_display").GetString()!);
            }
        }
        return folders;
    }

    public static async Task EnsureFolderAsync(string accessToken, string path, CancellationToken ct = default)
    {
        using HttpRequestMessage req = new(HttpMethod.Post, "https://api.dropboxapi.com/2/files/create_folder_v2");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Content = new StringContent(
            JsonSerializer.Serialize(new { path, autorename = false }),
            Encoding.UTF8, "application/json");
        HttpResponseMessage resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        // 409 Conflict means the folder already exists — that is fine
        if (resp.StatusCode != System.Net.HttpStatusCode.Conflict)
        {
            resp.EnsureSuccessStatusCode();
        }
    }

    public static async Task<string> GetAccountEmailAsync(string accessToken, CancellationToken ct = default)
    {
        using HttpRequestMessage req = new(HttpMethod.Post, "https://api.dropboxapi.com/2/users/get_current_account");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Content = new StringContent("null", Encoding.UTF8, "application/json");
        HttpResponseMessage resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using JsonDocument doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return doc.RootElement.GetProperty("email").GetString() ?? "unknown";
    }
}
