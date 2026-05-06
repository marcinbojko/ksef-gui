using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using KSeF.Client.Api.Services;
using KSeF.Client.Core.Exceptions;

namespace KSeFCli;

internal record SearchParams(string SubjectType, string From, string? To, string DateType, string? Source = null);
internal record DownloadParams(string OutputDir, int[]? SelectedIndices, bool CustomFilenames, bool ExportXml = true, bool ExportJson = false, bool ExportPdf = true, bool SeparateByNip = false, string? PdfColorScheme = null, string? SubjectType = null);
internal record CheckExistingParams(string OutputDir, bool CustomFilenames, bool SeparateByNip, string? SubjectType = null);
internal record DownloadSummaryParams(string OutputDir, string Month, bool SeparateByNip = false);
internal record BrowserDownloadParams(int[]? Indices, string? PdfColorScheme = null, string? Month = null, bool CustomFilenames = false);

internal sealed class WebProgressServer : IDisposable
{
    private static readonly HttpClient _wlHttpClient = CreateWlHttpClient();

    private static HttpClient CreateWlHttpClient()
    {
        HttpClient c = new() { Timeout = TimeSpan.FromSeconds(10) };
        c.DefaultRequestHeaders.Add("Accept", "application/json");
        return c;
    }

    private readonly HttpListener _listener = new();
    private readonly List<StreamWriter> _sseClients = new();
    private readonly Lock _clientsLock = new();
    private CancellationTokenSource? _cts;
    public int Port { get; }

    /// <summary>Called when the user clicks "Szukaj". Receives search params, returns JSON-serializable result.</summary>
    public Func<SearchParams, CancellationToken, Task<object>>? OnSearch { get; set; }

    /// <summary>Called when the user clicks "Pobierz". Receives download params. Progress reported via SendEventAsync.</summary>
    public Func<DownloadParams, CancellationToken, Task>? OnDownload { get; set; }

    /// <summary>Called when the user requests a monthly summary CSV. Returns the output file path.</summary>
    public Func<DownloadSummaryParams, CancellationToken, Task<string>>? OnDownloadSummary { get; set; }

    /// <summary>Called when the user requests a monthly summary CSV for browser download. Returns (stream, contentType, fileName).</summary>
    public Func<DownloadSummaryParams, CancellationToken, Task<(System.IO.Stream Data, string ContentType, string FileName)>>? OnBrowserDownloadSummary { get; set; }

    /// <summary>Called when the user requests ad-hoc browser download. Returns (stream, contentType, fileName). Caller disposes the stream.</summary>
    public Func<BrowserDownloadParams, CancellationToken, Task<(System.IO.Stream Data, string ContentType, string FileName)>>? OnBrowserDownload { get; set; }

    /// <summary>Called when the user clicks "Autoryzuj". Forces re-authentication and returns status message.</summary>
    public Func<CancellationToken, Task<string>>? OnAuth { get; set; }

    /// <summary>Called when user requests invoice details. Receives invoice index, returns JSON-serializable detail object.</summary>
    public Func<int, CancellationToken, Task<object>>? OnInvoiceDetails { get; set; }

    /// <summary>Called to check which invoices already exist as files in the output directory.</summary>
    public Func<CheckExistingParams, Task<object>>? OnCheckExisting { get; set; }

    /// <summary>Returns the last cached invoice list and search params for the current profile.
    /// Used to pre-populate the table on page load without requiring a search.</summary>
    public Func<Task<object>>? OnCachedInvoices { get; set; }

    /// <summary>Called when the user clicks "Zakoncz". Shuts down the server.</summary>
    public Action? OnQuit { get; set; }

    /// <summary>Called to get current token expiry status. Returns JSON-serializable object with token validity info.</summary>
    public Func<Task<object>>? OnTokenStatus { get; set; }

    /// <summary>Called on page load to retrieve saved GUI preferences (returns JSON-serializable object).</summary>
    public Func<Task<object>>? OnLoadPrefs { get; set; }

    /// <summary>Called to persist GUI preferences (receives JSON string with all prefs).</summary>
    public Func<string, Task>? OnSavePrefs { get; set; }

    /// <summary>Called to load the current config file for the editor. Returns ConfigEditorData.</summary>
    public Func<Task<object>>? OnLoadConfig { get; set; }

    /// <summary>Called when user opens the About dialog. Returns version, build date, author, GitHub link.</summary>
    public Func<Task<object>>? OnAbout { get; set; }

    /// <summary>Called to save modified config. Receives JSON string, returns empty string on success or error message.</summary>
    public Func<string, Task<string>>? OnSaveConfig { get; set; }

    /// <summary>Called when user triggers "test notification". Receives profile name, fires webhook(s) if configured.
    /// Returns empty string on success or a description of what was sent.</summary>
    public Func<string, CancellationToken, Task<string>>? OnTestNotification { get; set; }

    /// <summary>Called when user triggers a test email from the Email preferences tab.
    /// Receives the recipient address, sends a test message using saved SMTP settings.
    /// Returns a result message string.</summary>
    public Func<string, CancellationToken, Task<string>>? OnTestEmail { get; set; }

    /// <summary>Called to fetch notification delivery status per profile.
    /// Returns an object keyed by profile name with last-sent, pending-retry, and error state.</summary>
    public Func<Task<object>>? OnNotificationStatus { get; set; }

    public bool Lan { get; }

    /// <summary>
    /// Restricts the /mkdir endpoint to this directory tree.
    /// Set to the configured output directory so users cannot create directories
    /// outside the data area. Defaults to the filesystem root (unrestricted).
    /// </summary>
    public string MkdirRoot { get; init; } = "/";

    /// <summary>
    /// Directory shown when the file-system picker opens with no prior path.
    /// Defaults to the user's home directory.
    /// </summary>
    public string DefaultBrowseDir { get; init; } =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public WebProgressServer(bool lan = false, int port = 0)
    {
        Lan = lan;
        Port = port > 0 ? port : GetRandomPort();
        string host = lan ? "+" : "localhost";
        _listener.Prefixes.Add($"http://{host}:{Port}/");
    }


    public void Start(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener.Start();
        _ = AcceptLoop(_cts.Token);
    }

    public async Task SendEventAsync(string type, object? data = null)
    {
        string json = JsonSerializer.Serialize(new { type, data });
        string message = $"data: {json}\n\n";

        List<StreamWriter> snapshot;
        lock (_clientsLock)
        {
            snapshot = new List<StreamWriter>(_sseClients);
        }

        List<StreamWriter> dead = new();
        foreach (StreamWriter client in snapshot)
        {
            try
            {
                await client.WriteAsync(message).ConfigureAwait(false);
                await client.FlushAsync().ConfigureAwait(false);
            }
            catch
            {
                dead.Add(client);
            }
        }

        if (dead.Count > 0)
        {
            lock (_clientsLock)
            {
                foreach (StreamWriter d in dead)
                {
                    _sseClients.Remove(d);
                }
            }
        }
    }

    public string LocalUrl => $"http://localhost:{Port}/";

    public void OpenBrowser()
    {
        string url = LocalUrl;
        try
        {
            if (OperatingSystem.IsLinux())
            {
                System.Diagnostics.Process.Start("xdg-open", url);
            }
            else if (OperatingSystem.IsMacOS())
            {
                System.Diagnostics.Process.Start("open", url);
            }
            else if (OperatingSystem.IsWindows())
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd", $"/c start {url}") { CreateNoWindow = true });
            }
        }
        catch
        {
            Console.WriteLine($"Open browser at: {url}");
        }
    }

    public static void ShowErrorPage(string title, string detail, string? dbPath = null)
    {
        string safeTitle = System.Net.WebUtility.HtmlEncode(title);
        string safeDetail = System.Net.WebUtility.HtmlEncode(detail);
        string safeDbPath = System.Net.WebUtility.HtmlEncode(dbPath ?? "~/.cache/ksefcli/db/invoice-cache.db");
        string html = $$"""
            <!DOCTYPE html>
            <html lang="pl">
            <head>
              <meta charset="utf-8">
              <title>{{safeTitle}}</title>
              <style>
                body { font-family: sans-serif; max-width: 720px; margin: 4rem auto; padding: 0 1.5rem; background: #fafafa; color: #212121; }
                h1 { color: #b71c1c; }
                pre { background: #fff3e0; border: 1px solid #ffcc80; padding: 1rem; border-radius: 4px; white-space: pre-wrap; word-break: break-word; font-size: .85rem; }
                .hint { border-left: 4px solid #e53935; padding: .75rem 1rem; background: #fff; margin-top: 1.5rem; }
              </style>
            </head>
            <body>
              <h1>⚠ {{safeTitle}}</h1>
              <pre>{{safeDetail}}</pre>
              <div class="hint">
                <strong>Co zrobić:</strong> Usuń lub przywróć plik bazy danych i uruchom aplikację ponownie.<br>
                Plik bazy: <code>{{safeDbPath}}</code>
              </div>
            </body>
            </html>
            """;
        string path = Path.Join(Path.GetTempPath(), "ksefcli-error.html");
        try
        {
            File.WriteAllText(path, html, Encoding.UTF8);
            string fileUrl = "file:///" + path.Replace('\\', '/').TrimStart('/');
            if (OperatingSystem.IsLinux())
            {
                System.Diagnostics.Process.Start("xdg-open", fileUrl);
            }
            else if (OperatingSystem.IsMacOS())
            {
                System.Diagnostics.Process.Start("open", path);
            }
            else if (OperatingSystem.IsWindows())
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd", $"/c start \"\" \"{path}\"") { CreateNoWindow = true });
            }
        }
        catch (IOException ex) { Log.LogDebug($"[error-page] {ex.Message}"); }
        catch (UnauthorizedAccessException ex) { Log.LogDebug($"[error-page] {ex.Message}"); }
        catch (System.ComponentModel.Win32Exception ex) { Log.LogDebug($"[error-page] {ex.Message}"); }
        catch (InvalidOperationException ex) { Log.LogDebug($"[error-page] {ex.Message}"); }
        catch (PlatformNotSupportedException ex) { Log.LogDebug($"[error-page] {ex.Message}"); }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        lock (_clientsLock)
        {
            foreach (StreamWriter client in _sseClients)
            {
                try { client.Dispose(); } catch { }
            }
            _sseClients.Clear();
        }
        try { _listener.Stop(); } catch { }
        _listener.Close();
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                HttpListenerContext ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                _ = HandleRequest(ctx, ct);
            }
            catch (ObjectDisposedException) { break; }
            catch (HttpListenerException) { break; }
        }
    }

    private async Task HandleRequest(HttpListenerContext ctx, CancellationToken ct)
    {
        string path = ctx.Request.Url?.AbsolutePath ?? "/";
        string method = ctx.Request.HttpMethod;

        if (path == "/events")
        {
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers.Add("Cache-Control", "no-cache");
            ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
            ctx.Response.StatusCode = 200;

            StreamWriter writer = new StreamWriter(ctx.Response.OutputStream, Encoding.UTF8) { AutoFlush = false };
            lock (_clientsLock)
            {
                _sseClients.Add(writer);
            }

            try
            {
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            finally
            {
                lock (_clientsLock) { _sseClients.Remove(writer); }
                try { writer.Dispose(); } catch { }
                try { ctx.Response.Close(); } catch { }
            }
        }
        else if (path == "/prefs" && method == "GET")
        {
            await HandleAction(ctx, ct, async () =>
            {
                if (OnLoadPrefs == null)
                {
                    return JsonSerializer.Serialize(new { });
                }

                object prefs = await OnLoadPrefs().ConfigureAwait(false);
                return JsonSerializer.Serialize(prefs);
            }).ConfigureAwait(false);
        }
        else if (path == "/prefs" && method == "POST")
        {
            await HandleAction(ctx, ct, async () =>
            {
                using StreamReader bodyReader = new(ctx.Request.InputStream, ctx.Request.ContentEncoding);
                string body = await bodyReader.ReadToEndAsync(ct).ConfigureAwait(false);
                if (OnSavePrefs != null)
                {
                    await OnSavePrefs(body).ConfigureAwait(false);
                }

                return JsonSerializer.Serialize(new { ok = true });
            }).ConfigureAwait(false);
        }
        else if (path == "/auth" && method == "POST")
        {
            await HandleAction(ctx, ct, async () =>
            {
                if (OnAuth == null)
                {
                    throw new InvalidOperationException("Auth not configured");
                }

                string message = await OnAuth(ct).ConfigureAwait(false);
                return JsonSerializer.Serialize(new { ok = true, message });
            }).ConfigureAwait(false);
        }
        else if (path == "/search" && method == "POST")
        {
            await HandleAction(ctx, ct, async () =>
            {
                if (OnSearch == null)
                {
                    throw new InvalidOperationException("Search not configured");
                }

                using StreamReader bodyReader = new(ctx.Request.InputStream, ctx.Request.ContentEncoding);
                string body = await bodyReader.ReadToEndAsync(ct).ConfigureAwait(false);
                SearchParams searchParams = JsonSerializer.Deserialize<SearchParams>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new InvalidOperationException("Invalid search parameters");
                object result = await OnSearch(searchParams, ct).ConfigureAwait(false);
                return JsonSerializer.Serialize(result);
            }).ConfigureAwait(false);
        }
        else if (path == "/browse" && method == "GET")
        {
            await HandleAction(ctx, ct, () =>
            {
                string rawPath = ctx.Request.QueryString["path"] ?? DefaultBrowseDir;
                // Derive the allowed filesystem root from a server-side value, not from the user-provided path,
                // so the boundary cannot be manipulated by the caller.
                string fsRoot = Path.GetPathRoot(Path.GetFullPath(DefaultBrowseDir)) ?? "/";
                string dirPath = Path.GetFullPath(rawPath);
                if (!dirPath.StartsWith(fsRoot, StringComparison.Ordinal))
                {
                    throw new UnauthorizedAccessException("Path is outside the allowed filesystem.");
                }
                if (!Directory.Exists(dirPath))
                {
                    throw new DirectoryNotFoundException($"Directory not found: {dirPath}");
                }

                string? parent = Path.GetDirectoryName(dirPath);
                List<string> dirs = new();
                try
                {
                    dirs = Directory.GetDirectories(dirPath)
                        .Select(Path.GetFileName)
                        .Where(n => n != null && !n.StartsWith('.'))
                        .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                        .ToList()!;
                }
                catch (UnauthorizedAccessException) { }

                return Task.FromResult(JsonSerializer.Serialize(new { current = dirPath, parent, dirs }));
            }).ConfigureAwait(false);
        }
        else if (path == "/mkdir" && method == "POST")
        {
            await HandleAction(ctx, ct, async () =>
            {
                using StreamReader bodyReader = new(ctx.Request.InputStream, ctx.Request.ContentEncoding);
                string body = await bodyReader.ReadToEndAsync(ct).ConfigureAwait(false);
                using JsonDocument doc = JsonDocument.Parse(body);
                string dirPath = doc.RootElement.GetProperty("path").GetString()
                    ?? throw new InvalidOperationException("Missing path");
                string mkdirRoot = Path.GetFullPath(MkdirRoot);
                string mkdirRootWithSep = mkdirRoot.EndsWith(Path.DirectorySeparatorChar)
                    ? mkdirRoot : mkdirRoot + Path.DirectorySeparatorChar;
                dirPath = Path.GetFullPath(dirPath);
                if (!dirPath.StartsWith(mkdirRootWithSep, StringComparison.Ordinal) && dirPath != mkdirRoot)
                {
                    throw new UnauthorizedAccessException("Path is outside the allowed directory.");
                }
                Directory.CreateDirectory(dirPath);
                return JsonSerializer.Serialize(new { ok = true, path = dirPath });
            }).ConfigureAwait(false);
        }
        else if (path == "/download" && method == "POST")
        {
            await HandleAction(ctx, ct, async () =>
            {
                if (OnDownload == null)
                {
                    throw new InvalidOperationException("Download not configured");
                }

                using StreamReader bodyReader = new(ctx.Request.InputStream, ctx.Request.ContentEncoding);
                string body = await bodyReader.ReadToEndAsync(ct).ConfigureAwait(false);
                DownloadParams dlParams = JsonSerializer.Deserialize<DownloadParams>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new DownloadParams(".", null, false);
                await OnDownload(dlParams, ct).ConfigureAwait(false);
                return JsonSerializer.Serialize(new { ok = true });
            }).ConfigureAwait(false);
        }
        else if (path == "/download-browser" && method == "POST")
        {
            if (OnBrowserDownload == null)
            {
                ctx.Response.StatusCode = 501;
                ctx.Response.Close();
                return;
            }
            try
            {
                using StreamReader bodyReader = new(ctx.Request.InputStream, ctx.Request.ContentEncoding);
                string body = await bodyReader.ReadToEndAsync(ct).ConfigureAwait(false);
                BrowserDownloadParams dlParams = JsonSerializer.Deserialize<BrowserDownloadParams>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new BrowserDownloadParams(null);
                (System.IO.Stream dataStream, string contentType, string fileName) = await OnBrowserDownload(dlParams, ct).ConfigureAwait(false);
                await using (dataStream.ConfigureAwait(false))
                {
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = contentType;
                    // Sanitize fileName: strip path traversal, control characters and quotes to prevent header injection.
                    string safeBase = Path.GetFileName(fileName);
                    if (string.IsNullOrWhiteSpace(safeBase)) { safeBase = "download"; }
                    safeBase = System.Text.RegularExpressions.Regex.Replace(safeBase, @"[\x00-\x1F\x7F""\\]", "_");
                    string encoded = Uri.EscapeDataString(safeBase);
                    ctx.Response.Headers["Content-Disposition"] = $"attachment; filename=\"{safeBase}\"; filename*=UTF-8''{encoded}";
                    if (dataStream.CanSeek) { dataStream.Seek(0, System.IO.SeekOrigin.Begin); ctx.Response.ContentLength64 = dataStream.Length; }
                    await dataStream.CopyToAsync(ctx.Response.OutputStream, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                Log.LogWarning("[browser-dl] Cancelled by client.");
            }
            catch (InvalidOperationException ex)
            {
                Log.LogWarning($"[browser-dl] Client error: {ex.Message}");
                WriteErrorResponse(ctx, 400, ex.Message);
            }
            catch (IOException ex)
            {
                Log.LogError($"[browser-dl] I/O error: {ex.Message}\n{ex.StackTrace}");
                WriteErrorResponse(ctx, 500, "Błąd odczytu/zapisu.");
            }
            catch (HttpRequestException ex)
            {
                Log.LogError($"[browser-dl] Upstream error: {ex.Message}\n{ex.StackTrace}");
                WriteErrorResponse(ctx, 502, "Błąd komunikacji z KSeF.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                (int statusCode, string msg) = ex switch
                {
                    KsefApiException kex => ((int)kex.StatusCode, kex.Message),
                    UnauthorizedAccessException => (401, "Brak autoryzacji."),
                    JsonException => (400, "Nieprawidłowy format danych."),
                    _ => (500, "Nieoczekiwany błąd serwera.")
                };
                Log.LogError($"[browser-dl] Unexpected {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                WriteErrorResponse(ctx, statusCode, msg);
            }
            finally
            {
                try { ctx.Response.Close(); }
                catch (ObjectDisposedException) { }
            }
            return;
        }
        else if (path == "/download-summary" && method == "POST")
        {
            await HandleAction(ctx, ct, async () =>
            {
                if (OnDownloadSummary == null)
                {
                    throw new InvalidOperationException("Summary not configured");
                }

                using StreamReader bodyReader = new(ctx.Request.InputStream, ctx.Request.ContentEncoding);
                string body = await bodyReader.ReadToEndAsync(ct).ConfigureAwait(false);
                DownloadSummaryParams sumParams = JsonSerializer.Deserialize<DownloadSummaryParams>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new DownloadSummaryParams(".", "");
                string filePath = await OnDownloadSummary(sumParams, ct).ConfigureAwait(false);
                return JsonSerializer.Serialize(new { ok = true, filePath });
            }).ConfigureAwait(false);
        }
        else if (path == "/download-summary-browser" && method == "POST")
        {
            if (OnBrowserDownloadSummary == null)
            {
                WriteErrorResponse(ctx, 501, "Browser summary download not configured");
                return;
            }
            try
            {
                using StreamReader bodyReader = new(ctx.Request.InputStream, ctx.Request.ContentEncoding);
                string body = await bodyReader.ReadToEndAsync(ct).ConfigureAwait(false);
                DownloadSummaryParams sumParams = JsonSerializer.Deserialize<DownloadSummaryParams>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new DownloadSummaryParams(".", "");
                (System.IO.Stream dataStream, string contentType, string fileName) = await OnBrowserDownloadSummary(sumParams, ct).ConfigureAwait(false);
                await using (dataStream.ConfigureAwait(false))
                {
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentType = contentType;
                    string safeBase = Path.GetFileName(fileName);
                    if (string.IsNullOrWhiteSpace(safeBase)) { safeBase = "summary.csv"; }
                    safeBase = System.Text.RegularExpressions.Regex.Replace(safeBase, @"[\x00-\x1F\x7F""\\]", "_");
                    string encoded = Uri.EscapeDataString(safeBase);
                    ctx.Response.Headers["Content-Disposition"] = $"attachment; filename=\"{safeBase}\"; filename*=UTF-8''{encoded}";
                    if (dataStream.CanSeek) { dataStream.Seek(0, System.IO.SeekOrigin.Begin); ctx.Response.ContentLength64 = dataStream.Length; }
                    await dataStream.CopyToAsync(ctx.Response.OutputStream, ct).ConfigureAwait(false);
                }
            }
            catch (InvalidOperationException ex)
            {
                Log.LogWarning($"[summary-dl] Validation error: {ex.Message}");
                WriteErrorResponse(ctx, 400, ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.LogError($"[summary-dl] {ex.GetType().Name}: {ex.Message}");
                WriteErrorResponse(ctx, 500, "Internal server error");
            }
        }
        else if (path == "/invoice-details" && method == "GET")
        {
            await HandleAction(ctx, ct, async () =>
            {
                if (OnInvoiceDetails == null)
                {
                    throw new InvalidOperationException("Details not configured");
                }

                string idxStr = ctx.Request.QueryString["idx"] ?? throw new InvalidOperationException("Missing idx");
                int idx = int.Parse(idxStr);
                object details = await OnInvoiceDetails(idx, ct).ConfigureAwait(false);
                return JsonSerializer.Serialize(details);
            }).ConfigureAwait(false);
        }
        else if (path == "/check-existing" && method == "POST")
        {
            await HandleAction(ctx, ct, async () =>
            {
                if (OnCheckExisting == null)
                {
                    return "[]";
                }

                using StreamReader bodyReader = new(ctx.Request.InputStream, ctx.Request.ContentEncoding);
                string body = await bodyReader.ReadToEndAsync(ct).ConfigureAwait(false);
                CheckExistingParams checkParams = JsonSerializer.Deserialize<CheckExistingParams>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new CheckExistingParams(".", false, false);
                object result = await OnCheckExisting(checkParams).ConfigureAwait(false);
                return JsonSerializer.Serialize(result);
            }).ConfigureAwait(false);
        }
        else if (path == "/cached-invoices" && method == "GET")
        {
            await HandleAction(ctx, ct, async () =>
            {
                if (OnCachedInvoices == null)
                {
                    return JsonSerializer.Serialize(new { invoices = Array.Empty<object>() });
                }

                object result = await OnCachedInvoices().ConfigureAwait(false);
                return JsonSerializer.Serialize(result);
            }).ConfigureAwait(false);
        }
        else if (path == "/token-status" && method == "GET")
        {
            await HandleAction(ctx, ct, async () =>
            {
                if (OnTokenStatus == null)
                {
                    return JsonSerializer.Serialize(new { });
                }

                object status = await OnTokenStatus().ConfigureAwait(false);
                return JsonSerializer.Serialize(status);
            }).ConfigureAwait(false);
        }
        else if (path == "/about" && method == "GET")
        {
            await HandleAction(ctx, ct, async () =>
            {
                object result = OnAbout != null
                    ? await OnAbout().ConfigureAwait(false)
                    : new { };
                return JsonSerializer.Serialize(result);
            }).ConfigureAwait(false);
        }
        else if (path == "/config-editor" && method == "GET")
        {
            await HandleAction(ctx, ct, async () =>
            {
                if (OnLoadConfig == null)
                {
                    return JsonSerializer.Serialize(new { });
                }

                object data = await OnLoadConfig().ConfigureAwait(false);
                return JsonSerializer.Serialize(data, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            }).ConfigureAwait(false);
        }
        else if (path == "/config-editor" && method == "POST")
        {
            await HandleAction(ctx, ct, async () =>
            {
                using StreamReader reader1 = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
                string body = await reader1.ReadToEndAsync(ct).ConfigureAwait(false);
                string error = OnSaveConfig != null
                    ? await OnSaveConfig(body).ConfigureAwait(false)
                    : "";
                return string.IsNullOrEmpty(error)
                    ? JsonSerializer.Serialize(new { ok = true })
                    : JsonSerializer.Serialize(new { ok = false, error });
            }).ConfigureAwait(false);
        }
        else if (path == "/test-notification" && method == "POST")
        {
            await HandleAction(ctx, ct, async () =>
            {
                using StreamReader reader2 = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
                string body = await reader2.ReadToEndAsync(ct).ConfigureAwait(false);
                string result = OnTestNotification != null
                    ? await OnTestNotification(body, ct).ConfigureAwait(false)
                    : "";
                return JsonSerializer.Serialize(new { ok = true, message = result });
            }).ConfigureAwait(false);
        }
        else if (path == "/test-email" && method == "POST")
        {
            await HandleAction(ctx, ct, async () =>
            {
                using StreamReader reader2 = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
                string body = await reader2.ReadToEndAsync(ct).ConfigureAwait(false);
                string result = OnTestEmail != null
                    ? await OnTestEmail(body, ct).ConfigureAwait(false)
                    : "Brak obsługi testu e-mail.";
                return JsonSerializer.Serialize(new { ok = true, message = result });
            }).ConfigureAwait(false);
        }
        else if (path == "/notification-status" && method == "GET")
        {
            await HandleAction(ctx, ct, async () =>
            {
                object data = OnNotificationStatus != null
                    ? await OnNotificationStatus().ConfigureAwait(false)
                    : new { };
                return JsonSerializer.Serialize(data, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            }).ConfigureAwait(false);
        }
        else if (path == "/qr" && method == "GET")
        {
            const int MaxQrUrlLength = 2048;
            string? qrUrl = ctx.Request.QueryString["url"]?.Trim();
            if (string.IsNullOrEmpty(qrUrl) || qrUrl.Length > MaxQrUrlLength)
            {
                ctx.Response.StatusCode = 400;
                ctx.Response.Close();
                return;
            }
            if (!Uri.TryCreate(qrUrl, UriKind.Absolute, out Uri? parsedQrUri)
                || (parsedQrUri.Scheme != Uri.UriSchemeHttp && parsedQrUri.Scheme != Uri.UriSchemeHttps))
            {
                ctx.Response.StatusCode = 400;
                ctx.Response.Close();
                return;
            }
            qrUrl = parsedQrUri.AbsoluteUri;

            try
            {
                byte[] png = QrCodeService.GenerateQrCode(qrUrl);
                ctx.Response.ContentType = "image/png";
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentLength64 = png.Length;
                ctx.Response.Headers.Add("Cache-Control", "public, max-age=3600");
                await ctx.Response.OutputStream.WriteAsync(png, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine("[/qr] Request was canceled.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"[/qr] QR generation failed: {ex}");
                ctx.Response.StatusCode = 500;
            }
            finally
            {
                ctx.Response.Close();
            }
        }
        else if (path == "/whitelist-check" && method == "GET")
        {
            await HandleAction(ctx, ct, async () =>
            {
                string? nip = ctx.Request.QueryString["nip"]?.Trim();
                string? account = ctx.Request.QueryString["account"]?.Trim();
                if (string.IsNullOrEmpty(nip) || string.IsNullOrEmpty(account))
                {
                    throw new ArgumentException("Wymagane parametry: nip, account");
                }
                // Biała Lista API accepts digits only (26 chars): strip spaces, dashes, country code prefix (e.g. "PL"), any non-digits.
                account = System.Text.RegularExpressions.Regex.Replace(account, @"[^\d]", "");

                string date = DateTime.UtcNow.ToString("yyyy-MM-dd");
                string apiUrl = $"https://wl-api.mf.gov.pl/api/check/nip/{Uri.EscapeDataString(nip)}/bank-account/{Uri.EscapeDataString(account)}?date={date}";

                HttpResponseMessage resp = await _wlHttpClient.GetAsync(apiUrl, ct).ConfigureAwait(false);
                string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                string maskedNip = nip.Length > 4 ? new string('*', nip.Length - 4) + nip[^4..] : "****";
                string maskedAccount = account.Length > 4 ? new string('*', account.Length - 4) + account[^4..] : "****";
                string requestId = resp.Headers.TryGetValues("X-Request-ID", out IEnumerable<string>? ids) ? ids.First() : "-";
                Log.LogInformation($"[whitelist] NIP=...{maskedNip} account=...{maskedAccount} date={date} status={resp.StatusCode} requestId={requestId}");

                // Parse and re-serialize so HandleAction doesn't double-encode the JSON string.
                using JsonDocument doc = JsonDocument.Parse(body);
                if (!resp.IsSuccessStatusCode)
                {
                    return JsonSerializer.Serialize(new
                    {
                        error = $"MF API returned HTTP {(int)resp.StatusCode}",
                        status = (int)resp.StatusCode,
                        requestId,
                        detail = doc.RootElement,
                    });
                }
                return JsonSerializer.Serialize(doc.RootElement);
            }).ConfigureAwait(false);
        }
        else if (path == "/quit" && method == "POST")
        {
            ctx.Response.StatusCode = 200;
            byte[] ok = Encoding.UTF8.GetBytes("{\"ok\":true}");
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = ok.Length;
            await ctx.Response.OutputStream.WriteAsync(ok, ct).ConfigureAwait(false);
            ctx.Response.Close();
            OnQuit?.Invoke();
        }
        else
        {
            byte[] html = Encoding.UTF8.GetBytes(HtmlPage);
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.ContentLength64 = html.Length;
            await ctx.Response.OutputStream.WriteAsync(html, ct).ConfigureAwait(false);
            ctx.Response.Close();
        }
    }

    private static void WriteErrorResponse(HttpListenerContext ctx, int statusCode, string message)
    {
        try
        {
            byte[] body = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { error = message }));
            ctx.Response.StatusCode = statusCode;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = body.Length;
            ctx.Response.OutputStream.Write(body, 0, body.Length);
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private static async Task HandleAction(HttpListenerContext ctx, CancellationToken ct, Func<Task<string>> action)
    {
        try
        {
            string json = await action().ConfigureAwait(false);
            byte[] body = Encoding.UTF8.GetBytes(json);
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentLength64 = body.Length;
            await ctx.Response.OutputStream.WriteAsync(body, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            string errJson = JsonSerializer.Serialize(new { error = "Request was canceled." });
            byte[] body = Encoding.UTF8.GetBytes(errJson);
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.StatusCode = 408;
            ctx.Response.ContentLength64 = body.Length;
            await ctx.Response.OutputStream.WriteAsync(body, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ex is KsefApiException kapi)
            {
                Log.LogWarning($"[HandleAction] KSeF API error: HTTP {(int)kapi.StatusCode} — {kapi.Message}");
            }
            else
            {
                Log.LogError($"[HandleAction] Unhandled {ex.GetType().Name}: {ex.Message}");
            }
            (int statusCode, string errorMessage) = ex switch
            {
                KsefApiException kex => ((int)kex.StatusCode, kex.Message),
                ArgumentException or FormatException or JsonException => (400, ex.Message),
                UnauthorizedAccessException => (401, "Unauthorized"),
                FileNotFoundException or DirectoryNotFoundException => (404, "Not found"),
                HttpRequestException => (502, "Upstream service error"),
                _ => (500, "Internal server error"),
            };
            string errJson = JsonSerializer.Serialize(new { error = errorMessage });
            byte[] body = Encoding.UTF8.GetBytes(errJson);
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.StatusCode = statusCode;
            ctx.Response.ContentLength64 = body.Length;
            await ctx.Response.OutputStream.WriteAsync(body, ct).ConfigureAwait(false);
        }
        finally
        {
            ctx.Response.Close();
        }
    }

    private static int GetRandomPort()
    {
        using TcpListener listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    internal const string HtmlPage = """
<!DOCTYPE html>
<html lang="pl">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>KSeFCli</title>
<style>
*{box-sizing:border-box;margin:0;padding:0}
body{font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;background:#f5f5f5;color:#333;padding:1.5rem;max-width:100%;margin:0 auto}
h1{font-size:1.3rem;margin-bottom:1rem}
.search-form{display:flex;flex-direction:column;gap:0;margin-bottom:1rem;padding:.75rem 1rem;background:#fff;border-radius:8px;box-shadow:0 1px 3px rgba(0,0,0,.1)}
.search-row{display:flex;gap:.5rem;align-items:end;flex-wrap:wrap;width:100%}
.action-row{display:flex;gap:.5rem;align-items:center;flex-wrap:wrap;width:100%;padding-top:.5rem;margin-top:.4rem;border-top:1px solid #f0f0f0}
.field{display:flex;flex-direction:column;gap:.2rem}
.field label{font-size:.75rem;font-weight:600;color:#555}
.field select,.field input{padding:.4rem .6rem;border:1px solid #ccc;border-radius:4px;font-size:.85rem}
.field input{width:140px}.field input[type=month]{width:160px}
button{padding:.5rem 1.2rem;border:none;border-radius:6px;font-size:.9rem;font-weight:600;cursor:pointer;transition:background .15s,opacity .15s}
button:disabled{opacity:.4;cursor:default}
.btn-primary{background:#1976d2;color:#fff}
.btn-primary:hover:not(:disabled){background:#1565c0}
.btn-success{background:#2e7d32;color:#fff}
.btn-success:hover:not(:disabled){background:#256029}
.btn-auth{background:#2e7d32;color:#fff}
.btn-auth:hover:not(:disabled){background:#256029}
.btn-auth.auth-warning{background:#e65100}
.btn-auth.auth-warning:hover:not(:disabled){background:#bf360c}
.btn-auth.auth-expired{background:#c62828}
.btn-auth.auth-expired:hover:not(:disabled){background:#b71c1c}
.btn-auth.auth-unknown{background:#757575}
.btn-auth.auth-unknown:hover:not(:disabled){background:#616161}
.token-info{font-size:.72rem;color:#888;margin-left:-.2rem;white-space:nowrap}
.btn-prefs{background:#546e7a;color:#fff}
.btn-prefs:hover:not(:disabled){background:#37474f}
.btn-config{background:#4527a0;color:#fff}
.btn-config:hover:not(:disabled){background:#311b92}
.btn-about{background:#00695c;color:#fff}
.btn-about:hover:not(:disabled){background:#004d40}
.cfg-modal{width:600px;max-height:85vh}
.cfg-profile-card{border:1px solid #ddd;border-radius:8px;padding:.8rem 1rem;margin-bottom:.8rem;position:relative}
.cfg-profile-card .cfg-card-title{font-weight:600;font-size:.9rem;margin-bottom:.6rem;display:flex;align-items:center;gap:.5rem}
.cfg-card-footer{display:flex;justify-content:space-between;align-items:flex-start;padding-top:.4rem;border-top:1px solid #ddd}
.cfg-field{display:flex;flex-direction:column;margin-bottom:.5rem}
.cfg-field label{font-size:.75rem;color:#666;margin-bottom:.2rem}
.cfg-field input,.cfg-field select{padding:.35rem .5rem;border:1px solid #ccc;border-radius:4px;font-size:.85rem}
.cfg-pw-wrap{display:flex;gap:.3rem}
.cfg-pw-wrap input{flex:1}
.cfg-pw-wrap button{padding:.3rem .5rem;font-size:.8rem}
.btn-danger{background:#c62828;color:#fff}
.btn-danger:hover:not(:disabled){background:#b71c1c}
.prefs-modal{width:480px;max-height:80vh}
.prefs-tabs{display:flex;border-bottom:2px solid #e0e0e0;background:#fafafa;padding:0 1rem;gap:0}
.prefs-tab{background:none;border:none;border-bottom:3px solid transparent;border-radius:0;padding:.55rem 1rem;font-size:.85rem;font-weight:600;color:#666;cursor:pointer;margin-bottom:-2px;transition:color .15s,border-color .15s}
.prefs-tab:hover{color:#333}
.prefs-tab.active{color:#1976d2;border-bottom-color:#1976d2}
.pref-row{display:flex;align-items:flex-start;gap:1rem;padding:.55rem 0;border-bottom:1px solid #f0f0f0}
.pref-row:last-child{border-bottom:none}
.pref-label{font-size:.82rem;font-weight:600;color:#555;min-width:175px;flex-shrink:0;padding-top:.15rem}
#toast-container{position:fixed;top:1rem;right:1rem;z-index:9999;display:flex;flex-direction:column;gap:.5rem;pointer-events:none;max-width:380px}
.toast{display:flex;align-items:flex-start;gap:.6rem;padding:.65rem .9rem;border-radius:8px;font-size:.85rem;font-weight:500;box-shadow:0 3px 10px rgba(0,0,0,.15);pointer-events:all;opacity:1;transition:opacity .35s,transform .35s;transform:translateX(0)}
.toast.hiding{opacity:0;transform:translateX(calc(100% + 1rem))}
.toast.info{background:#e3f2fd;color:#1565c0;border-left:3px solid #1976d2}
.toast.done{background:#e8f5e9;color:#2e7d32;border-left:3px solid #388e3c}
.toast.error{background:#fbe9e7;color:#c62828;border-left:3px solid #d32f2f}
.toast.idle{background:#f5f5f5;color:#555;border-left:3px solid #bdbdbd}
.toast-msg{flex:1;line-height:1.35}
.toast-close{background:none;border:none;cursor:pointer;color:inherit;opacity:.6;font-size:1rem;padding:0 0 0 .3rem;line-height:1;flex-shrink:0}
.toast-close:hover{opacity:1}
.progress-wrap{background:#ddd;border-radius:8px;overflow:hidden;height:22px;margin-bottom:.75rem;display:none}
.progress-wrap.visible{display:block}
.progress-bar{height:100%;background:#1976d2;transition:width .3s;width:0%;display:flex;align-items:center;justify-content:center;color:#fff;font-size:.75rem;font-weight:600;min-width:2rem}
table{width:100%;border-collapse:collapse;background:#fff;border-radius:8px;overflow:hidden;box-shadow:0 1px 3px rgba(0,0,0,.1);font-size:.85rem;margin-bottom:.75rem;table-layout:auto}
th{background:#fafafa;text-align:left;padding:.6rem .8rem;border-bottom:2px solid #e0e0e0;cursor:pointer;user-select:none;white-space:nowrap;font-size:.8rem;color:#555}
th:hover{background:#f0f0f0}
td{padding:.5rem .8rem;border-bottom:1px solid #eee;vertical-align:top}
td.col-name{max-width:300px;word-break:break-word}
td.col-ksef{word-break:break-all}
tr:hover td{background:#f8f9ff}
tr.downloading td{background:#e3f2fd}
tr.done td{background:#f1f8e9}
tr.error td{background:#fbe9e7}
.amount{text-align:right;font-variant-numeric:tabular-nums;white-space:nowrap}
.sort-arrow{font-size:.65rem;margin-left:.2rem;color:#999}
.toolbar{display:flex;gap:.5rem;align-items:center;margin-bottom:.75rem}
.count{color:#888;font-size:.85rem}
.empty{text-align:center;padding:2rem;color:#999}
@keyframes spin{to{transform:rotate(360deg)}}
.spinner{display:inline-block;animation:spin 1s linear infinite}
.dl-icon{width:1.2rem;text-align:center;flex-shrink:0;font-size:.85rem}
.sel-toolbar{display:none;gap:.5rem;align-items:center;margin-bottom:.6rem;padding:.4rem .8rem;background:#fff;border-radius:8px;box-shadow:0 1px 3px rgba(0,0,0,.1)}
.sel-toolbar.visible{display:flex}
.btn-sm{padding:.3rem .8rem;font-size:.78rem;font-weight:500;border-radius:4px}
.btn-outline{background:#fff;color:#1976d2;border:1px solid #1976d2}
.btn-outline:hover{background:#e3f2fd}
.sel-count{font-size:.82rem;color:#555;margin-left:auto}
td input[type=checkbox]{cursor:pointer;width:1rem;height:1rem}
.filter-bar{display:flex;flex-direction:column;gap:.4rem;margin-bottom:.6rem;padding:.5rem .8rem;background:#fff;border-radius:8px;box-shadow:0 1px 3px rgba(0,0,0,.1);display:none}
.filter-bar.visible{display:flex}
.filter-chips-row{display:flex;gap:.4rem;align-items:center;flex-wrap:wrap}
.filter-label{font-size:.78rem;font-weight:600;color:#555;margin-right:.2rem}
.filter-chart{display:flex;flex-direction:column;gap:5px;border-top:1px solid #eee;padding-top:.45rem;margin-top:.1rem}
.hbar-row{display:flex;align-items:center;gap:.5rem}
.hbar-cur{font-size:.88rem;font-weight:700;width:2.8rem;text-align:right;flex-shrink:0}
.hbar-track{flex:1;background:#e8e8e8;border-radius:3px;height:10px;overflow:hidden;display:flex;align-items:stretch}
.hbar-fill{transition:width .35s}
.hbar-amt{font-size:.88rem;color:#555;white-space:nowrap;min-width:10rem;text-align:right;flex-shrink:0}
.hbar-pln{font-size:.78rem;color:#999;margin-left:.3rem}
.hbar-summary{font-size:.8rem;color:#666;margin-top:.4rem;padding-top:.35rem;border-top:1px solid #e0e0e0}
.hbar-summary b{color:#444}
.hbar-summary-warn{color:#e57373;font-size:.75rem}
.hbar-summary-wait{font-style:italic;color:#aaa}
.hbar-chart-title{font-size:.75rem;color:#888;font-weight:600;margin-bottom:.1rem}
.chip{display:inline-flex;align-items:center;padding:.25rem .7rem;border-radius:16px;font-size:.78rem;cursor:pointer;border:1.5px solid #bbb;background:#fff;color:#555;transition:all .15s;user-select:none}
.chip:hover{border-color:#888}
.chip.active{background:#1976d2;color:#fff;border-color:#1976d2}
.chip .chip-count{margin-left:.3rem;opacity:.7;font-size:.7rem}
.chip .chip-rate{margin-left:.35rem;font-size:.68rem;opacity:.6;font-variant-numeric:tabular-nums}
.btn-preview{background:none;border:1px solid #bbb;border-radius:4px;padding:.15rem .4rem;font-size:.75rem;cursor:pointer;color:#666;white-space:nowrap}
.btn-preview:hover{border-color:#2e7d32;color:#2e7d32;background:#e8f5e9}
.badge{display:inline-block;padding:.1rem .3rem;border-radius:3px;font-size:.58rem;font-weight:700;letter-spacing:.3px;margin-right:.15rem;line-height:1.2}
.badge-xml{background:#1976d2;color:#fff}
.badge-pdf{background:#c62828;color:#fff}
.badge-json{background:#e65100;color:#fff}
tr.has-files>td{background:rgba(46,125,50,.05)}
tr.has-files:hover>td{background:rgba(46,125,50,.1)}
.preview-page{padding:2rem;max-width:800px;margin:0 auto;font-size:.85rem;line-height:1.5}
.preview-title{font-size:1.1rem;font-weight:700}
.preview-subtitle{text-align:center;font-size:.8rem;color:#666;margin-bottom:.6rem}
.preview-qr{text-align:center;margin-bottom:1rem}.preview-qr img{width:90px;height:90px;border:1px solid #ddd;border-radius:4px}
.preview-qr a{display:inline-flex;align-items:center;gap:.25rem;font-size:.72rem;color:#1565c0;margin-top:.4rem;text-decoration:none;border:1px solid #1565c0;border-radius:4px;padding:.2rem .5rem;transition:background .15s,color .15s}.preview-qr a:hover{background:#1565c0;color:#fff}@media(prefers-color-scheme:dark){.preview-qr a{color:#90caf9;border-color:#90caf9}.preview-qr a:hover{background:#90caf9;color:#000}}
.preview-parties{display:flex;gap:1.5rem;margin-bottom:1.2rem}
.preview-party{flex:1;border:1px solid #ddd;border-radius:6px;padding:.8rem}
.preview-party h5{font-size:.75rem;text-transform:uppercase;color:#888;margin-bottom:.3rem;letter-spacing:.5px}
.preview-party .name{font-weight:600;font-size:.9rem}
.preview-party .nip{font-size:.8rem;color:#555}
.preview-party .addr{font-size:.8rem;color:#666}
.preview-items{width:100%;border-collapse:collapse;margin-bottom:1rem;font-size:.8rem}
.preview-items th{background:#f5f5f5;padding:.4rem .6rem;text-align:left;border:1px solid #ddd;font-size:.75rem}
.preview-items td{padding:.4rem .6rem;border:1px solid #ddd}
.preview-totals{display:flex;justify-content:flex-end;margin-bottom:1rem}
.preview-totals table{border-collapse:collapse;font-size:.85rem}
.preview-totals td{padding:.3rem .8rem;border:1px solid #ddd}
.preview-totals .label{font-weight:600;background:#f5f5f5}
.preview-totals .total{font-weight:700;font-size:.95rem}
.preview-meta{font-size:.75rem;color:#888;border-top:1px solid #eee;padding-top:.5rem;margin-top:1rem}
.preview-payment{border:1px solid #ddd;border-radius:6px;padding:.8rem 1rem;margin-bottom:1rem;font-size:.82rem}
.preview-payment h5{font-size:.72rem;text-transform:uppercase;color:#888;margin-bottom:.4rem;letter-spacing:.5px}
.preview-payment .pay-meta{color:#555;margin-bottom:.6rem;font-size:.8rem}
.preview-payment .bank-row{display:flex;align-items:center;gap:.5rem;flex-wrap:wrap;margin-bottom:.25rem}
.preview-payment .bank-nr{font-family:monospace;font-size:.88rem;font-weight:600;letter-spacing:.5px}
.preview-payment .bank-name{font-size:.78rem;color:#777;margin-bottom:.6rem}
.btn-copy{display:inline-flex;align-items:center;gap:.25rem;background:#e8f5e9;border:1px solid #81c784;border-radius:4px;padding:.15rem .45rem;font-size:.72rem;cursor:pointer;color:#2e7d32;white-space:nowrap;transition:all .15s}
.btn-copy:hover{background:#c8e6c9;border-color:#4caf50;color:#1b5e20}
.btn-copy.copied{background:#a5d6a7;border-color:#388e3c;color:#1b5e20}
.btn-whitelist{display:inline-flex;align-items:center;gap:.3rem;font-size:.78rem;color:#1565c0;background:none;border:1px solid #1565c0;border-radius:4px;padding:.25rem .6rem;cursor:pointer;transition:background .15s,color .15s}
.btn-whitelist:hover{background:#1565c0;color:#fff}
.btn-whitelist:disabled{opacity:.5;cursor:wait}
.wl-result{font-size:.78rem;margin-left:.5rem;font-weight:600}
.wl-result.ok{color:#2e7d32}.wl-result.fail{color:#c62828}.wl-result.err{color:#e65100}
.wl-limit{display:block;font-size:.7rem;color:#999;margin-top:.3rem}
.preview-title-row{display:flex;align-items:center;justify-content:center;gap:.5rem;margin-bottom:.2rem}
.detail-overlay{display:flex;position:fixed;top:0;left:0;width:100%;height:100%;background:rgba(0,0,0,.4);z-index:200;align-items:center;justify-content:center;overflow-y:auto;padding:1rem}
.modal-overlay{display:none;position:fixed;top:0;left:0;width:100%;height:100%;background:rgba(0,0,0,.4);z-index:100;align-items:center;justify-content:center}
.modal-overlay.visible{display:flex}
.modal{background:#fff;border-radius:10px;box-shadow:0 8px 30px rgba(0,0,0,.2);width:480px;max-width:95vw;max-height:70vh;display:flex;flex-direction:column;overflow:hidden}
.modal-header{display:flex;align-items:center;justify-content:space-between;padding:.8rem 1rem;border-bottom:1px solid #e0e0e0;background:#fafafa}
.modal-header h2{font-size:.95rem;margin:0}
.modal-close{background:none;border:none;font-size:1.3rem;cursor:pointer;color:#888;padding:0 .3rem}
.modal-close:hover{color:#333}
.modal-path{padding:.5rem 1rem;background:#f5f5f5;border-bottom:1px solid #eee;font-size:.8rem;color:#555;word-break:break-all;display:flex;align-items:center;gap:.5rem}
.modal-path-text{flex:1;overflow:hidden;text-overflow:ellipsis}
.dir-list{overflow-y:auto;flex:1;padding:.3rem 0}
.dir-item{display:flex;align-items:center;gap:.5rem;padding:.45rem 1rem;cursor:pointer;font-size:.85rem;border-bottom:1px solid #f5f5f5;transition:background .1s}
.dir-item:hover{background:#e3f2fd}
.dir-item.parent{color:#1976d2;font-weight:600}
.dir-icon{flex-shrink:0;width:1.2rem;text-align:center}
.modal-footer{padding:.6rem 1rem;border-top:1px solid #e0e0e0;display:flex;justify-content:space-between;align-items:center;background:#fafafa}
.modal-footer .new-dir{display:flex;gap:.3rem;align-items:center}
.modal-footer .new-dir input{padding:.3rem .5rem;border:1px solid #ccc;border-radius:4px;font-size:.8rem;width:140px}
.modal-footer .new-dir button{padding:.3rem .6rem;font-size:.8rem}
/* --- Dark mode --- */
body.dark{background:#121212;color:#e0e0e0}
body.dark h1{color:#e0e0e0}
body.dark .search-form{background:#1e1e1e;box-shadow:0 1px 3px rgba(0,0,0,.4)}
body.dark .action-row{border-top-color:#333}
body.dark .field label{color:#aaa}
body.dark .field select,body.dark .field input{background:#2a2a2a;border-color:#444;color:#e0e0e0}
body.dark .prefs-panel{border-left-color:#78909c}
body.dark .toast.info{background:#0d2137;color:#64b5f6;border-left-color:#1976d2}
body.dark .toast.done{background:#1b3a1b;color:#81c784;border-left-color:#388e3c}
body.dark .toast.error{background:#3e1212;color:#ef9a9a;border-left-color:#d32f2f}
body.dark .toast.idle{background:#1e1e1e;color:#888;border-left-color:#444}
body.dark .progress-wrap{background:#333}
body.dark table{background:#1e1e1e;box-shadow:0 1px 3px rgba(0,0,0,.4)}
body.dark th{background:#252525;border-bottom-color:#444;color:#aaa}
body.dark th:hover{background:#2a2a2a}
body.dark td{border-bottom-color:#333}
body.dark tr:hover td{background:#1a2638}
body.dark tr.downloading td{background:#0d2137}
body.dark tr.done td{background:#1b3a1b}
body.dark tr.error td{background:#3e1212}
body.dark .count{color:#888}
body.dark .empty{color:#666}
body.dark .sel-toolbar{background:#1e1e1e;box-shadow:0 1px 3px rgba(0,0,0,.4)}
body.dark .sel-count{color:#aaa}
body.dark .btn-outline{background:#1e1e1e;color:#64b5f6;border-color:#64b5f6}
body.dark .btn-outline:hover{background:#0d2137}
body.dark .filter-bar{background:#1e1e1e;box-shadow:0 1px 3px rgba(0,0,0,.4)}
body.dark .filter-label{color:#aaa}
body.dark .filter-chart{border-top-color:#2a2a2a}
body.dark .hbar-track{background:#333}
body.dark .hbar-amt{color:#aaa}
body.dark .hbar-pln{color:#666}
body.dark .hbar-summary{color:#888;border-top-color:#333}
body.dark .hbar-summary b{color:#bbb}
body.dark .hbar-chart-title{color:#666}
body.dark .chip{background:#2a2a2a;border-color:#555;color:#ccc}
body.dark .chip:hover{border-color:#888}
body.dark .chip.active{background:#1565c0;color:#fff;border-color:#1565c0}
/* --- Preview dark mode (independent of GUI dark mode) --- */
.detail-popover.preview-dark{background:#1e1e1e;color:#e0e0e0}
.detail-popover.preview-dark .dp-header{background:#252525;border-bottom-color:#444}
.detail-popover.preview-dark .dp-header h3{color:#e0e0e0}
.detail-popover.preview-dark .dp-close{color:#888}
.detail-popover.preview-dark .dp-close:hover{color:#e0e0e0}
.detail-popover.preview-dark .preview-subtitle{color:#aaa}
.detail-popover.preview-dark .preview-party{border-color:#444}
.detail-popover.preview-dark .preview-party h5{color:#888}
.detail-popover.preview-dark .preview-party .name{color:#e0e0e0}
.detail-popover.preview-dark .preview-party .nip{color:#aaa}
.detail-popover.preview-dark .preview-party .addr{color:#999}
.detail-popover.preview-dark .preview-items th{background:#252525;border-color:#444;color:#aaa}
.detail-popover.preview-dark .preview-items td{border-color:#444;color:#e0e0e0}
.detail-popover.preview-dark .preview-totals td{border-color:#444;color:#e0e0e0}
.detail-popover.preview-dark .preview-totals .label{background:#252525}
.detail-popover.preview-dark .preview-meta{color:#666;border-top-color:#333}
.detail-popover.preview-dark .preview-payment{border-color:#444}
.detail-popover.preview-dark .preview-payment h5{color:#888}
.detail-popover.preview-dark .preview-payment .pay-meta{color:#aaa}
.detail-popover.preview-dark .preview-payment .bank-nr{color:#e0e0e0}
.detail-popover.preview-dark .preview-payment .bank-name{color:#666}
.detail-popover.preview-dark .btn-copy{background:#1b5e20;border-color:#4caf50;color:#a5d6a7}
.detail-popover.preview-dark .btn-copy:hover{background:#2e7d32;border-color:#66bb6a;color:#c8e6c9}
.detail-popover.preview-dark .btn-copy.copied{background:#388e3c;border-color:#81c784;color:#e8f5e9}
.detail-popover.preview-dark .btn-whitelist{color:#90caf9;border-color:#90caf9}
.detail-popover.preview-dark .btn-whitelist:hover{background:#90caf9;color:#000}
.detail-popover.preview-dark .wl-result.ok{color:#a5d6a7}
.detail-popover.preview-dark .wl-result.fail{color:#ef9a9a}
.detail-popover.preview-dark .wl-result.err{color:#ffcc80}
.detail-popover.preview-dark .wl-limit{color:#555}
.detail-popover.preview-dark .dp-section h4{color:#aaa;border-bottom-color:#333}
.detail-popover.preview-dark .dp-label{color:#aaa}
.detail-popover.preview-dark .dp-val{color:#e0e0e0}
.detail-popover.preview-dark th{background:#252525;border-bottom-color:#444;color:#aaa}
.detail-popover.preview-dark td{border-bottom-color:#333;color:#e0e0e0}
.detail-popover.preview-dark .dp-loading{color:#666}
body.dark .btn-preview{border-color:#555;color:#aaa}
body.dark .btn-preview:hover{border-color:#81c784;color:#81c784;background:#1b3a1b}
body.dark tr.has-files>td{background:rgba(129,199,132,.06)}
body.dark tr.has-files:hover>td{background:rgba(129,199,132,.12)}
/* Preview popover header — flex so close button stays in top-right corner */
.detail-popover.preview-popover .dp-header{display:flex;justify-content:space-between;align-items:center;padding:.6rem 1rem;background:#fafafa;border-bottom:1px solid #e0e0e0;border-radius:10px 10px 0 0;position:sticky;top:0;z-index:1}
.detail-popover.preview-popover .dp-header h3{margin:0;font-size:.9rem}
.detail-popover.preview-popover .dp-close{background:none;border:none;font-size:1.2rem;cursor:pointer;color:#888;padding:0 .3rem}
.detail-popover.preview-popover .dp-close:hover{color:#333}
.detail-popover.preview-popover .dp-body{padding:.8rem 1rem}
.detail-popover.preview-popover .dp-loading{text-align:center;padding:2rem;color:#999}
/* Force light styles on details popover when details-dark is off (overrides body.dark) */
.detail-popover:not(.preview-popover):not(.details-dark){background:#fff;color:#333;box-shadow:0 8px 30px rgba(0,0,0,.25)}
.detail-popover:not(.preview-popover):not(.details-dark) .dp-header{background:#fafafa;border-bottom-color:#e0e0e0}
.detail-popover:not(.preview-popover):not(.details-dark) .dp-header h3{color:#333}
.detail-popover:not(.preview-popover):not(.details-dark) .dp-close{color:#888}
.detail-popover:not(.preview-popover):not(.details-dark) .dp-close:hover{color:#333}
.detail-popover:not(.preview-popover):not(.details-dark) .dp-section h4{color:#555;border-bottom-color:#eee}
.detail-popover:not(.preview-popover):not(.details-dark) .dp-label{color:#555}
.detail-popover:not(.preview-popover):not(.details-dark) .dp-val{color:#333}
.detail-popover:not(.preview-popover):not(.details-dark) th{background:#fafafa;border-bottom-color:#e0e0e0;color:#555}
.detail-popover:not(.preview-popover):not(.details-dark) td{border-bottom-color:#eee;color:#333}
.detail-popover:not(.preview-popover):not(.details-dark) .dp-loading{color:#999}
/* Force light styles on preview popover when preview-dark is off (overrides body.dark) */
.detail-popover.preview-popover:not(.preview-dark){background:#fff;color:#333;box-shadow:0 8px 30px rgba(0,0,0,.25)}
.detail-popover.preview-popover:not(.preview-dark) .dp-header{background:#fafafa;border-bottom-color:#e0e0e0}
.detail-popover.preview-popover:not(.preview-dark) .dp-header h3{color:#333}
.detail-popover.preview-popover:not(.preview-dark) .dp-close{color:#888}
.detail-popover.preview-popover:not(.preview-dark) .dp-close:hover{color:#333}
.detail-popover.preview-popover:not(.preview-dark) .preview-items{background:#fff;box-shadow:none}
.detail-popover.preview-popover:not(.preview-dark) .preview-items th{background:#f5f5f5;border-color:#ddd;color:#333}
.detail-popover.preview-popover:not(.preview-dark) .preview-items td{border-color:#ddd;color:#333}
.detail-popover.preview-popover:not(.preview-dark) .preview-totals table{background:transparent;box-shadow:none}
.detail-popover.preview-popover:not(.preview-dark) .preview-totals td{border-color:#ddd;color:#333}
.detail-popover.preview-popover:not(.preview-dark) .preview-totals .label{background:#f5f5f5}
.detail-popover.preview-popover:not(.preview-dark) .preview-meta{color:#888;border-top-color:#eee}
body.dark .detail-popover{background:#1e1e1e;box-shadow:0 8px 30px rgba(0,0,0,.6)}
body.dark .detail-popover .dp-header{background:#252525;border-bottom-color:#444}
body.dark .detail-popover .dp-header h3{color:#e0e0e0}
body.dark .detail-popover .dp-close{color:#888}
body.dark .detail-popover .dp-close:hover{color:#e0e0e0}
body.dark .detail-popover .dp-section h4{color:#aaa;border-bottom-color:#333}
body.dark .detail-popover .dp-label{color:#aaa}
body.dark .detail-popover .dp-val{color:#e0e0e0}
body.dark .detail-popover th{background:#252525;border-bottom-color:#444;color:#aaa}
body.dark .detail-popover td{border-bottom-color:#333;color:#e0e0e0}
body.dark .detail-popover .dp-loading{color:#666}
body.dark .modal{background:#1e1e1e;box-shadow:0 8px 30px rgba(0,0,0,.6)}
body.dark .modal-header{background:#252525;border-bottom-color:#444}
body.dark .modal-header h2{color:#e0e0e0}
body.dark .modal-close{color:#888}
body.dark .modal-close:hover{color:#e0e0e0}
body.dark .modal-path{background:#1a1a1a;border-bottom-color:#333;color:#aaa}
body.dark .dir-item{border-bottom-color:#2a2a2a;color:#e0e0e0}
body.dark .dir-item:hover{background:#0d2137}
body.dark .dir-item.parent{color:#64b5f6}
body.dark .modal-footer{background:#252525;border-top-color:#444}
body.dark .modal-footer .new-dir input{background:#2a2a2a;border-color:#444;color:#e0e0e0}
body.dark .cfg-profile-card{border-color:#444}
body.dark .cfg-card-footer{border-top-color:#444}
body.dark .cfg-field label{color:#aaa}
body.dark .cfg-field input,body.dark .cfg-field select{background:#2a2a2a;border-color:#444;color:#e0e0e0}
body.dark .token-info{color:#888}
body.dark .sort-arrow{color:#666}
/* Prefs modal dark */
body.dark .prefs-tabs{background:#252525;border-bottom-color:#444}
body.dark .prefs-tab{color:#aaa}
body.dark .prefs-tab:hover{color:#e0e0e0}
body.dark .prefs-tab.active{color:#64b5f6;border-bottom-color:#64b5f6}
body.dark .pref-row{border-bottom-color:#2a2a2a}
body.dark .pref-label{color:#aaa}
/* Details dark mode (independent of GUI dark mode) */
.detail-popover.details-dark{background:#1e1e1e;color:#e0e0e0}
.detail-popover.details-dark .dp-header{background:#252525;border-bottom-color:#444}
.detail-popover.details-dark .dp-header h3{color:#e0e0e0}
.detail-popover.details-dark .dp-close{color:#888}
.detail-popover.details-dark .dp-close:hover{color:#e0e0e0}
.detail-popover.details-dark .dp-section h4{color:#aaa;border-bottom-color:#333}
.detail-popover.details-dark .dp-label{color:#aaa}
.detail-popover.details-dark .dp-val{color:#e0e0e0}
.detail-popover.details-dark th{background:#252525;border-bottom-color:#444;color:#aaa}
.detail-popover.details-dark td{border-bottom-color:#333;color:#e0e0e0}
.detail-popover.details-dark .dp-loading{color:#666}
/* Force light on details popover when details-dark is off (overrides body.dark) */
.detail-popover:not(.preview-popover):not(.details-dark){background:#fff;color:#333;box-shadow:0 8px 30px rgba(0,0,0,.25)}
.detail-popover:not(.preview-popover):not(.details-dark) .dp-header{background:#fafafa;border-bottom-color:#e0e0e0}
.detail-popover:not(.preview-popover):not(.details-dark) .dp-header h3{color:#333}
.detail-popover:not(.preview-popover):not(.details-dark) .dp-close{color:#888}
.detail-popover:not(.preview-popover):not(.details-dark) .dp-close:hover{color:#333}
.detail-popover:not(.preview-popover):not(.details-dark) .dp-section h4{color:#555;border-bottom-color:#eee}
.detail-popover:not(.preview-popover):not(.details-dark) .dp-label{color:#555}
.detail-popover:not(.preview-popover):not(.details-dark) .dp-val{color:#333}
.detail-popover:not(.preview-popover):not(.details-dark) th{background:#fafafa;border-bottom-color:#e0e0e0;color:#555}
.detail-popover:not(.preview-popover):not(.details-dark) td{border-bottom-color:#eee;color:#333}
.detail-popover:not(.preview-popover):not(.details-dark) .dp-loading{color:#999}
</style>
</head>
<body>
<h1>KSeFCli - Faktury</h1>
<div class="search-form">
  <div class="search-row">
    <div class="field">
      <label>Profil</label>
      <select id="profileSelect" onchange="onProfileChange()"></select>
    </div>
    <div class="field">
      <label>Typ podmiotu</label>
      <select id="subjectType">
        <option value="Subject1">Sprzedawca (Subject1)</option>
        <option value="Subject2" selected>Nabywca (Subject2)</option>
        <option value="Subject3">Subject3</option>
        <option value="SubjectAuthorized">Upowazniony</option>
      </select>
    </div>
    <div class="field">
      <label>Od (miesiac)</label>
      <!-- coderabbit-ignore: min is intentionally hardcoded to 2026-02 — KSeF mandatory e-invoicing started February 2026 -->
      <input id="fromDate" type="month" min="2026-02">
    </div>
    <div class="field">
      <label>Do (miesiac)</label>
      <!-- coderabbit-ignore: min is intentionally hardcoded to 2026-02 — KSeF mandatory e-invoicing started February 2026 -->
      <input id="toDate" type="month" min="2026-02">
    </div>
    <div class="field">
      <label>Typ daty</label>
      <select id="dateType">
        <option value="Issue" selected>Data wystawienia</option>
        <option value="Invoicing">Data przyjecia KSeF</option>
        <option value="PermanentStorage">Trwaly zapis</option>
      </select>
    </div>
    <button class="btn-primary" id="btnSearch" onclick="doSearch()">Szukaj</button>
  </div>
  <div class="action-row">
    <button class="btn-auth auth-unknown" id="btnAuth" onclick="doAuth()" title="Odswierz token KSeF">&#128274; Autoryzuj</button>
    <span class="token-info" id="tokenInfo"></span>
    <button class="btn-prefs" id="btnPrefs" onclick="togglePrefs()" title="Preferencje">&#9881; Preferencje</button>
    <button class="btn-config" id="btnConfig" onclick="openConfigEditor()" title="Edytor konfiguracji">&#9998; Konfiguracja</button>
    <button class="btn-about" onclick="openAbout()" title="O programie">&#9432; O programie</button>
    <button class="btn-danger" onclick="doQuit()" title="Zamknij serwer GUI" style="margin-left:auto">&#9746; Zakoncz</button>
  </div>
</div>
<div class="modal-overlay" id="prefsModal">
  <div class="modal prefs-modal" onclick="event.stopPropagation()">
    <div class="modal-header">
      <h2>&#9881; Preferencje</h2>
      <button class="modal-close" id="btnClosePrefs" onclick="cancelPrefs()" title="Anuluj">&times;</button>
    </div>
    <div class="prefs-tabs">
      <button class="prefs-tab active" id="ptab-general" onclick="switchPrefsTab('general',this)">Ogólne</button>
      <button class="prefs-tab" id="ptab-export" onclick="switchPrefsTab('export',this)">Eksport</button>
      <button class="prefs-tab" id="ptab-network" onclick="switchPrefsTab('network',this)">Sieć</button>
      <button class="prefs-tab" id="ptab-email" onclick="switchPrefsTab('email',this)">Email</button>
      <button class="prefs-tab" id="ptab-appearance" onclick="switchPrefsTab('appearance',this)">Wygląd</button>
    </div>
    <div style="overflow-y:auto;flex:1;padding:.6rem 1rem">
      <div class="prefs-pane" id="pane-general">
        <div class="pref-row">
          <span class="pref-label">Katalog wyjściowy</span>
          <div style="display:flex;gap:.3rem">
            <input id="outputDir" type="text" value="." placeholder="/tmp/faktury" style="width:220px">
            <button class="btn-primary" type="button" onclick="openBrowser()" style="padding:.4rem .6rem;font-size:.8rem" title="Wybierz folder">&#128193;</button>
          </div>
        </div>
        <div class="pref-row">
          <span class="pref-label">Separuj po NIP</span>
          <label style="display:flex;align-items:center;gap:.4rem;cursor:pointer;font-size:.85rem"><input type="checkbox" id="separateByNip"> <span id="profileNameLabel"></span></label>
        </div>
        <div class="pref-row">
          <span class="pref-label">Nazwy plików</span>
          <label style="display:flex;align-items:center;gap:.4rem;cursor:pointer;font-size:.85rem"><input type="checkbox" id="customFilenames"> data-sprzedawca-waluta-ksef</label>
        </div>
        <div class="pref-row">
          <span class="pref-label">Auto-odświeżanie (min)</span>
          <div style="display:flex;align-items:center;gap:.5rem">
            <input id="autoRefreshMinutes" type="number" value="0" min="0" max="1440" step="1"
                   style="width:5rem"
                   title="0 = wyłączone, min. 10 minut">
            <span style="font-size:.75rem;color:#999">0 = wyłączone, min. 10</span>
          </div>
        </div>
        <div class="pref-row">
          <span class="pref-label">Wiersze na ekranie</span>
          <div style="display:flex;align-items:center;gap:.5rem">
            <select id="displayLimit" onchange="renderTable()">
              <option value="5">5</option>
              <option value="10">10</option>
              <option value="50" selected>50</option>
              <option value="100">100</option>
            </select>
            <span style="font-size:.75rem;color:#999">wierszy na stronie</span>
          </div>
        </div>
      </div>
      <div class="prefs-pane" id="pane-network" style="display:none">
        <div class="pref-row">
          <span class="pref-label">Port nasłuchiwania</span>
          <div style="display:flex;align-items:center;gap:.5rem">
            <input id="lanPort" type="number" value="18150" min="1024" max="65535" style="width:95px">
            <span style="font-size:.75rem;color:#999">wymaga restartu</span>
          </div>
        </div>
        <div class="pref-row">
          <span class="pref-label">Tryb nasłuchiwania</span>
          <div style="display:flex;flex-direction:column;gap:.4rem;font-size:.85rem">
            <label style="display:flex;align-items:center;gap:.4rem;cursor:pointer">
              <input type="radio" name="listenMode" id="listenLocal" value="local"> Tylko localhost (127.0.0.1)
            </label>
            <label style="display:flex;align-items:center;gap:.4rem;cursor:pointer">
              <input type="radio" name="listenMode" id="listenAll" value="all"> Sieć lokalna (0.0.0.0) — wymaga restartu
            </label>
          </div>
        </div>
        <div class="pref-row" style="align-items:flex-start">
          <span class="pref-label" style="padding-top:.1rem">Aktualny adres</span>
          <span id="serverUrl" style="font-size:.85rem;font-family:monospace;word-break:break-all"></span>
        </div>
      </div>
      <div class="prefs-pane" id="pane-export" style="display:none">
        <div class="pref-row">
          <span class="pref-label">Formaty eksportu</span>
          <div style="display:flex;gap:.9rem;align-items:center;flex-wrap:wrap">
            <label style="display:flex;align-items:center;gap:.3rem;cursor:pointer;font-size:.85rem"><input type="checkbox" id="expXml" checked> XML</label>
            <label style="display:flex;align-items:center;gap:.3rem;cursor:pointer;font-size:.85rem"><input type="checkbox" id="expPdf" checked> PDF</label>
            <label style="display:flex;align-items:center;gap:.3rem;cursor:pointer;font-size:.85rem"><input type="checkbox" id="expJson"> JSON</label>
          </div>
        </div>
        <div class="pref-row">
          <span class="pref-label">Schemat kolorów PDF</span>
          <select id="pdfColorScheme">
            <option value="navy">Granatowy</option>
            <option value="forest">Zielony</option>
            <option value="slate">Szary</option>
          </select>
        </div>
      </div>
      <div class="prefs-pane" id="pane-email" style="display:none">
        <div class="pref-row">
          <span class="pref-label">Serwer SMTP</span>
          <input type="text" id="smtpHost" placeholder="smtp.gmail.com" style="flex:1">
        </div>
        <div class="pref-row">
          <span class="pref-label">Protokół</span>
          <select id="smtpSecurity" onchange="onSmtpSecurityChange()" style="flex:1">
            <option value="StartTls">STARTTLS (port 587)</option>
            <option value="None">Brak szyfrowania (port 25)</option>
          </select>
        </div>
        <div class="pref-row">
          <span class="pref-label">Port</span>
          <input type="number" id="smtpPort" placeholder="587" min="1" max="65535" style="width:6rem">
        </div>
        <div class="pref-row">
          <span class="pref-label">Użytkownik</span>
          <input type="text" id="smtpUser" placeholder="user@example.com" autocomplete="off" style="flex:1">
        </div>
        <div class="pref-row">
          <span class="pref-label">Hasło</span>
          <input type="password" id="smtpPassword" autocomplete="new-password" style="flex:1">
        </div>
        <div class="pref-row">
          <span class="pref-label">Nadawca (From)</span>
          <input type="text" id="smtpFrom" placeholder="KSeFCli &lt;noreply@example.com&gt;" style="flex:1">
        </div>
        <div class="pref-row" style="border-top:2px solid #e0e0e0;margin-top:.5rem;padding-top:.75rem">
          <span class="pref-label">Testuj wysyłkę</span>
          <div style="display:flex;gap:.5rem;flex:1;align-items:center">
            <input type="email" id="smtpTestTo" placeholder="odbiorca@example.com" style="flex:1">
            <button class="btn-sm btn-prefs" id="btnTestEmail" onclick="sendTestEmail()">&#9993; Wyślij test</button>
          </div>
        </div>
        <div id="smtpTestResult" style="display:none;font-size:.8rem;padding:.3rem 0 0 0"></div>
        <div class="prefs-panel" style="margin-top:.5rem;font-size:.8rem;color:#666">
          Adres e-mail odbiorcy dla powiadomień konfiguruje się w <strong>Konfiguracja → karta profilu</strong>.
        </div>
      </div>
      <div class="prefs-pane" id="pane-appearance" style="display:none">
        <div class="pref-row">
          <span class="pref-label">Tryb ciemny (GUI)</span>
          <label style="display:flex;align-items:center;gap:.4rem;cursor:pointer;font-size:.85rem"><input type="checkbox" id="darkMode" onchange="toggleDarkMode()"> Włącz</label>
        </div>
        <div class="pref-row">
          <span class="pref-label">Podgląd faktury ciemny</span>
          <label style="display:flex;align-items:center;gap:.4rem;cursor:pointer;font-size:.85rem"><input type="checkbox" id="previewDarkMode"> Włącz</label>
        </div>
        <div class="pref-row">
          <span class="pref-label">Testuj powiadomienia</span>
          <button class="btn-sm btn-prefs" onclick="sendSampleNotification()">&#128276; Wyślij testowe</button>
        </div>
        <div class="pref-row">
          <span class="pref-label">Format logów konsoli</span>
          <label style="display:flex;align-items:center;gap:.4rem;cursor:pointer;font-size:.85rem"><input type="checkbox" id="jsonConsoleLog"> JSON</label>
        </div>
        <div class="pref-row">
          <span class="pref-label">Wykres netto + VAT</span>
          <label style="display:flex;align-items:center;gap:.4rem;cursor:pointer;font-size:.85rem"><input type="checkbox" id="showIncomeChart" onchange="showIncomeChart=this.checked;buildCurrencyFilter()"> Włącz</label>
        </div>
      </div>
    </div>
    <div class="modal-footer" style="justify-content:flex-end;gap:.5rem">
      <span id="prefsSaveErr" style="display:none;font-size:.8rem;color:#c62828"></span>
      <button class="btn-sm btn-outline" id="btnCancelPrefs" onclick="cancelPrefs()" style="padding:.4rem 1rem">Anuluj</button>
      <button class="btn-prefs" id="btnSavePrefs" onclick="saveAndClosePrefs()" style="padding:.4rem 1.2rem">Zapisz preferencje</button>
    </div>
  </div>
</div>
<div id="setupBanner" style="display:none;align-items:center;gap:.8rem;background:#fff3e0;border:1px solid #ffb300;border-radius:8px;padding:.7rem 1rem;margin-bottom:.5rem;font-size:.9rem;color:#6d4c00">
  <span>&#9888;</span>
  <span>Brak konfiguracji. Skonfiguruj profil w edytorze, aby korzystac z aplikacji.</span>
  <button class="btn-config" onclick="openConfigEditor()" style="margin-left:auto;padding:.3rem .8rem;font-size:.82rem">&#9998; Otwórz edytor</button>
</div>
<div id="toast-container"></div>
<div class="progress-wrap" id="progressWrap"><div class="progress-bar" id="bar"></div></div>
<div class="filter-bar" id="filterBar"></div>
<div class="sel-toolbar" id="selToolbar">
  <button class="btn-sm btn-outline" onclick="selectAll()">Zaznacz wszystkie</button>
  <button class="btn-sm btn-outline" onclick="clearSelection()">Odznacz wszystkie</button>
  <button class="btn-sm btn-outline" onclick="selectMissing()">Zaznacz brakujące</button>
  <span class="sel-count" id="selCount"></span>
</div>
<div id="tableWrap"></div>
<div class="toolbar" id="downloadBar" style="display:none">
  <button class="btn-success" id="btnDownloadSel" onclick="doDownload(true)">Zapisz zaznaczone</button>
  <button class="btn-success" id="btnDownload" onclick="doDownload(false)" style="background:#1976d2">Zapisz wszystkie</button>
  <button class="btn-success" id="btnBrowserDownload" onclick="doBrowserDownload()" style="background:#e65100">Pobierz PDF</button>
  <button class="btn-success" id="btnSummary" onclick="doSummary()" style="background:#7b1fa2" disabled>Zapisz CSV</button>
  <button class="btn-success" id="btnSummaryBrowser" onclick="doBrowserSummary()" style="background:#e65100" disabled>Pobierz CSV</button>
  <span class="count" id="countLabel"></span>
</div>
<div class="modal-overlay" id="folderModal">
  <div class="modal">
    <div class="modal-header">
      <h2>Wybierz folder</h2>
      <button class="modal-close" onclick="closeBrowser()">&times;</button>
    </div>
    <div class="modal-path">
      <span class="modal-path-text" id="browseCurrentPath"></span>
    </div>
    <div class="dir-list" id="dirList"></div>
    <div class="modal-footer">
      <div class="new-dir">
        <input id="newDirName" type="text" placeholder="Nowy folder...">
        <button class="btn-primary" onclick="createDir()" style="padding:.3rem .6rem;font-size:.8rem">Utworz</button>
      </div>
      <button class="btn-success" onclick="selectCurrentDir()" style="padding:.4rem 1rem">Wybierz</button>
    </div>
  </div>
</div>
<div class="modal-overlay" id="configModal">
  <div class="modal cfg-modal" onclick="event.stopPropagation()">
    <div class="modal-header">
      <h2>&#9998; Konfiguracja</h2>
      <button class="modal-close" onclick="closeConfigEditor()">&times;</button>
    </div>
    <div class="modal-path" id="cfgFilePath" style="font-family:monospace;font-size:.78rem"></div>
    <div style="overflow-y:auto;flex:1;padding:.8rem 1rem" id="cfgBody">
      <div style="text-align:center;color:#999;padding:2rem">Wczytywanie...</div>
    </div>
    <div class="modal-footer" style="justify-content:flex-end;gap:.5rem">
      <span id="cfgSaveMsg" style="font-size:.8rem;color:#2e7d32;display:none"></span>
      <span id="cfgErrMsg" style="font-size:.8rem;color:#c62828;display:none"></span>
      <button class="btn-sm btn-outline" onclick="addProfile()">+ Dodaj profil</button>
      <button class="btn-sm btn-outline" onclick="closeConfigEditor()" style="padding:.4rem 1rem">Anuluj</button>
      <button class="btn-success" onclick="saveConfigEditor()" style="padding:.4rem 1.2rem">Zapisz</button>
    </div>
  </div>
</div>
<div class="modal-overlay" id="aboutModal" onclick="this.classList.remove('visible')">
  <div class="modal" style="width:520px;max-height:80vh;display:flex;flex-direction:column" onclick="event.stopPropagation()">
    <div class="modal-header">
      <h2>&#9432; O programie</h2>
      <button class="modal-close" onclick="$('aboutModal').classList.remove('visible')">&times;</button>
    </div>
    <div style="padding:1.2rem 1.4rem;font-size:.88rem;line-height:1.7;overflow-y:auto;flex:1">
      <div id="aboutBody" style="color:#555">Wczytywanie...</div>
    </div>
  </div>
</div>
<script>
const $ = id => document.getElementById(id);
const bar = $('bar'), progressWrap = $('progressWrap'),
      tableWrap = $('tableWrap'), downloadBar = $('downloadBar'), filterBar = $('filterBar'),
      selToolbar = $('selToolbar'), selCount = $('selCount'),
      btnSearch = $('btnSearch'), btnDownload = $('btnDownload'), btnDownloadSel = $('btnDownloadSel'), btnBrowserDownload = $('btnBrowserDownload'),
      btnSummary = $('btnSummary'), btnSummaryBrowser = $('btnSummaryBrowser'), countLabel = $('countLabel');
let invoices = [], total = 0, completed = 0, sortCol = null, sortAsc = true, es = null;
let profileSwitchGen = 0; // incremented on every profile switch; stale async results discard themselves
let activeCurrencies = new Set();
let selectedInvoices = new Set();
let fileStatus = [];
let autoRefreshTimer = null;
let displayAll = false;
let currentPage = 0;
let refreshRunning = false; // guard against concurrent silentRefresh() calls
const profileBadges = {}; // profileName → unread new-invoice count for dropdown badge
let knownInvoiceKsefNumbers = null; // null = not yet baselined; Set after first search
let lastSearchParams = null;        // params of last successful search; null = no search yet
let showIncomeChart = true;         // opt-out pref — read from /prefs on load
let fxRates = {};                   // { EUR: 4.25, HUF: 0.011, ... } — PLN per 1 unit, fetched from NBP
let fxRatesFetchedAt = 0;           // epoch ms of last successful fetch
let fxRatesFetchInProgress = false; // true while the NBP request is in-flight
let fxRatesFetchFailed = false;     // true after a failed fetch (distinct from "never fetched")

// Set default month to current month and keep Od/Do consistent
(function initDates() {
  const now = new Date();
  const cur = now.getFullYear() + '-' + String(now.getMonth() + 1).padStart(2, '0');
  $('fromDate').value = cur;
  $('toDate').value = cur;
  $('fromDate').max = cur;
  $('toDate').min = cur;   // initialise min to match the default fromDate
  $('toDate').max = cur;

  // When Od changes: if Do is now before Od, advance Do to match Od
  $('fromDate').addEventListener('change', () => {
    const from = $('fromDate').value;
    $('toDate').min = from || '2026-02';
    if ($('toDate').value && $('toDate').value < from) {
      $('toDate').value = from;
    }
  });
})();

async function loadCachedInvoices() {
  const myGen = profileSwitchGen;
  try {
    const res = await fetch('/cached-invoices');
    if (!res.ok) return;
    const data = await res.json();
    if (profileSwitchGen !== myGen) return; // profile switched while fetching — discard stale result
    if (!data.invoices || data.invoices.length === 0) return;
    invoices = data.invoices;
    for (let i = 0; i < invoices.length; i++) invoices[i]._idx = i;
    total = invoices.length;
    countLabel.textContent = total + ' faktur';
    if (data.params) {
      if (data.params.subjectType) $('subjectType').value = data.params.subjectType;
      if (data.params.dateType) $('dateType').value = data.params.dateType;
      const fromISO = data.params.from;
      if (fromISO && fromISO !== 'thismonth') {
        $('fromDate').value = fromISO.substring(0, 7);
        $('toDate').min = fromISO.substring(0, 7); // keep min in sync — change event not fired on programmatic set
      }
      if (data.params.to) $('toDate').value = data.params.to.substring(0, 7);
      lastSearchParams = {
        subjectType: data.params.subjectType,
        fromDate: fromISO && fromISO !== 'thismonth' ? fromISO.substring(0, 7) : '',
        toDate: data.params.to ? data.params.to.substring(0, 7) : '',
        dateType: data.params.dateType
      };
      knownInvoiceKsefNumbers = new Set(invoices.map(i => i.ksefNumber));
    }
    refreshExchangeRates(); // sets fxRatesFetchInProgress before buildCurrencyFilter renders
    buildCurrencyFilter();
    displayAll = false;
    currentPage = 0;
    renderTable();
    downloadBar.style.display = total > 0 ? 'flex' : 'none';
    checkExisting();
    setStatus('Zaladowano ' + total + ' faktur z pamięci podręcznej.', 'idle');
  } catch (e) { /* silent */ }
}

// Load saved preferences
let currentSessionProfile = '';
async function loadPrefs() {
  try {
    const res = await fetch('/prefs');
    if (res.ok) {
      const p = await res.json();
      if (p.outputDir) $('outputDir').value = p.outputDir;
      if (p.exportXml != null) $('expXml').checked = p.exportXml;
      if (p.exportJson != null) $('expJson').checked = p.exportJson;
      if (p.exportPdf != null) $('expPdf').checked = p.exportPdf;
      if (p.customFilenames != null) $('customFilenames').checked = p.customFilenames;
      if (p.separateByNip != null) $('separateByNip').checked = p.separateByNip;
      if (p.lanPort) $('lanPort').value = p.lanPort;
      $('listenLocal').checked = !p.listenOnAll;
      $('listenAll').checked = !!p.listenOnAll;
      if (p.serverUrl) $('serverUrl').textContent = p.serverUrl;
      $('darkMode').checked = !!p.darkMode; document.body.classList.toggle('dark', !!p.darkMode);
      $('previewDarkMode').checked = !!p.previewDarkMode;
      if (p.pdfColorScheme) $('pdfColorScheme').value = p.pdfColorScheme;
      if (p.jsonConsoleLog) $('jsonConsoleLog').checked = p.jsonConsoleLog;
      $('autoRefreshMinutes').value = p.autoRefreshMinutes ?? 0;
      if (p.displayLimit) $('displayLimit').value = p.displayLimit;
      showIncomeChart = p.showIncomeChart !== false; // default true (opt-out)
      $('showIncomeChart').checked = showIncomeChart;
      $('smtpHost').value = p.smtpHost || '';
      $('smtpPort').value = p.smtpPort || 587;
      $('smtpSecurity').value = p.smtpSecurity || 'StartTls';
      $('smtpUser').value = p.smtpUser || '';
      $('smtpPassword').value = '';
      $('smtpPassword').placeholder = p.hasSmtpPassword ? '(hasło jest ustawione)' : '';
      $('smtpFrom').value = p.smtpFrom || '';
      startAutoRefresh(parseInt($('autoRefreshMinutes').value) || 0);
      if (p.profileName) $('profileNameLabel').textContent = '(' + p.profileName + ')';
      // Populate profile dropdown
      if (p.allProfiles) {
        const sel = $('profileSelect');
        sel.innerHTML = '';
        const entries = Object.entries(p.allProfiles);
        for (const [name, nip] of entries) {
          const opt = document.createElement('option');
          opt.value = name;
          opt.textContent = name + ' (NIP: ' + nip + ')';
          if (name === p.selectedProfile) opt.selected = true;
          sel.appendChild(opt);
        }
        currentSessionProfile = p.selectedProfile || '';
        if (entries.length <= 1) sel.disabled = true;
      }
      applySetupMode(!!p.setupRequired);
    }
  } catch {}
}
loadPrefs().then(() => loadCachedInvoices());

function applySetupMode(required) {
  const banner = $('setupBanner');
  if (banner) banner.style.display = required ? 'flex' : 'none';
  const blocked = [btnSearch, btnDownload, btnDownloadSel, btnBrowserDownload, $('btnAuth')];
  for (const b of blocked) { if (b) b.disabled = required; }
  if (required) setTimeout(() => openConfigEditor(), 400);
}

// --- Token status ---
let tokenExpiry = null; // Date object for access token expiry
let tokenRefreshExpiry = null;
let searchRunning = false;

async function fetchTokenStatus() {
  try {
    const res = await fetch('/token-status');
    if (!res.ok) return;
    const data = await res.json();
    if (data.accessTokenValidUntil) {
      tokenExpiry = new Date(data.accessTokenValidUntil);
    } else {
      tokenExpiry = null;
    }
    if (data.refreshTokenValidUntil) {
      tokenRefreshExpiry = new Date(data.refreshTokenValidUntil);
    }
    updateAuthButton();
  } catch {}
}

function updateAuthButton() {
  const btn = $('btnAuth');
  const info = $('tokenInfo');
  btn.classList.remove('auth-warning', 'auth-expired', 'auth-unknown');

  if (!tokenExpiry) {
    btn.classList.add('auth-unknown');
    info.textContent = 'brak tokenu';
    if (!searchRunning) $('btnSearch').disabled = true;
    return;
  }

  const now = new Date();
  const diffMs = tokenExpiry - now;
  const diffMin = diffMs / 60000;
  const timeStr = tokenExpiry.toLocaleTimeString('pl-PL', { hour: '2-digit', minute: '2-digit', second: '2-digit' });

  if (diffMs <= 0) {
    btn.classList.add('auth-expired');
    info.textContent = 'token wygasl (' + timeStr + ')';
    info.style.color = '#c62828';
    if (!searchRunning) $('btnSearch').disabled = true;
  } else if (diffMin <= 5) {
    btn.classList.add('auth-warning');
    info.textContent = 'wygasa o ' + timeStr;
    info.style.color = '#e65100';
    if (!searchRunning) $('btnSearch').disabled = false;
  } else {
    info.textContent = 'wazny do ' + timeStr;
    info.style.color = '#2e7d32';
    if (!searchRunning) $('btnSearch').disabled = false;
  }
}

// Fetch token and notification status on load and periodically
fetchTokenStatus();
fetchNotifStatus();
setInterval(() => { updateAuthButton(); }, 15000);
setInterval(() => { fetchTokenStatus(); }, 60000);
setInterval(() => { fetchNotifStatus(); }, 120000);

async function savePrefs() {
  const prefs = {
    outputDir: $('outputDir').value || '.',
    exportXml: $('expXml').checked,
    exportJson: $('expJson').checked,
    exportPdf: $('expPdf').checked,
    customFilenames: $('customFilenames').checked,
    separateByNip: $('separateByNip').checked,
    darkMode: $('darkMode').checked,
    previewDarkMode: $('previewDarkMode').checked,
    pdfColorScheme: $('pdfColorScheme').value,
    selectedProfile: $('profileSelect').value,
    lanPort: parseInt($('lanPort').value) || 18150,
    listenOnAll: $('listenAll').checked,
    autoRefreshMinutes: parseInt($('autoRefreshMinutes').value) || 0,
    jsonConsoleLog: $('jsonConsoleLog').checked,
    displayLimit: parseInt($('displayLimit').value) || 50,
    showIncomeChart: $('showIncomeChart').checked,
    smtpHost: $('smtpHost').value || null,
    smtpPort: parseInt($('smtpPort').value) || null,
    smtpSecurity: $('smtpSecurity').value || 'StartTls',
    smtpUser: $('smtpUser').value || null,
    smtpPassword: $('smtpPassword').value || null,
    smtpFrom: $('smtpFrom').value || null
  };
  const mins = prefs.autoRefreshMinutes;
  // Values 1–9 are below the server minimum of 10 — normalise to 0 (disabled) so the timer,
  // notification-permission request, and the persisted value are all consistent.
  const effectiveMins = (mins > 0 && mins < 10) ? 0 : mins;
  prefs.autoRefreshMinutes = effectiveMins;
  const resp = await fetch('/prefs', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify(prefs) });
  if (!resp.ok) throw new Error('HTTP ' + resp.status);
  const data = await resp.json();
  if (data?.error) throw new Error(data.error);
  // Only mutate runtime state after the save is confirmed on disk
  startAutoRefresh(effectiveMins);
  if (effectiveMins > 0) requestNotificationPermission();
  showIncomeChart = prefs.showIncomeChart;
  buildCurrencyFilter();
}

async function onProfileChange() {
  const chosen = $('profileSelect').value;
  if (profileBadges[chosen]) {
    delete profileBadges[chosen];
    updateProfileSelectBadges();
  }
  try {
    await savePrefs(); // commits the new active profile to the server; must succeed before any UI change
  } catch (err) {
    $('profileSelect').value = currentSessionProfile; // revert dropdown to last known-good profile
    setStatus('Błąd zmiany profilu: ' + err.message, 'error');
    return; // abort — server still has the old profile; do not clear UI or load wrong cache
  }
  profileSwitchGen++; // server confirmed switch; invalidate in-flight results from the old profile
  currentSessionProfile = chosen; // keep in sync with what server confirmed
  // Clear all results and token status immediately on profile switch
  tableWrap.innerHTML = '';
  invoices = []; total = 0; completed = 0; sortCol = null;
  activeCurrencies = new Set();
  selectedInvoices = new Set();
  fileStatus = [];
  filterBar.classList.remove('visible');
  selToolbar.classList.remove('visible');
  downloadBar.style.display = 'none';
  progressWrap.classList.remove('visible');
  countLabel.textContent = '';
  lastSearchParams = null;
  knownInvoiceKsefNumbers = null;
  buildCurrencyFilter();
  renderTable();
  setStatus('Profil zmieniony na "' + chosen + '". Kliknij Szukaj aby wyszukać faktury.', 'idle');
  await fetchTokenStatus();
}

function togglePrefs() { openPrefs(); }
function openPrefs() { $('prefsModal').classList.add('visible'); }
function closePrefs() { $('prefsModal').classList.remove('visible'); }
async function cancelPrefs() { await loadPrefs(); closePrefs(); }
async function saveAndClosePrefs() {
  const btn = $('btnSavePrefs'), errEl = $('prefsSaveErr');
  const cancelBtns = [$('btnCancelPrefs'), $('btnClosePrefs')];
  errEl.style.display = 'none';
  btn.disabled = true;
  cancelBtns.forEach(b => { if (b) b.disabled = true; });
  try {
    await savePrefs();
    closePrefs();
  } catch (err) {
    errEl.textContent = 'Błąd zapisu — ' + (err?.message || 'sprawdź połączenie z serwerem.');
    errEl.style.display = '';
  } finally {
    btn.disabled = false;
    cancelBtns.forEach(b => { if (b) b.disabled = false; });
  }
}
function switchPrefsTab(name, btn) {
  document.querySelectorAll('.prefs-pane').forEach(p => p.style.display = 'none');
  document.querySelectorAll('.prefs-tab').forEach(t => t.classList.remove('active'));
  $('pane-' + name).style.display = 'block';
  btn.classList.add('active');
}

const smtpDefaultPorts = { StartTls: 587, None: 25 };
function onSmtpSecurityChange() {
  const sel = $('smtpSecurity');
  const portEl = $('smtpPort');
  if (!sel || !portEl) return;
  portEl.value = smtpDefaultPorts[sel.value] ?? 587;
}

// ---- Config editor ----
let cfgData = null;

async function openConfigEditor() {
  $('configModal').classList.add('visible');
  $('cfgBody').innerHTML = '<div style="text-align:center;color:#999;padding:2rem">Wczytywanie...</div>';
  $('cfgSaveMsg').style.display = 'none';
  $('cfgErrMsg').style.display = 'none';
  try {
    const res = await fetch('/config-editor');
    cfgData = await res.json();
    $('cfgFilePath').textContent = cfgData.configFilePath || '';
    renderConfigEditor();
  } catch(e) {
    $('cfgBody').innerHTML = '<div style="color:#c62828;padding:1rem">Blad: ' + e.message + '</div>';
  }
}

function closeConfigEditor() {
  $('configModal').classList.remove('visible');
}

async function openAbout() {
  $('aboutModal').classList.add('visible');
  try {
    const r = await fetch('/about');
    const d = await r.json();
    const gh = d.github
      ? '<a href="' + d.github + '" target="_blank" rel="noopener noreferrer" style="color:#1976d2;word-break:break-all">' + escHtml(d.github) + '</a>'
      : '—';
    const disclaimer =
      '<div style="margin-top:1.1rem;border-top:1px solid #e0e0e0;padding-top:.9rem">' +
        '<p style="font-weight:700;margin:0 0 .4rem">⚠ Zastrzeżenie / Disclaimer</p>' +
        '<p style="margin:0 0 .5rem;font-size:.8rem;color:#555">' +
          'Oprogramowanie stanowi narzędzie techniczne do komunikacji z KSeF i nie&nbsp;stanowi porady ' +
          'finansowej, księgowej ani&nbsp;podatkowej. Autorzy <strong>nie ponoszą odpowiedzialności</strong> za ' +
          'straty finansowe, kary podatkowe, odsetki ani&nbsp;inne konsekwencje prawne wynikające z&nbsp;użytkowania ' +
          'programu. Użytkownik samodzielnie odpowiada za&nbsp;zgodność z&nbsp;przepisami podatkowymi ' +
          'i&nbsp;powinien konsultować się z&nbsp;uprawnionym księgowym lub doradcą podatkowym.' +
        '</p>' +
        '<p style="margin:0;font-size:.8rem;color:#555">' +
          'This software is a technical tool for interfacing with KSeF and does not constitute ' +
          'financial, accounting, or tax advice. The authors accept <strong>no liability</strong> for ' +
          'financial losses, tax penalties, fines, or any other legal consequences arising from use of ' +
          'this software. You are solely responsible for compliance with applicable tax regulations ' +
          'and should consult a qualified accountant or tax advisor.' +
        '</p>' +
      '</div>';
    $('aboutBody').innerHTML =
      '<table style="border-collapse:collapse;width:100%;table-layout:fixed">' +
      '<col style="width:7rem"><col>' +
      '<tr><td style="padding:.25rem .5rem .25rem 0;color:#888;white-space:nowrap">Wersja</td>' +
        '<td style="padding:.25rem 0;font-family:monospace;word-break:break-all">' + escHtml(d.version || '—') + '</td></tr>' +
      '<tr><td style="padding:.25rem .5rem .25rem 0;color:#888;white-space:nowrap">Data budowy</td>' +
        '<td style="padding:.25rem 0;font-family:monospace;word-break:break-all">' + escHtml(d.buildDate || '—') + '</td></tr>' +
      '<tr><td style="padding:.25rem .5rem .25rem 0;color:#888;white-space:nowrap">Autor</td>' +
        '<td style="padding:.25rem 0">' + escHtml(d.author || '—') + '</td></tr>' +
      '<tr><td style="padding:.25rem .5rem .25rem 0;color:#888;white-space:nowrap">GitHub</td>' +
        '<td style="padding:.25rem 0">' + gh + '</td></tr>' +
      '</table>' + disclaimer;
  } catch(e) {
    $('aboutBody').innerHTML = '<span style="color:#c62828">Błąd: ' + escHtml(e.message) + '</span>';
  }
}

function renderConfigEditor() {
  if (!cfgData) return;
  let html = '';
  // Hidden active profile selector (value managed by per-card radio buttons)
  html += '<select id="cfgActiveProfile" style="display:none">';
  for (const p of cfgData.profiles) {
    const sel = p.name === cfgData.activeProfile ? ' selected' : '';
    html += '<option value="' + esc(p.name) + '"' + sel + '>' + esc(p.name) + '</option>';
  }
  html += '</select>';
  // Profile cards
  for (let i = 0; i < cfgData.profiles.length; i++) {
    html += renderProfileCard(cfgData.profiles[i], i);
  }
  $('cfgBody').innerHTML = html;
  // Sync auth method visibility
  for (let i = 0; i < cfgData.profiles.length; i++) toggleAuthFields(i);
}

function renderProfileCard(p, i) {
  const am = p.authMethod || 'token';
  const tokenVal = p.token || '';
  const isActive = p.name === cfgData.activeProfile;
  return '<div class="cfg-profile-card" id="cfgCard' + i + '">' +
    '<div class="cfg-card-title">Profil #' + (i+1) +
    '<label style="font-weight:normal;font-size:.82rem;cursor:pointer;display:flex;align-items:center;gap:.3rem;margin-left:auto">' +
    '<input type="radio" name="activeProfileRadio" id="cfgActiveRadio' + i + '" value="' + esc(p.name) + '"' + (isActive ? ' checked' : '') + ' onchange="onActiveRadioChange(' + i + ')"> Domyślny</label>' +
    '</div>' +
    '<div class="cfg-field"><label>Nazwa profilu</label>' +
    '<input type="text" id="cfgName' + i + '" value="' + esc(p.name) + '" onchange="syncActiveProfileSelect()"></div>' +
    '<div class="cfg-field"><label>NIP</label>' +
    '<input type="text" id="cfgNip' + i + '" value="' + esc(p.nip || '') + '"></div>' +
    '<div class="cfg-field"><label>Srodowisko</label>' +
    '<select id="cfgEnv' + i + '">' +
    '<option value="test"' + (p.environment==='test'?' selected':'') + '>test</option>' +
    '<option value="demo"' + (p.environment==='demo'?' selected':'') + '>demo</option>' +
    '<option value="prod"' + (p.environment==='prod'?' selected':'') + '>prod</option>' +
    '</select></div>' +
    '<div class="cfg-field"><label>Metoda uwierzytelniania</label>' +
    '<select id="cfgAuth' + i + '" onchange="toggleAuthFields(' + i + ')">' +
    '<option value="token"' + (am==='token'?' selected':'') + '>Token</option>' +
    '<option value="certificate"' + (am==='certificate'?' selected':'') + '>Certyfikat</option>' +
    '</select></div>' +
    '<div id="cfgTokenSection' + i + '">' +
    '<div class="cfg-field"><label>Token</label>' +
    '<div class="cfg-pw-wrap">' +
    '<input type="password" id="cfgToken' + i + '" value="' + esc(tokenVal) + '" autocomplete="off">' +
    '<button class="btn-sm btn-outline" onclick="toggleTokenVis(' + i + ')">&#128065;</button>' +
    '</div></div></div>' +
    '<div id="cfgCertSection' + i + '">' +
    '<div class="cfg-field"><label>Plik klucza prywatnego</label>' +
    '<input type="text" id="cfgCertKey' + i + '" value="' + esc(p.certPrivateKeyFile||'') + '" placeholder="~/klucz.key"></div>' +
    '<div class="cfg-field"><label>Plik certyfikatu</label>' +
    '<input type="text" id="cfgCertFile' + i + '" value="' + esc(p.certCertificateFile||'') + '" placeholder="~/cert.pem"></div>' +
    '<div class="cfg-field"><label>Haslo certyfikatu</label>' +
    '<input type="password" id="cfgCertPass' + i + '" value="' + esc(p.certPassword||'') + '" autocomplete="off"></div>' +
    '<div class="cfg-field"><label>Haslo z env var (opcjonalnie)</label>' +
    '<input type="text" id="cfgCertPassEnv' + i + '" value="' + esc(p.certPasswordEnv||'') + '" placeholder="KSEF_CERT_PASSWORD"></div>' +
    '<div class="cfg-field"><label>Haslo z pliku (opcjonalnie)</label>' +
    '<input type="text" id="cfgCertPassFile' + i + '" value="' + esc(p.certPasswordFile||'') + '" placeholder="~/password.txt"></div>' +
    '</div>' +
    '<div class="cfg-field">' +
    '<label>Slack Webhook URL (opcjonalnie)</label>' +
    '<input type="url" id="cfgSlackWebhook' + i + '" value="' + esc(p.slackWebhookUrl||'') + '" placeholder="https://hooks.slack.com/services/...">' +
    '</div>' +
    '<div class="cfg-field">' +
    '<label>Teams Webhook URL (opcjonalnie)</label>' +
    '<input type="url" id="cfgTeamsWebhook' + i + '" value="' + esc(p.teamsWebhookUrl||'') + '" placeholder="https://...webhook.office.com/...">' +
    '</div>' +
    '<div class="cfg-field">' +
    '<label>E-mail powiadomień (opcjonalnie)</label>' +
    '<input type="email" id="cfgNotifEmail' + i + '" value="' + esc(p.notificationEmail||'') + '" placeholder="odbiorca@example.com">' +
    '</div>' +
    '<div class="cfg-card-footer">' +
    '<div style="display:flex;flex-direction:column;gap:.35rem">' +
    '<label style="display:flex;align-items:center;gap:.5rem;cursor:pointer;font-size:.85rem">' +
    '<input type="checkbox" id="cfgAutoRefresh' + i + '"' + (p.includeInAutoRefresh ? ' checked' : '') + '>' +
    ' Uwzględnij w auto-odświeżaniu (tło)</label>' +
    '<label style="display:flex;align-items:center;gap:.5rem;cursor:pointer;font-size:.85rem">' +
    '<input type="checkbox" id="cfgAutoRefreshCurrentMonth' + i + '"' + (p.autoRefreshCurrentMonth !== false ? ' checked' : '') + '>' +
    ' Auto-odświeżanie: ogranicz do bieżącego miesiąca</label>' +
    '<label style="display:flex;align-items:center;gap:.5rem;cursor:pointer;font-size:.85rem">' +
    '<input type="checkbox" id="cfgExtNotif' + i + '"' + (p.extendedNotifications ? ' checked' : '') + '>' +
    ' Rozszerzone powiadomienia (data, NIP, nazwa firmy)</label>' +
    '</div>' +
    '<button type="button" class="btn-sm btn-danger" onclick="deleteProfile(' + i + ')" style="align-self:flex-end">Usuń profil</button>' +
    '</div>' +
    '</div>';
}

function toggleAuthFields(i) {
  const am = document.getElementById('cfgAuth' + i)?.value || 'token';
  const ts = $('cfgTokenSection' + i);
  const cs = $('cfgCertSection' + i);
  if (ts) ts.style.display = am === 'token' ? '' : 'none';
  if (cs) cs.style.display = am === 'certificate' ? '' : 'none';
}

function toggleTokenVis(i) {
  const el = $('cfgToken' + i);
  if (el) el.type = el.type === 'password' ? 'text' : 'password';
}

function syncActiveProfileSelect() {
  const sel = $('cfgActiveProfile');
  if (!sel || !cfgData) return;
  const prev = sel.value;
  sel.innerHTML = '';
  for (let i = 0; i < cfgData.profiles.length; i++) {
    const name = document.getElementById('cfgName' + i)?.value || cfgData.profiles[i].name;
    const opt = document.createElement('option');
    opt.value = name;
    opt.textContent = name;
    if (name === prev) opt.selected = true;
    sel.appendChild(opt);
    // Sync radio button value when name changes
    const radio = document.getElementById('cfgActiveRadio' + i);
    if (radio) radio.value = name;
  }
}

function onActiveRadioChange(i) {
  const name = document.getElementById('cfgName' + i)?.value || (cfgData.profiles[i]?.name || '');
  const sel = $('cfgActiveProfile');
  if (sel) sel.value = name;
  cfgData.activeProfile = name;
}

function addProfile() {
  if (!cfgData) return;
  cfgData.profiles.push({ name: 'nowy-profil', nip: '', environment: 'test', authMethod: 'token', token: '' });
  renderConfigEditor();
  $('cfgCard' + (cfgData.profiles.length - 1))?.scrollIntoView({ behavior: 'smooth' });
}

function deleteProfile(i) {
  if (!cfgData) return;
  if (cfgData.profiles.length <= 1) { alert('Musi pozostać co najmniej jeden profil.'); return; }
  const displayName = document.getElementById('cfgName' + i)?.value || cfgData.profiles[i].name;
  if (!confirm('Usunąć profil "' + displayName + '"?')) return;
  // Capture active profile index before sync so a renamed-but-unsaved active profile is still found
  const activeIdx = cfgData.profiles.findIndex(p => p.name === cfgData.activeProfile);
  // Sync current form values into cfgData before splicing so other profiles' edits are preserved
  for (let j = 0; j < cfgData.profiles.length; j++) {
    const am = document.getElementById('cfgAuth' + j)?.value || 'token';
    cfgData.profiles[j] = {
      ...cfgData.profiles[j],
      name: document.getElementById('cfgName' + j)?.value ?? cfgData.profiles[j].name,
      nip: document.getElementById('cfgNip' + j)?.value ?? cfgData.profiles[j].nip,
      environment: document.getElementById('cfgEnv' + j)?.value ?? cfgData.profiles[j].environment,
      authMethod: am,
      token: am === 'token' ? (document.getElementById('cfgToken' + j)?.value || '') : null,
      certPrivateKeyFile: am === 'certificate' ? (document.getElementById('cfgCertKey' + j)?.value || null) : null,
      certCertificateFile: am === 'certificate' ? (document.getElementById('cfgCertFile' + j)?.value || null) : null,
      certPassword: am === 'certificate' ? (document.getElementById('cfgCertPass' + j)?.value || null) : null,
      certPasswordEnv: am === 'certificate' ? (document.getElementById('cfgCertPassEnv' + j)?.value || null) : null,
      certPasswordFile: am === 'certificate' ? (document.getElementById('cfgCertPassFile' + j)?.value || null) : null,
      includeInAutoRefresh: document.getElementById('cfgAutoRefresh' + j)?.checked ?? cfgData.profiles[j].includeInAutoRefresh,
      autoRefreshCurrentMonth: document.getElementById('cfgAutoRefreshCurrentMonth' + j)?.checked ?? true,
      slackWebhookUrl: document.getElementById('cfgSlackWebhook' + j)?.value || null,
      teamsWebhookUrl: document.getElementById('cfgTeamsWebhook' + j)?.value || null,
      notificationEmail: document.getElementById('cfgNotifEmail' + j)?.value || null,
      extendedNotifications: document.getElementById('cfgExtNotif' + j)?.checked || false,
    };
  }
  // If deleting the active profile, reassign to the nearest remaining one
  if (activeIdx === i) {
    const next = cfgData.profiles.find((_, idx) => idx !== i);
    cfgData.activeProfile = next?.name || '';
  }
  cfgData.profiles.splice(i, 1);
  renderConfigEditor();
}

async function saveConfigEditor() {
  if (!cfgData) return;
  $('cfgSaveMsg').style.display = 'none';
  $('cfgErrMsg').style.display = 'none';
  // Collect current form state
  const activeProfile = $('cfgActiveProfile')?.value || '';
  const profiles = [];
  for (let i = 0; i < cfgData.profiles.length; i++) {
    const authMethod = document.getElementById('cfgAuth' + i)?.value || 'token';
    profiles.push({
      name: document.getElementById('cfgName' + i)?.value || '',
      nip: document.getElementById('cfgNip' + i)?.value || '',
      environment: document.getElementById('cfgEnv' + i)?.value || 'test',
      authMethod,
      token: authMethod === 'token' ? (document.getElementById('cfgToken' + i)?.value || '') : null,
      certPrivateKeyFile: authMethod === 'certificate' ? (document.getElementById('cfgCertKey' + i)?.value || null) : null,
      certCertificateFile: authMethod === 'certificate' ? (document.getElementById('cfgCertFile' + i)?.value || null) : null,
      certPassword: authMethod === 'certificate' ? (document.getElementById('cfgCertPass' + i)?.value || null) : null,
      certPasswordEnv: authMethod === 'certificate' ? (document.getElementById('cfgCertPassEnv' + i)?.value || null) : null,
      certPasswordFile: authMethod === 'certificate' ? (document.getElementById('cfgCertPassFile' + i)?.value || null) : null,
      includeInAutoRefresh: document.getElementById('cfgAutoRefresh' + i)?.checked || false,
      autoRefreshCurrentMonth: document.getElementById('cfgAutoRefreshCurrentMonth' + i)?.checked ?? true,
      slackWebhookUrl: document.getElementById('cfgSlackWebhook' + i)?.value || null,
      teamsWebhookUrl: document.getElementById('cfgTeamsWebhook' + i)?.value || null,
      notificationEmail: document.getElementById('cfgNotifEmail' + i)?.value || null,
      extendedNotifications: document.getElementById('cfgExtNotif' + i)?.checked || false,
    });
  }
  const payload = { activeProfile, configFilePath: cfgData.configFilePath, profiles };
  try {
    const res = await fetch('/config-editor', { method: 'POST', headers: {'Content-Type':'application/json'}, body: JSON.stringify(payload) });
    const data = await res.json();
    if (data.ok) {
      cfgData = payload;
      // Refresh profile dropdown and token status (new/edited profile = new token state)
      await loadPrefs();
      applySetupMode(false);
      await fetchTokenStatus();
      closeConfigEditor();
    } else {
      $('cfgErrMsg').textContent = data.error || 'Nieznany blad';
      $('cfgErrMsg').style.display = '';
    }
  } catch(e) {
    $('cfgErrMsg').textContent = e.message;
    $('cfgErrMsg').style.display = '';
  }
}

function esc(s) {
  return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
}

function toggleDarkMode() {
  document.body.classList.toggle('dark', $('darkMode').checked);
}

const toastContainer = document.getElementById('toast-container');
let _activeErrorToast = null;     // persistent — stays until dismissed or replaced
let _activeTransientToast = null; // transient — reused so rapid updates don't stack
let _activeTransientTimeout = null;
function setStatus(text, cls) {
  if (!text) { return; }
  if (cls === 'error') {
    // Replace existing error toast in-place rather than stacking
    if (_activeErrorToast) {
      _activeErrorToast.querySelector('.toast-msg').textContent = text;
      return;
    }
  } else {
    // Reuse the single transient toast — update text and reset the auto-dismiss timer
    if (_activeTransientToast) {
      _activeTransientToast.querySelector('.toast-msg').textContent = text;
      _activeTransientToast.className = 'toast ' + cls;
      clearTimeout(_activeTransientTimeout);
      _activeTransientTimeout = setTimeout(() => dismissToast(_activeTransientToast), 3500);
      return;
    }
  }
  const t = document.createElement('div');
  t.className = 'toast ' + cls;
  t.innerHTML = '<span class="toast-msg"></span><button class="toast-close" aria-label="Zamknij">\xd7</button>';
  t.querySelector('.toast-msg').textContent = text;
  t.querySelector('.toast-close').addEventListener('click', () => dismissToast(t));
  toastContainer.appendChild(t);
  if (cls === 'error') {
    _activeErrorToast = t;
  } else {
    _activeTransientToast = t;
    _activeTransientTimeout = setTimeout(() => dismissToast(t), 3500);
  }
}
function dismissToast(t) {
  if (!t || !t.parentNode) { return; }
  if (t === _activeErrorToast) { _activeErrorToast = null; }
  if (t === _activeTransientToast) {
    clearTimeout(_activeTransientTimeout);
    _activeTransientTimeout = null;
    _activeTransientToast = null;
  }
  t.classList.add('hiding');
  setTimeout(() => { if (t.parentNode) { t.parentNode.removeChild(t); } }, 380);
}

function getFilteredInvoices() {
  if (activeCurrencies.size === 0) return invoices;
  return invoices.filter(i => activeCurrencies.has(i.currency || ''));
}

const CHART_PALETTE = ['#1976d2','#388e3c','#f57c00','#8e24aa','#0097a7','#e53935','#546e7a'];
const VAT_COLOR = '#b0bec5'; // blue-grey 200 — intentionally outside CHART_PALETTE so it never matches a currency bar
function fmtAmt(v) {
  return v.toLocaleString('pl-PL', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
}

// Fetches daily average exchange rates from NBP Table A (CORS-open public API).
// Caches for 1 hour — NBP rates are updated once per business day.
// On success re-renders the currency filter bar so PLN equivalents appear without a page reload.
async function refreshExchangeRates() {
  if (Date.now() - fxRatesFetchedAt < 3_600_000) { return; }
  if (fxRatesFetchInProgress) { return; }
  fxRatesFetchInProgress = true;
  try {
    const res = await fetch('https://api.nbp.pl/api/exchangerates/tables/A/?format=json', { signal: AbortSignal.timeout(5000) });
    if (!res.ok) { fxRatesFetchFailed = true; buildCurrencyFilter(); return; }
    const data = await res.json();
    const updated = {};
    for (const rate of data[0].rates) { updated[rate.code] = rate.mid; }
    updated['PLN'] = 1;
    fxRates = updated;
    fxRatesFetchedAt = Date.now();
    fxRatesFetchFailed = false;
    console.log('[FX] NBP rates refreshed:', Object.keys(fxRates).length, 'currencies, effective', data[0].effectiveDate);
    buildCurrencyFilter(); // re-render so PLN equivalents appear immediately
  } catch (e) {
    fxRatesFetchFailed = true;
    console.warn('[FX] NBP rate fetch failed:', e.message);
    buildCurrencyFilter();
  } finally {
    fxRatesFetchInProgress = false;
  }
}

function buildCurrencyFilter() {
  const counts = {}, netTotals = {}, vatTotals = {};
  for (const inv of invoices) {
    const c = inv.currency || '(brak)';
    counts[c] = (counts[c] || 0) + 1;
    if (inv.netAmount != null) { netTotals[c] = (netTotals[c] || 0) + inv.netAmount; }
    // grossAmount − netAmount = Σ(P_14_*W) = VAT in the invoice's own currency (vatAmount from API is PLN only).
    // No sign guard: correction invoices have negative gross/net, so gross−net is negative and correctly reduces vatTotals.
    if (inv.grossAmount != null && inv.netAmount != null) {
      vatTotals[c] = (vatTotals[c] || 0) + (inv.grossAmount - inv.netAmount);
    }
  }
  const currencies = Object.keys(counts).sort();
  // Drop any previously-selected currencies that no longer exist in this invoice set
  // so getFilteredInvoices() doesn't silently return zero rows after a refresh.
  if (activeCurrencies.size > 0) {
    const validSet = new Set(currencies);
    for (const c of activeCurrencies) { if (!validSet.has(c)) { activeCurrencies.delete(c); } }
  }
  const hasChart = showIncomeChart && Object.keys(netTotals).length > 0;
  const hasChips = currencies.length > 1;

  if (!hasChips && !hasChart) { filterBar.classList.remove('visible'); return; }

  let html = '';

  // Currency chips row (only when multiple currencies)
  if (hasChips) {
    html += '<div class="filter-chips-row"><span class="filter-label">Waluta:</span>';
    for (const c of currencies) {
      const active = activeCurrencies.has(c) ? ' active' : '';
      const rate = c !== 'PLN' && fxRates[c] ? '<span class="chip-rate">' + fxRates[c].toFixed(4) + '</span>' : '';
      html += '<span class="chip' + active + '" onclick="toggleCurrency(\'' + c + '\')">' + c + '<span class="chip-count">(' + counts[c] + ')</span>' + rate + '</span>';
    }
    html += '</div>';
  }

  // Horizontal bar chart (sorted largest first by net)
  if (hasChart) {
    const subj = $('subjectType') ? $('subjectType').value : '';
    const chartTitles = { Subject1: 'Przychody netto + VAT', Subject2: 'Koszty netto + VAT', Subject3: 'Kwoty netto + VAT', SubjectAuthorized: 'Kwoty netto + VAT' };
    const chartTitle = chartTitles[subj] || 'Kwoty netto + VAT';
    const entries = Object.entries(netTotals).sort((a, b) => b[1] - a[1]);
    // Scale against the largest absolute gross so positive and negative bars share the same axis
    const maxVal = Math.max(...entries.map(([c, v]) => Math.abs(v + (vatTotals[c] || 0))), 1);
    // Assign consistent colors by alphabetical currency order so chips and bars share a color
    const colorMap = {};
    currencies.forEach((c, i) => { colorMap[c] = CHART_PALETTE[i % CHART_PALETTE.length]; });
    const MIN_PCT = 2; // minimum bar width so tiny values are always visible
    let barsHtml = '';
    entries.forEach(([cur, net]) => {
      const vat = vatTotals[cur] || 0;
      const gross = net + vat;
      const isNeg = net < 0;
      const absNet = Math.abs(net);
      const absVat = Math.abs(vat);
      const color = colorMap[cur] || CHART_PALETTE[0];
      // Negative net (corrections dominate): render muted bar, label shows sign
      const barColor = isNeg ? '#ef9a9a' : color; // red-200 for net-negative currencies
      // Clamp the total bar (net+vat together) to MIN_PCT, then split proportionally.
      // This prevents a fake net bar when absNet===0 (e.g. cancellation-only currencies).
      const totalPct = Math.max(MIN_PCT, ((absNet + absVat) / maxVal) * 100);
      const netPct = absNet === 0 ? 0 : (absNet / (absNet + absVat || 1)) * totalPct;
      const vatPct = totalPct - netPct;
      const tooltip = cur + ': netto ' + fmtAmt(net) + ', VAT ' + fmtAmt(vat) + ', brutto ' + fmtAmt(gross);
      const vatStyle = 'width:' + vatPct + '%;flex-shrink:0;background:' + VAT_COLOR;
      barsHtml += '<div class="hbar-row" title="' + tooltip + '">' +
        '<span class="hbar-cur" style="color:' + color + '">' + cur + '</span>' +
        '<div class="hbar-track">' +
          '<div class="hbar-fill" style="width:' + netPct + '%;flex-shrink:0;background:' + barColor + '"></div>' +
          (absVat > 0 ? '<div class="hbar-fill" style="' + vatStyle + '"></div>' : '') +
        '</div>' +
        '<span class="hbar-amt">' + fmtAmt(net) + ' + ' + fmtAmt(vat) + ' VAT' +
          (cur !== 'PLN' && fxRates[cur] ? ' <span class="hbar-pln">≈ netto: ' + fmtAmt(net * fxRates[cur]) + ' / brutto: ' + fmtAmt(gross * fxRates[cur]) + ' PLN</span>' : '') +
        '</span>' +
        '</div>';
    });

    // Summary row — totals in PLN for selected currencies (all when no filter active)
    const summaryEntries = entries.filter(([c]) => activeCurrencies.size === 0 || activeCurrencies.has(c));
    let totalNetPln = 0, totalGrossPln = 0, missingRate = false, hasNonPln = false, convertedAny = false;
    for (const [c, net] of summaryEntries) {
      const vat = vatTotals[c] || 0;
      const rate = c === 'PLN' ? 1 : (fxRates[c] || null);
      if (c !== 'PLN') { hasNonPln = true; }
      if (rate == null) { missingRate = true; continue; }
      totalNetPln += net * rate;
      totalGrossPln += (net + vat) * rate;
      convertedAny = true;
    }
    let summaryHtml = '';
    const allRatesMissing = hasNonPln && missingRate && !convertedAny;
    if (hasNonPln && fxRatesFetchInProgress) {
      summaryHtml = '<div class="hbar-summary hbar-summary-wait">⏳ Pobieranie kursów NBP…</div>';
    } else if (hasNonPln && fxRatesFetchFailed && Object.keys(fxRates).length === 0) {
      summaryHtml = '<div class="hbar-summary hbar-summary-warn">Nie udało się pobrać kursów NBP</div>';
    } else if (hasNonPln && Object.keys(fxRates).length === 0) {
      summaryHtml = '<div class="hbar-summary hbar-summary-wait">Oczekiwanie na kursy NBP…</div>';
    } else if (allRatesMissing) {
      summaryHtml = '<div class="hbar-summary hbar-summary-wait">Brak kursów NBP dla wybranych walut</div>';
    } else if (summaryEntries.length > 0) {
      const prefix = hasNonPln ? '~ ' : '';
      const missing = missingRate ? ' <span class="hbar-summary-warn">(brak kursu dla niektórych walut)</span>' : '';
      summaryHtml = '<div class="hbar-summary">' + prefix + 'łącznie netto: <b>' + fmtAmt(totalNetPln) + ' PLN</b>'
        + ' / brutto: <b>' + fmtAmt(totalGrossPln) + ' PLN</b>' + missing + '</div>';
    }
    html += '<div class="filter-chart"><div class="hbar-chart-title">' + chartTitle + '</div>' + barsHtml + summaryHtml + '</div>';
  }

  filterBar.innerHTML = html;
  filterBar.classList.add('visible');
}

function toggleCurrency(c) {
  if (activeCurrencies.has(c)) activeCurrencies.delete(c);
  else activeCurrencies.add(c);
  buildCurrencyFilter();
  currentPage = 0;
  renderTable();
  updateFilteredCount();
}

function updateFilteredCount() {
  const filtered = getFilteredInvoices();
  const suffix = activeCurrencies.size > 0 ? ' (filtr: ' + filtered.length + ' z ' + total + ')' : '';
  countLabel.textContent = total + ' faktur' + suffix;
}

function renderTable() {
  updateSummaryButtons();
  if (!invoices.length) { tableWrap.innerHTML = '<div class="empty">Brak faktur.</div>'; return; }
  const cols = [
    {key:'ksefNumber', label:'Numer KSeF'},
    {key:'invoiceNumber', label:'Numer faktury'},
    {key:'issueDate', label:'Data wystawienia'},
    {key:'sellerName', label:'Sprzedawca'},
    {key:'buyerName', label:'Nabywca'},
    {key:'netAmount', label:'Kwota netto', cls:'amount'},
    {key:'_vat', label:'VAT', cls:'amount'},
    {key:'grossAmount', label:'Kwota brutto', cls:'amount'},
    {key:'currency', label:'Waluta'},
  ];
  let sorted = [...getFilteredInvoices()];
  if (sortCol !== null) {
    sorted.sort((a,b) => {
      let va = sortCol === '_vat' ? (a.grossAmount != null && a.netAmount != null ? a.grossAmount - a.netAmount : null) : (a[sortCol] ?? '');
      let vb = sortCol === '_vat' ? (b.grossAmount != null && b.netAmount != null ? b.grossAmount - b.netAmount : null) : (b[sortCol] ?? '');
      if (va == null && vb == null) { return 0; }
      if (va == null) { return sortAsc ? 1 : -1; }
      if (vb == null) { return sortAsc ? -1 : 1; }
      if (typeof va === 'number' && typeof vb === 'number') return sortAsc ? va - vb : vb - va;
      va = String(va).toLowerCase(); vb = String(vb).toLowerCase();
      return sortAsc ? va.localeCompare(vb) : vb.localeCompare(va);
    });
  }
  const limit = parseInt($('displayLimit')?.value) || 50;
  const totalPages = displayAll ? 1 : Math.ceil(sorted.length / limit);
  if (currentPage >= totalPages) currentPage = Math.max(0, totalPages - 1);
  const visible = displayAll ? sorted : sorted.slice(currentPage * limit, (currentPage + 1) * limit);
  let html = '<table><thead><tr>';
  html += '<th style="width:2rem"><input type="checkbox" id="checkAll" onchange="toggleAll(this.checked)"></th>';
  html += '<th style="width:2rem"></th>';
  html += '<th style="width:3rem"></th>';
  html += '<th style="width:5rem">Pliki</th>';
  for (const c of cols) {
    const arrow = sortCol === c.key ? (sortAsc ? ' &#9650;' : ' &#9660;') : '';
    html += '<th data-col="' + c.key + '" onclick="sortBy(\'' + c.key + '\')">' + c.label + '<span class="sort-arrow">' + arrow + '</span></th>';
  }
  html += '</tr></thead><tbody>';
  for (let i = 0; i < visible.length; i++) {
    const inv = visible[i];
    const idx = inv._idx;
    const date = inv.issueDate ? inv.issueDate.substring(0,10) : '';
    const chk = selectedInvoices.has(idx) ? ' checked' : '';
    const fs = fileStatus[idx] || {};
    const hasFiles = fs.xml || fs.pdf || fs.json;
    let badges = '';
    if (fs.xml) badges += '<span class="badge badge-xml">XML</span>';
    if (fs.pdf) badges += '<span class="badge badge-pdf">PDF</span>';
    if (fs.json) badges += '<span class="badge badge-json">JSON</span>';
    html += '<tr id="row-' + idx + '"' + (hasFiles ? ' class="has-files"' : '') + '>';
    html += '<td><input type="checkbox" data-idx="' + idx + '" onchange="toggleSelect(' + idx + ', this.checked)"' + chk + '></td>';
    html += '<td class="dl-icon" id="icon-' + idx + '"></td>';
    html += '<td><button class="btn-preview" onclick="showPreview(' + idx + ')" title="Podglad faktury">&#128196;</button></td>';
    html += '<td>' + badges + '</td>';
    html += '<td class="col-ksef" title="' + (inv.ksefNumber||'') + '">' + (inv.ksefNumber||'') + '</td>';
    html += '<td>' + (inv.invoiceNumber||'') + '</td>';
    html += '<td>' + date + '</td>';
    html += '<td class="col-name" title="NIP: ' + (inv.sellerNip||'') + '">' + (inv.sellerName||'') + '</td>';
    html += '<td class="col-name">' + (inv.buyerName||'') + '</td>';
    const vat = (inv.grossAmount != null && inv.netAmount != null) ? inv.grossAmount - inv.netAmount : null;
    html += '<td class="amount">' + (inv.netAmount != null ? inv.netAmount.toFixed(2) : '') + '</td>';
    html += '<td class="amount">' + (vat != null ? vat.toFixed(2) : '') + '</td>';
    html += '<td class="amount">' + (inv.grossAmount != null ? inv.grossAmount.toFixed(2) : '') + '</td>';
    html += '<td>' + (inv.currency||'') + '</td>';
    html += '</tr>';
  }
  html += '</tbody></table>';
  if (!displayAll && sorted.length > 0) {
    const first = currentPage * limit + 1;
    const last = Math.min((currentPage + 1) * limit, sorted.length);
    const pageOpts = [5, 10, 50, 100].map(n =>
      '<option value="' + n + '"' + (n === limit ? ' selected' : '') + '>' + n + '</option>'
    ).join('');
    html += '<div style="display:flex;align-items:center;justify-content:center;gap:.5rem;padding:.5rem 0;flex-wrap:wrap;font-size:.82rem">';
    if (totalPages > 1) {
      html += '<button class="btn-sm btn-outline" onclick="currentPage=0;renderTable()" '
            +   (currentPage === 0 ? 'disabled' : '') + ' title="Pierwsza strona">&#171;</button>'
            + '<button class="btn-sm btn-outline" onclick="currentPage--;renderTable()" '
            +   (currentPage === 0 ? 'disabled' : '') + '>&#8249; Poprzednia</button>'
            + '<span style="color:#555">Strona ' + (currentPage + 1) + ' z ' + totalPages
            +   ' <span style="color:#999">(' + first + '–' + last + ' z ' + sorted.length + ')</span></span>'
            + '<button class="btn-sm btn-outline" onclick="currentPage++;renderTable()" '
            +   (currentPage >= totalPages - 1 ? 'disabled' : '') + '>Następna &#8250;</button>'
            + '<button class="btn-sm btn-outline" onclick="currentPage=' + (totalPages - 1) + ';renderTable()" '
            +   (currentPage >= totalPages - 1 ? 'disabled' : '') + ' title="Ostatnia strona">&#187;</button>'
            + '<span style="color:#bbb">|</span>';
    }
    html += '<label style="display:flex;align-items:center;gap:.3rem;color:#555">Pokaż'
          + '<select style="font-size:.82rem;padding:.15rem .3rem;border:1px solid #ccc;border-radius:4px" onchange="setDisplayLimit(this.value)">'
          + pageOpts + '</select>na stronie</label>';
    if (totalPages > 1) {
      html += '<button class="btn-primary" style="font-size:.78rem;padding:.3rem .7rem" '
            +   'onclick="displayAll=true;renderTable()">Pokaż wszystkie (' + sorted.length + ')</button>';
    }
    html += '</div>';
  }
  tableWrap.innerHTML = html;
  updateSelectionUI();
}

function sortBy(col) {
  if (sortCol === col) sortAsc = !sortAsc;
  else { sortCol = col; sortAsc = true; }
  currentPage = 0;
  renderTable();
}

function setDisplayLimit(val) {
  const sel = $('displayLimit');
  if (sel) sel.value = val;
  currentPage = 0;
  savePrefs().catch(() => {});
  renderTable();
}

function toggleSelect(idx, checked) {
  if (checked) selectedInvoices.add(idx);
  else selectedInvoices.delete(idx);
  updateSelectionUI();
}

function toggleAll(checked) {
  const filtered = getFilteredInvoices();
  for (const inv of filtered) {
    if (checked) selectedInvoices.add(inv._idx);
    else selectedInvoices.delete(inv._idx);
  }
  document.querySelectorAll('td input[type=checkbox]').forEach(cb => cb.checked = checked);
  updateSelectionUI();
}

function selectAll() {
  const filtered = getFilteredInvoices();
  for (const inv of filtered) selectedInvoices.add(inv._idx);
  renderTable();
}

function clearSelection() {
  selectedInvoices.clear();
  renderTable();
}

function selectMissing() {
  const filtered = getFilteredInvoices();
  for (const inv of filtered) {
    const fs = fileStatus[inv._idx] || {};
    if (!fs.xml && !fs.pdf && !fs.json) selectedInvoices.add(inv._idx);
  }
  renderTable();
}

function updateSelectionUI() {
  const n = selectedInvoices.size;
  selToolbar.classList.toggle('visible', invoices.length > 0);
  selCount.textContent = n > 0 ? 'Zaznaczono: ' + n : '';
  btnDownloadSel.disabled = n === 0;
  btnDownloadSel.textContent = n > 0 ? 'Zapisz zaznaczone (' + n + ')' : 'Zapisz zaznaczone';
  btnBrowserDownload.disabled = n === 0;
  if (n <= 1) { btnBrowserDownload.textContent = 'Pobierz PDF'; }
  else { btnBrowserDownload.textContent = 'Pobierz ZIP (' + n + ')'; }
}

function connectSSE() {
  if (es) es.close();
  es = new EventSource('/events');
  es.onmessage = (e) => {
    const msg = JSON.parse(e.data);
    const d = msg.data || {};
    switch (msg.type) {
      case 'invoice_start': {
        const row = document.getElementById('row-' + d.current);
        const icon = document.getElementById('icon-' + d.current);
        if (row) row.className = 'downloading';
        if (icon) icon.innerHTML = '<span class="spinner">&#8635;</span>';
        row?.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
        break;
      }
      case 'invoice_done': {
        if (!d.pdf) { markDone(d.current); }
        break;
      }
      case 'pdf_done': { markDone(d.current); break; }
      case 'invoice_complete': { markDone(d.current); break; }
      case 'all_done':
        setStatus('Gotowe! Pobrano ' + (d.count || dlTotal) + ' faktur.', 'done');
        bar.style.width = '100%'; bar.textContent = '100%';
        clearSelection();
        btnSearch.disabled = false; btnDownload.disabled = false;
        checkExisting();
        clearTimeout(progressHideTimeoutId); progressHideTimeoutId = setTimeout(hideProgress, 1200);
        break;
      case 'error': {
        const row = document.getElementById('row-' + d.current);
        const icon = document.getElementById('icon-' + d.current);
        if (row) row.className = 'error';
        if (icon) icon.innerHTML = '&#10007;';
        if (d.fatal) {
          setStatus('Blad: ' + d.message, 'error');
          btnSearch.disabled = false; btnDownload.disabled = false;
          btnDownloadSel.disabled = selectedInvoices.size === 0; btnBrowserDownload.disabled = selectedInvoices.size === 0;
        }
        break;
      }
      case 'background_refresh':
        // d = { profileName, count, newCount, truncated }
        if (d.profileName === currentSessionProfile) {
          // Active profile: C# bg-refresh already updated _cachedInvoices in-memory.
          // Reload the table so the screen matches the logs without a manual search.
          // Must await before setting truncation status — loadCachedInvoices calls setStatus on completion.
          loadCachedInvoices().then(() => {
            if (d.truncated) {
              setStatus('\u26A0\uFE0F Wyniki obci\u0119te \u2014 KSeF zwraca maks. 10\u00A0000 faktur. Zaw\u0119\u017C zakres dat.', 'error');
            }
          });
        }
        if (d.newCount > 0) {
          markProfileBadge(d.profileName, d.newCount);
          notifyNewInvoices(d.profileName, d.newCount);
        }
        fetchNotifStatus();
        break;
      case 'notification_outcome':
        // d = { profileName, isRetry, channelsOk, channelsFailed, pendingRetries? }
        if (d.channelsFailed && d.channelsFailed.length > 0) {
          console.warn('[notif] Delivery failed for profile "' + d.profileName + '",' +
            (d.isRetry ? ' retry' : ' initial') +
            ' channels: [' + d.channelsFailed.join(', ') + ']');
        }
        fetchNotifStatus();
        break;
    }
  };
}

let dlTotal = 0;
let progressHideTimeoutId = null;
function hideProgress() { progressHideTimeoutId = null; progressWrap.classList.remove('visible'); }
function markDone(idx) {
  const row = document.getElementById('row-' + idx);
  const icon = document.getElementById('icon-' + idx);
  if (row) row.className = 'done';
  if (icon) icon.innerHTML = '&#10003;';
  completed++;
  const pct = dlTotal > 0 ? Math.round((completed / dlTotal) * 100) : 0;
  bar.style.width = pct + '%'; bar.textContent = pct + '%';
  setStatus('Pobieranie... ' + completed + ' / ' + dlTotal, 'info');
}

function markProfileBadge(name, count) {
  profileBadges[name] = (profileBadges[name] || 0) + count;
  updateProfileSelectBadges();
}

const notifStatusBadges = {}; // profileName → { pendingRetries, lastSentAt, hasErrors }

function updateProfileSelectBadges() {
  const sel = $('profileSelect');
  if (!sel) return;
  for (const opt of sel.options) {
    const invBadge = profileBadges[opt.value];
    const notif = notifStatusBadges[opt.value];
    const base = opt.dataset.base || opt.textContent;
    opt.dataset.base = base;
    let label = base;
    if (invBadge) { label += ' \uD83D\uDD14' + invBadge; }
    if (notif && notif.pendingRetries > 0) { label += ' \u26A0\uFE0F' + notif.pendingRetries; }
    opt.textContent = label;
  }
}

async function fetchNotifStatus() {
  try {
    const res = await fetch('/notification-status');
    if (!res.ok) { return; }
    const data = await res.json();
    for (const [name, status] of Object.entries(data)) {
      notifStatusBadges[name] = status;
    }
    updateProfileSelectBadges();
  } catch (e) { /* ignore — server may not be ready yet */ }
}

// Extensible notification hook — swap for email/Slack in future
function notifyNewInvoices(profileName, count) {
  if (typeof Notification === 'undefined') return;
  if (Notification.permission !== 'granted') return;
  try {
    new Notification('KSeF: nowe faktury (' + profileName + ')', {
      body: count + ' nowych faktur dla profilu ' + profileName
    });
  } catch (e) { /* notification not supported in this context */ }
}

async function doAuth() {
  const btn = $('btnAuth');
  btn.disabled = true;
  setStatus('Autoryzacja...', 'info');
  try {
    const res = await fetch('/auth', { method:'POST' });
    if (!res.ok) { const e = await res.json(); throw new Error(e.error || 'Auth failed'); }
    const data = await res.json();
    setStatus('Autoryzacja OK: ' + (data.message || 'Token odswiezony.'), 'done');
    await fetchTokenStatus();
  } catch (err) {
    setStatus('Blad autoryzacji: ' + err.message, 'error');
  } finally {
    btn.disabled = false;
  }
}

function monthToFrom(val) {
  if (!val) return null;
  return val + '-01';
}
function monthToTo(val) {
  if (!val) return null;
  const [y, m] = val.split('-').map(Number);
  const last = new Date(y, m, 0).getDate();
  return val + '-' + String(last).padStart(2, '0');
}

function setBusyState(busy) {
  btnSearch.disabled = busy;
  btnDownload.disabled = busy;
  btnDownloadSel.disabled = busy || selectedInvoices.size === 0;
  btnBrowserDownload.disabled = busy || selectedInvoices.size === 0;
}

let summaryInFlight = false;
function updateSummaryButtons() {
  const hasInvoices = total > 0;
  btnSummary.disabled = !hasInvoices || summaryInFlight;
  btnSummaryBrowser.disabled = !hasInvoices || summaryInFlight;
}

async function doSearch() {
  searchRunning = true;
  setBusyState(true);
  downloadBar.style.display = 'none';
  selToolbar.classList.remove('visible');
  tableWrap.innerHTML = '';
  progressWrap.classList.remove('visible');
  setStatus('Wyszukiwanie faktur...', 'info');
  invoices = []; total = 0; completed = 0; sortCol = null;
  activeCurrencies = new Set();
  selectedInvoices = new Set();
  fileStatus = [];
  filterBar.classList.remove('visible');
  const myGen = profileSwitchGen;

  try {
    const fromVal = $('fromDate').value;
    const toVal = $('toDate').value;
    const params = {
      subjectType: $('subjectType').value,
      from: monthToFrom(fromVal) || 'thismonth',
      to: monthToTo(toVal) || null,
      dateType: $('dateType').value
    };
    const res = await fetch('/search', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify(params) });
    if (!res.ok) { const e = await res.json(); throw new Error(e.error || 'Search failed'); }
    if (profileSwitchGen !== myGen) { searchRunning = false; return; } // profile switched — discard
    const searchResult = await res.json();
    invoices = searchResult.invoices;
    for (let i = 0; i < invoices.length; i++) invoices[i]._idx = i;
    total = invoices.length;
    countLabel.textContent = total + ' faktur';
    if (searchResult.truncated) {
      setStatus('\u26A0\uFE0F Wyniki obci\u0119te \u2014 KSeF zwraca maks. 10\u00A0000 faktur. Zaw\u0119\u017A zakres dat.', 'error');
    } else {
      setStatus('Znaleziono ' + total + ' faktur.', total > 0 ? 'info' : 'idle');
    }
    refreshExchangeRates(); // sets fxRatesFetchInProgress before buildCurrencyFilter renders
    buildCurrencyFilter();
    displayAll = false;
    currentPage = 0;
    renderTable();
    downloadBar.style.display = total > 0 ? 'flex' : 'none';
    searchRunning = false;
    searchRunning = false; btnSearch.disabled = false; btnDownload.disabled = total === 0; btnDownloadSel.disabled = true; btnBrowserDownload.disabled = total === 0;
    checkExisting();
    // Capture params for cyclic refresh; re-baseline known set on every manual search
    lastSearchParams = { subjectType: $('subjectType').value, fromDate: $('fromDate').value, toDate: $('toDate').value, dateType: $('dateType').value };
    knownInvoiceKsefNumbers = new Set(invoices.map(i => i.ksefNumber));
  } catch (err) {
    searchRunning = false;
    setStatus('Błąd wyszukiwania: ' + err.message, 'error');
    updateAuthButton();
  }
}

// ── Auto-refresh ─────────────────────────────────────────────────────────────

function startAutoRefresh(minutes) {
  if (autoRefreshTimer) { clearInterval(autoRefreshTimer); autoRefreshTimer = null; }
  // Values 1–9 are below the server-enforced minimum of 10 minutes; treat them as disabled.
  if (!minutes || minutes < 10) return;
  autoRefreshTimer = setInterval(() => { if (lastSearchParams) silentRefresh(); }, minutes * 60 * 1000);
}

async function silentRefresh() {
  if (!lastSearchParams) return;
  if (refreshRunning) { console.info('[auto-refresh] Skipping — previous cycle still running'); return; }
  refreshRunning = true;
  const myGen = profileSwitchGen;
  try {
    // Refresh session token if it expires within 1 minute
    if (tokenExpiry && (tokenExpiry - Date.now()) < 60 * 1000) {
      console.info('[auto-refresh] Access token expiring in <1 min — refreshing session before search');
      try {
        const authRes = await fetch('/auth', { method: 'POST' });
        if (!authRes.ok) {
          console.warn('[auto-refresh] Token refresh failed — skipping this cycle');
          return;
        }
        await fetchTokenStatus();
        console.info('[auto-refresh] Session refreshed. New expiry:', tokenExpiry?.toLocaleTimeString());
      } catch(e) {
        console.warn('[auto-refresh] Token refresh error:', e);
        return;
      }
    }
    console.info('[auto-refresh] Cyclic search started at', new Date().toLocaleTimeString());
    const params = {
      subjectType: lastSearchParams.subjectType,
      from: 'thismonth', // always current month — matches C# bg-refresh behaviour
      to: null,          // always up to now — never a stale past date
      dateType: lastSearchParams.dateType,
      source: 'auto'
    };
    const res = await fetch('/search', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify(params) });
    if (!res.ok) { try { const e = await res.json(); setStatus('Auto-odświeżanie: błąd — ' + (e.error || 'HTTP ' + res.status), 'error'); } catch(_) { setStatus('Auto-odświeżanie: błąd HTTP ' + res.status, 'error'); } await fetchTokenStatus(); return; }
    const searchResult = await res.json();
    if (profileSwitchGen !== myGen) return; // profile switched while request was in-flight — discard
    const fresh = searchResult.invoices;
    fresh.forEach((inv, i) => inv._idx = i);
    console.info('[auto-refresh] Cyclic search complete —', fresh.length, 'invoices', searchResult.truncated ? '(TRUNCATED)' : '');
    detectNewInvoices(fresh);
    invoices = fresh;
    total = invoices.length;
    countLabel.textContent = total + ' faktur';
    if (searchResult.truncated) {
      setStatus('\u26A0\uFE0F Wyniki obci\u0119te \u2014 KSeF zwraca maks. 10\u00A0000 faktur. Zaw\u0119\u017C zakres dat.', 'error');
    } else {
      setStatus('Auto-od\u015Bwie\u017Canie: ' + total + ' faktur.', 'idle');
    }
    refreshExchangeRates(); // sets fxRatesFetchInProgress before buildCurrencyFilter renders
    buildCurrencyFilter();
    renderTable();
    checkExisting();
    await fetchTokenStatus();
  } catch(e) {
    setStatus('Auto-odświeżanie: błąd sieci — ' + e.message, 'error');
    await fetchTokenStatus();
  } finally { refreshRunning = false; }
}

function detectNewInvoices(fresh) {
  if (knownInvoiceKsefNumbers === null) {
    knownInvoiceKsefNumbers = new Set(fresh.map(i => i.ksefNumber));
    return;
  }
  const newOnes = fresh.filter(i => !knownInvoiceKsefNumbers.has(i.ksefNumber));
  if (newOnes.length === 0) return;
  fresh.forEach(i => knownInvoiceKsefNumbers.add(i.ksefNumber));
  const n = newOnes.length;
  const msg = 'Znaleziono ' + n + ' now' + (n === 1 ? 'ą fakturę' : (n < 5 ? 'e faktury' : 'ych faktur')) + '!';
  // 1. Page title badge — cleared on next focus
  document.title = '(' + n + ' nowych) KSeFCli';
  window.addEventListener('focus', () => { document.title = 'KSeFCli'; }, { once: true });
  // 2. In-page toast
  showNewInvoiceToast(msg);
  // 3. OS notification
  if (typeof Notification !== 'undefined' && Notification.permission === 'granted') {
    new Notification('KSeFCli', { body: msg });
  }
}

function showNewInvoiceToast(msg) {
  let toast = document.getElementById('newInvoiceToast');
  if (!toast) {
    toast = document.createElement('div');
    toast.id = 'newInvoiceToast';
    toast.style.cssText = 'position:fixed;top:1rem;left:50%;transform:translateX(-50%);background:#1976d2;color:#fff;padding:.6rem 1.4rem;border-radius:.5rem;font-weight:600;font-size:.95rem;box-shadow:0 2px 12px rgba(0,0,0,.25);z-index:9999;cursor:pointer;transition:opacity .4s';
    toast.onclick = () => { toast.style.opacity = 0; setTimeout(() => toast.remove(), 400); };
    document.body.appendChild(toast);
  }
  toast.textContent = msg;
  toast.style.opacity = 1;
  clearTimeout(toast._timer);
  toast._timer = setTimeout(() => { toast.style.opacity = 0; setTimeout(() => toast.remove(), 400); }, 8000);
}

function requestNotificationPermission() {
  if (typeof Notification !== 'undefined' && Notification.permission === 'default') {
    Notification.requestPermission();
  }
}

async function sendSampleNotification() {
  requestNotificationPermission();
  const msg = 'Znaleziono 3 nowe faktury!';
  // 1. Page title badge
  document.title = '(3 nowych) KSeFCli';
  window.addEventListener('focus', () => { document.title = 'KSeFCli'; }, { once: true });
  // 2. In-page toast
  showNewInvoiceToast(msg);
  // 3. OS notification
  if (typeof Notification !== 'undefined' && Notification.permission === 'granted') {
    new Notification('KSeFCli', { body: msg });
  }
  // 4. Webhook channels (Slack / Teams) configured for the current profile
  try {
    const profileName = (typeof currentSessionProfile !== 'undefined' ? currentSessionProfile : '') || '';
    const res = await fetch('/test-notification', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ profileName }),
    });
    if (res.ok) {
      const data = await res.json();
      if (data.message) showNewInvoiceToast('Webhook: ' + data.message);
    }
  } catch (e) { /* ignore webhook test errors */ }
}

async function sendTestEmail() {
  const toEmail = $('smtpTestTo')?.value?.trim();
  const resultEl = $('smtpTestResult');
  const btn = $('btnTestEmail');
  if (!toEmail) {
    if (resultEl) { resultEl.textContent = 'Wpisz adres e-mail odbiorcy.'; resultEl.style.color = '#c62828'; resultEl.style.display = ''; }
    return;
  }
  if (btn) btn.disabled = true;
  if (resultEl) { resultEl.textContent = 'Wysyłanie...'; resultEl.style.color = '#666'; resultEl.style.display = ''; }
  try {
    // Save SMTP settings first so the server uses the current form values
    await savePrefs();
    const res = await fetch('/test-email', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ toEmail }),
    });
    const data = await res.json();
    if (data.error) throw new Error(data.error);
    if (resultEl) { resultEl.textContent = data.message || 'Wysłano.'; resultEl.style.color = '#2e7d32'; resultEl.style.display = ''; }
  } catch (err) {
    if (resultEl) { resultEl.textContent = 'Błąd: ' + (err?.message || 'nieznany błąd'); resultEl.style.color = '#c62828'; resultEl.style.display = ''; }
  } finally {
    if (btn) btn.disabled = false;
  }
}

async function doDownload(selOnly) {
  const indices = selOnly ? [...selectedInvoices] : null;
  const dlCount = selOnly ? selectedInvoices.size : total;
  if (selOnly && dlCount === 0) { setStatus('Nie zaznaczono faktur.', 'error'); return; }

  setBusyState(true);
  completed = 0; dlTotal = dlCount;
  progressWrap.classList.add('visible');
  bar.style.width = '0%'; bar.textContent = '0%';
  setStatus('Pobieranie... 0 / ' + dlCount, 'info');
  for (let i = 0; i < total; i++) {
    const row = document.getElementById('row-' + i);
    const icon = document.getElementById('icon-' + i);
    if (row) row.className = '';
    if (icon) icon.innerHTML = '';
  }
  connectSSE();
  try {
    const body = {
      outputDir: $('outputDir').value || '.',
      selectedIndices: indices,
      customFilenames: $('customFilenames').checked,
      exportXml: $('expXml').checked,
      exportJson: $('expJson').checked,
      exportPdf: $('expPdf').checked,
      separateByNip: $('separateByNip').checked,
      pdfColorScheme: $('pdfColorScheme').value,
      subjectType: $('subjectType') ? $('subjectType').value : null
    };
    const res = await fetch('/download', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify(body) });
    if (!res.ok) { const e = await res.json(); throw new Error(e.error || 'Download failed'); }
  } catch (err) {
    setStatus('Blad: ' + err.message, 'error');
    setStatus('Blad: ' + err.message, 'error');
    setBusyState(false);
  }
}

async function doBrowserDownload() {
  const indices = selectedInvoices.size > 0 ? [...selectedInvoices] : null;
  const count = indices ? indices.length : total;
  const month = $('fromDate').value || new Date().toISOString().slice(0, 7);
  const body = {
    indices,
    pdfColorScheme: $('pdfColorScheme').value,
    month,
    customFilenames: $('customFilenames').checked
  };
  setBusyState(true);
  completed = 0; dlTotal = count;
  progressWrap.classList.add('visible');
  bar.style.width = '0%'; bar.textContent = '0%';
  setStatus('Generowanie PDF... 0 / ' + count, 'info');
  connectSSE();
  try {
    const res = await fetch('/download-browser', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
    if (!res.ok) { let msg = 'HTTP ' + res.status; try { const e = await res.json(); msg = e.error || msg; } catch { msg = await res.text().then(t => t.slice(0,120)).catch(() => msg); } throw new Error(msg); }
    const disposition = res.headers.get('Content-Disposition') || '';
    const nameMatch = disposition.match(/filename="([^"]+)"/);
    const fileName = nameMatch ? nameMatch[1] : 'faktury.pdf';
    const blob = await res.blob();
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url; a.download = fileName;
    document.body.appendChild(a);
    a.click();
    setTimeout(() => { URL.revokeObjectURL(url); a.remove(); }, 0);
    clearSelection();
    bar.style.width = '100%'; bar.textContent = '100%';
    setStatus('Pobieranie gotowe.', 'done');
    clearTimeout(progressHideTimeoutId); progressHideTimeoutId = setTimeout(hideProgress, 1200);
  } catch (err) {
    setStatus('Błąd pobierania: ' + err.message, 'error');
    progressWrap.classList.remove('visible');
  } finally {
    setBusyState(false);
  }
}

async function doSummary() {
  const month = $('fromDate').value;
  if (!month) { setStatus('Wybierz miesiąc w polu "Od".', 'error'); return; }
  summaryInFlight = true; updateSummaryButtons();
  setStatus('Generowanie podsumowania...', 'info');
  try {
    const body = { outputDir: $('outputDir').value || '.', month, separateByNip: $('separateByNip').checked };
    const res = await fetch('/download-summary', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify(body) });
    if (!res.ok) { const e = await res.json(); throw new Error(e.error || 'Błąd serwera'); }
    const data = await res.json();
    setStatus('Podsumowanie zapisane: ' + data.filePath, 'done');
  } catch (err) {
    setStatus('Błąd: ' + err.message, 'error');
  } finally {
    summaryInFlight = false; updateSummaryButtons();
  }
}

async function doBrowserSummary() {
  const month = $('fromDate').value;
  if (!month) { setStatus('Wybierz miesiąc w polu "Od".', 'error'); return; }
  summaryInFlight = true; updateSummaryButtons();
  setStatus('Generowanie CSV...', 'info');
  try {
    const body = { outputDir: '.', month, separateByNip: false };
    const res = await fetch('/download-summary-browser', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify(body) });
    if (!res.ok) { let msg = 'HTTP ' + res.status; try { const e = await res.json(); msg = e.error || msg; } catch {} throw new Error(msg); }
    const disposition = res.headers.get('Content-Disposition') || '';
    const nameMatch = disposition.match(/filename="([^"]+)"/);
    const fileName = nameMatch ? nameMatch[1] : 'summary.csv';
    const blob = await res.blob();
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url; a.download = fileName;
    document.body.appendChild(a); a.click();
    setTimeout(() => { URL.revokeObjectURL(url); a.remove(); }, 0);
    setStatus('Pobieranie CSV gotowe.', 'done');
  } catch (err) {
    setStatus('Błąd: ' + err.message, 'error');
  } finally {
    updateSummaryButtons();
  }
}

// --- Folder picker ---
let browseCurrent = '.';
const folderModal = $('folderModal'), dirList = $('dirList'), browseCurrentPath = $('browseCurrentPath');

async function openBrowser() {
  const startPath = $('outputDir').value || '.';
  await browseTo(startPath);
  folderModal.classList.add('visible');
}

function closeBrowser() {
  folderModal.classList.remove('visible');
}

folderModal.addEventListener('click', (e) => { if (e.target === folderModal) closeBrowser(); });

async function browseTo(path) {
  try {
    const res = await fetch('/browse?path=' + encodeURIComponent(path));
    if (!res.ok) { const e = await res.json(); throw new Error(e.error || 'Browse failed'); }
    const data = await res.json();
    browseCurrent = data.current;
    browseCurrentPath.textContent = data.current;
    let html = '';
    if (data.parent) {
      html += '<div class="dir-item parent" onclick="browseTo(\'' + escAttr(data.parent) + '\')"><span class="dir-icon">&#128194;</span> ..</div>';
    }
    for (const d of data.dirs) {
      const fullPath = data.current.replace(/\/$/, '') + '/' + d;
      html += '<div class="dir-item" onclick="browseTo(\'' + escAttr(fullPath) + '\')"><span class="dir-icon">&#128193;</span> ' + escHtml(d) + '</div>';
    }
    if (!data.parent && data.dirs.length === 0) {
      html = '<div style="padding:1rem;color:#999;text-align:center">Brak podfolderow.</div>';
    }
    dirList.innerHTML = html;
  } catch (err) {
    dirList.innerHTML = '<div style="padding:1rem;color:#c62828">Blad: ' + escHtml(err.message) + '</div>';
  }
}

function selectCurrentDir() {
  $('outputDir').value = browseCurrent;
  closeBrowser();
  // No savePrefs() here — the folder browser is used from within the prefs modal,
  // so persistence is left to the explicit "Zapisz preferencje" button.
}

async function createDir() {
  const name = $('newDirName').value.trim();
  if (!name) return;
  const newPath = browseCurrent.replace(/\/$/, '') + '/' + name;
  try {
    const res = await fetch('/mkdir', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({ path: newPath }) });
    if (!res.ok) { const e = await res.json(); throw new Error(e.error || 'Mkdir failed'); }
    $('newDirName').value = '';
    await browseTo(newPath);
  } catch (err) {
    alert('Blad tworzenia folderu: ' + err.message);
  }
}

function escHtml(s) { const d = document.createElement('div'); d.textContent = s; return d.innerHTML; }

async function checkWhitelist(btn, nip, account) {
  const resultEl = document.getElementById(btn.id + '-result');
  btn.disabled = true;
  if (resultEl) { resultEl.textContent = '⏳ Sprawdzam...'; resultEl.className = 'wl-result'; }
  try {
    const res = await fetch('/whitelist-check?nip=' + encodeURIComponent(nip) + '&account=' + encodeURIComponent(account));
    const data = await res.json();
    // API MF returns { result: { accountAssigned: "TAK"|"NIE", requestDateTime, requestId } }
    // HandleAction errors return { error: "..." }
    const result = data.result || data;
    const assigned = result.accountAssigned;
    const dt = (result.requestDateTime || new Date().toISOString().slice(0,10)).slice(0,10);
    const key = result.requestId ? ' [' + result.requestId + ']' : '';
    if (assigned === 'TAK') {
      if (resultEl) { resultEl.textContent = '✓ na Białej Liście (' + dt + ')' + key; resultEl.className = 'wl-result ok'; }
    } else if (assigned === 'NIE') {
      if (resultEl) { resultEl.textContent = '✗ NIE na Białej Liście (' + dt + ')' + key; resultEl.className = 'wl-result fail'; }
    } else {
      const msg = data.error || data.message || data.code || result.message || ('HTTP ' + res.status);
      if (resultEl) { resultEl.textContent = '⚠ ' + msg; resultEl.className = 'wl-result err'; }
    }
  } catch (e) {
    if (resultEl) { resultEl.textContent = '⚠ Błąd połączenia'; resultEl.className = 'wl-result err'; }
  } finally {
    btn.disabled = false;
  }
}

function copyToClipboard(btn, text) {
  const showOk = () => {
    const orig = btn.innerHTML;
    btn.innerHTML = '&#10003; Skopiowano';
    btn.classList.add('copied');
    setTimeout(() => { btn.innerHTML = orig; btn.classList.remove('copied'); }, 1800);
  };
  const fallback = () => {
    const ta = document.createElement('textarea');
    ta.value = text; ta.style.position = 'fixed'; ta.style.opacity = '0';
    document.body.appendChild(ta); ta.select();
    try { document.execCommand('copy'); showOk(); } catch (_) {}
    ta.remove();
  };
  if (navigator.clipboard && navigator.clipboard.writeText) {
    navigator.clipboard.writeText(text).then(showOk).catch(fallback);
  } else {
    fallback();
  }
}
function escAttr(s) { return s.replace(/\\/g, '\\\\').replace(/'/g, "\\'"); }

// --- Invoice Details ---
let detailOverlay = null;
const detailCache = {};

function closeDetails() {
  if (detailOverlay) { detailOverlay.remove(); detailOverlay = null; }
}

document.addEventListener('keydown', (e) => {
  if (e.key === 'Escape') {
    closeDetails();
    $('aboutModal').classList.remove('visible');
    if ($('prefsModal').classList.contains('visible')) cancelPrefs();
    if ($('configModal').classList.contains('visible')) closeConfigEditor();
  }
});

// --- Invoice Preview ---
async function showPreview(idx) {
  closeDetails();
  const inv = invoices.find(i => i._idx === idx) || {};

  const overlay = document.createElement('div');
  overlay.className = 'detail-overlay';
  overlay.addEventListener('click', (e) => { if (e.target === overlay) closeDetails(); });

  const pop = document.createElement('div');
  pop.className = 'detail-popover preview-popover' + ($('previewDarkMode').checked ? ' preview-dark' : '');
  pop.style.width = '800px';
  pop.innerHTML = '<div class="dp-header"><h3>Podglad faktury</h3><button class="dp-close" onclick="closeDetails()">&times;</button></div><div class="dp-body"><div class="dp-loading"><span class="spinner">&#8635;</span> Pobieranie...</div></div>';
  overlay.appendChild(pop);
  document.body.appendChild(overlay);
  detailOverlay = overlay;

  const cacheKey = inv.ksefNumber || ('idx-' + idx);
  try {
    let data = detailCache[cacheKey];
    if (!data) {
      const res = await fetch('/invoice-details?idx=' + idx);
      if (!res.ok) { const e = await res.json(); throw new Error(e.error || 'Failed'); }
      data = await res.json();
      detailCache[cacheKey] = data;
    }
    if (detailOverlay !== overlay) return;
    renderPreview(pop, data, inv);
  } catch (err) {
    if (detailOverlay === overlay) {
      pop.querySelector('.dp-body').innerHTML = '<div style="color:#c62828;padding:1rem">Blad: ' + escHtml(err.message) + '</div>';
    }
  }
}

function renderPreview(pop, d, inv) {
  const invNr = inv.invoiceNumber || '';
  const copyBtn = (text, label) =>
    // Store text in data-copy attribute to avoid quote conflicts inside onclick=""
    '<button class="btn-copy" data-copy="' + text.replace(/&/g,'&amp;').replace(/"/g,'&quot;') + '" onclick="copyToClipboard(this,this.dataset.copy)" title="' + escHtml(label) + '">&#128203; Kopiuj</button>';

  let html = '<div class="preview-page">';
  html += '<div class="preview-title-row"><span class="preview-title">Faktura ' + escHtml(invNr) + '</span>'
        + (invNr ? copyBtn(invNr, 'Kopiuj numer faktury') : '') + '</div>';
  html += '<div class="preview-subtitle">KSeF: ' + escHtml(inv.ksefNumber || '') + ' | Data: ' + (inv.issueDate ? inv.issueDate.substring(0,10) : '');
  if (d.invoiceType) html += ' | Typ: ' + escHtml(d.invoiceType);
  if (d.currency) html += ' | Waluta: ' + escHtml(d.currency);
  html += '</div>';

  if (inv.ksefVerificationUrl) {
    const qrSrc = '/qr?url=' + encodeURIComponent(inv.ksefVerificationUrl);
    html += '<div class="preview-qr">';
    html += '<img src="' + qrSrc + '" alt="QR KSeF" title="Skanuj aby zweryfikowac fakture w KSeF">';
    html += '<br><a href="' + escHtml(inv.ksefVerificationUrl) + '" target="_blank" rel="noopener">Weryfikuj w KSeF &#8599;</a>';
    html += '</div>';
  }

  html += '<div class="preview-parties">';
  html += '<div class="preview-party"><h5>Sprzedawca</h5>';
  const sName = inv.sellerName || '';
  html += '<div class="name">' + escHtml(sName) + (sName ? ' ' + copyBtn(sName, 'Kopiuj nazwę sprzedawcy') : '') + '</div>';
  if (inv.sellerNip) html += '<div class="nip">NIP: ' + escHtml(inv.sellerNip) + '</div>';
  if (d.sellerAddress) html += '<div class="addr">' + escHtml(d.sellerAddress) + '</div>';
  html += '</div>';
  html += '<div class="preview-party"><h5>Nabywca</h5>';
  const bName = inv.buyerName || '';
  html += '<div class="name">' + escHtml(bName) + '</div>';
  if (d.buyerAddress) html += '<div class="addr">' + escHtml(d.buyerAddress) + '</div>';
  html += '</div>';
  html += '</div>';

  if (d.lineItems && d.lineItems.length > 0) {
    const hasRate = d.lineItems.some(l => l.exchangeRate);
    html += '<table class="preview-items"><thead><tr><th>#</th><th>Nazwa</th><th>J.m.</th><th>Ilosc</th><th>Cena j.</th><th class="amount">Netto</th><th class="amount">Brutto</th><th>VAT%</th>';
    if (hasRate) html += '<th>Kurs</th>';
    html += '</tr></thead><tbody>';
    for (const l of d.lineItems) {
      html += '<tr><td>' + (l.nr||'') + '</td><td>' + escHtml(l.name||'') + '</td><td>' + (l.unit||'') + '</td><td>' + (l.qty||'') + '</td><td class="amount">' + (l.unitPrice||'') + '</td><td class="amount">' + (l.netAmount||'') + '</td><td class="amount">' + (l.grossAmount||'') + '</td><td>' + (l.vatRate||'') + '</td>';
      if (hasRate) html += '<td>' + (l.exchangeRate||'') + '</td>';
      html += '</tr>';
    }
    html += '</tbody></table>';
  }

  html += '<div class="preview-totals"><table>';
  if (d.netTotal) html += '<tr><td class="label">Netto</td><td class="amount">' + d.netTotal + ' ' + (d.currency||'') + '</td></tr>';
  if (d.vatTotal) html += '<tr><td class="label">VAT</td><td class="amount">' + d.vatTotal + ' ' + (d.currency||'') + (d.vatTotalCurrency ? ' (wal.: ' + d.vatTotalCurrency + ')' : '') + '</td></tr>';
  if (d.grossTotal) html += '<tr><td class="label total">Brutto</td><td class="amount total">' + d.grossTotal + ' ' + (d.currency||'') + ' ' + copyBtn(d.grossTotal, 'Kopiuj kwotę brutto') + '</td></tr>';
  html += '</table></div>';

  // Payment / bank section
  if (d.paymentMethod || d.dueDate || d.bankAccount) {
    html += '<div class="preview-payment"><h5>Płatność</h5>';
    if (d.paymentMethod || d.dueDate) {
      html += '<div class="pay-meta">';
      if (d.paymentMethod) html += 'Forma: <strong>' + escHtml(d.paymentMethod) + '</strong>';
      if (d.paymentMethod && d.dueDate) html += ' &nbsp;|&nbsp; ';
      if (d.dueDate) html += 'Termin: <strong>' + escHtml(d.dueDate) + '</strong>';
      html += '</div>';
    }
    if (d.bankAccount) {
      const acctRaw = d.bankAccount.replace(/\s/g, '');
      const sellerNip = d.sellerNip || inv.sellerNip || '';
      html += '<div class="bank-row">'
            + '<span class="bank-nr">' + escHtml(d.bankAccount) + '</span>'
            + copyBtn(acctRaw, 'Kopiuj numer konta (bez spacji)')
            + '</div>';
      if (d.bankName || d.bankDescription) {
        html += '<div class="bank-name">' + escHtml(d.bankName || '') + (d.bankName && d.bankDescription ? ' — ' : '') + escHtml(d.bankDescription || '') + '</div>';
      }
      if (sellerNip) {
        const wlBtnId = 'wl-' + (inv.ksefNumber || '').replace(/[^a-z0-9]/gi, '') + '-' + Date.now();
        const nipAttr = sellerNip.replace(/&/g,'&amp;').replace(/"/g,'&quot;');
        const acctAttr = acctRaw.replace(/&/g,'&amp;').replace(/"/g,'&quot;');
        html += '<button class="btn-whitelist" id="' + wlBtnId + '"'
              + ' data-nip="' + nipAttr + '" data-account="' + acctAttr + '"'
              + ' onclick="checkWhitelist(this,this.dataset.nip,this.dataset.account)">&#128269; Sprawdź w Białej Liście</button>'
              + '<span class="wl-result" id="' + wlBtnId + '-result"></span>'
              + '<span class="wl-limit">Limit API MF: 100 zapytań/dzień per IP</span>';
      }
    }
    html += '</div>';
  }

  if (d.additionalDescriptions && d.additionalDescriptions.length > 0) {
    html += '<div class="preview-meta"><strong>Dodatkowe informacje:</strong><br>';
    for (const ad of d.additionalDescriptions) {
      html += escHtml(ad.key || '') + ': ' + escHtml(ad.value || '') + '<br>';
    }
    html += '</div>';
  }

  html += '<div class="preview-meta">';
  if (d.periodFrom || d.periodTo) html += 'Okres: ' + (d.periodFrom||'?') + ' \u2014 ' + (d.periodTo||'?') + '<br>';
  if (d.createdAt) html += 'Utworzono: ' + d.createdAt + '<br>';
  if (d.systemInfo) html += 'System: ' + escHtml(d.systemInfo) + '<br>';
  if (d.formCode) html += 'Formularz: ' + d.formCode + (d.schemaVersion ? ' v' + d.schemaVersion : '');
  html += '</div>';

  html += '</div>';
  pop.querySelector('.dp-body').innerHTML = html;
}

// --- Check existing files ---
async function checkExisting() {
  if (!invoices.length) return;
  try {
    const body = {
      outputDir: $('outputDir').value || '.',
      customFilenames: $('customFilenames').checked,
      separateByNip: $('separateByNip').checked,
      subjectType: $('subjectType') ? $('subjectType').value : null
    };
    const res = await fetch('/check-existing', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify(body) });
    if (!res.ok) return;
    fileStatus = await res.json();
    renderTable();
  } catch {}
}

// --- Quit ---
async function doQuit() {
  if (!confirm('Zamknac serwer GUI?')) return;
  try { await fetch('/quit', { method:'POST' }); } catch {}
  document.body.innerHTML = '<div style="text-align:center;padding:4rem;color:#666;font-size:1.2rem">Serwer zostal zamkniety. Mozesz zamknac ta karte.</div>';
}

</script>
</body>
</html>
""";
}
