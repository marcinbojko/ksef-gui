using System.Net;
using System.Text;
using System.Text.Json;

namespace KSeFCli;

internal record SearchParams(string SubjectType, string From, string? To, string DateType);
internal record DownloadParams(string OutputDir, int[]? SelectedIndices, bool CustomFilenames, bool ExportXml = true, bool ExportJson = false, bool ExportPdf = true, bool SeparateByNip = false);
internal record CheckExistingParams(string OutputDir, bool CustomFilenames, bool SeparateByNip);

internal sealed class WebProgressServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly List<StreamWriter> _sseClients = new();
    private readonly Lock _clientsLock = new();
    private CancellationTokenSource? _cts;
    public int Port { get; }

    /// <summary>Called when the user clicks "Szukaj". Receives search params, returns JSON-serializable result.</summary>
    public Func<SearchParams, CancellationToken, Task<object>>? OnSearch { get; set; }

    /// <summary>Called when the user clicks "Pobierz". Receives download params. Progress reported via SendEventAsync.</summary>
    public Func<DownloadParams, CancellationToken, Task>? OnDownload { get; set; }

    /// <summary>Called when the user clicks "Autoryzuj". Forces re-authentication and returns status message.</summary>
    public Func<CancellationToken, Task<string>>? OnAuth { get; set; }

    /// <summary>Called when user requests invoice details. Receives invoice index, returns JSON-serializable detail object.</summary>
    public Func<int, CancellationToken, Task<object>>? OnInvoiceDetails { get; set; }

    /// <summary>Called to check which invoices already exist as files in the output directory.</summary>
    public Func<CheckExistingParams, Task<object>>? OnCheckExisting { get; set; }

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

    /// <summary>Called to save modified config. Receives JSON string, returns empty string on success or error message.</summary>
    public Func<string, Task<string>>? OnSaveConfig { get; set; }

    public bool Lan { get; }

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
                    _sseClients.Remove(d);
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
                System.Diagnostics.Process.Start("xdg-open", url);
            else if (OperatingSystem.IsMacOS())
                System.Diagnostics.Process.Start("open", url);
            else if (OperatingSystem.IsWindows())
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd", $"/c start {url}") { CreateNoWindow = true });
        }
        catch
        {
            Console.WriteLine($"Open browser at: {url}");
        }
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
                if (OnLoadPrefs == null) return JsonSerializer.Serialize(new { });
                object prefs = await OnLoadPrefs().ConfigureAwait(false);
                return JsonSerializer.Serialize(prefs);
            }).ConfigureAwait(false);
        }
        else if (path == "/prefs" && method == "POST")
        {
            await HandleAction(ctx, ct, async () =>
            {
                string body = await new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding).ReadToEndAsync(ct).ConfigureAwait(false);
                if (OnSavePrefs != null)
                    await OnSavePrefs(body).ConfigureAwait(false);
                return JsonSerializer.Serialize(new { ok = true });
            }).ConfigureAwait(false);
        }
        else if (path == "/auth" && method == "POST")
        {
            await HandleAction(ctx, ct, async () =>
            {
                if (OnAuth == null) throw new InvalidOperationException("Auth not configured");
                string message = await OnAuth(ct).ConfigureAwait(false);
                return JsonSerializer.Serialize(new { ok = true, message });
            }).ConfigureAwait(false);
        }
        else if (path == "/search" && method == "POST")
        {
            await HandleAction(ctx, ct, async () =>
            {
                if (OnSearch == null) throw new InvalidOperationException("Search not configured");
                string body = await new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding).ReadToEndAsync(ct).ConfigureAwait(false);
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
                string dirPath = ctx.Request.QueryString["path"] ?? Directory.GetCurrentDirectory();
                dirPath = Path.GetFullPath(dirPath);
                if (!Directory.Exists(dirPath))
                    throw new DirectoryNotFoundException($"Directory not found: {dirPath}");

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
                string body = await new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding).ReadToEndAsync(ct).ConfigureAwait(false);
                using JsonDocument doc = JsonDocument.Parse(body);
                string dirPath = doc.RootElement.GetProperty("path").GetString()
                    ?? throw new InvalidOperationException("Missing path");
                dirPath = Path.GetFullPath(dirPath);
                Directory.CreateDirectory(dirPath);
                return JsonSerializer.Serialize(new { ok = true, path = dirPath });
            }).ConfigureAwait(false);
        }
        else if (path == "/download" && method == "POST")
        {
            await HandleAction(ctx, ct, async () =>
            {
                if (OnDownload == null) throw new InvalidOperationException("Download not configured");
                string body = await new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding).ReadToEndAsync(ct).ConfigureAwait(false);
                DownloadParams dlParams = JsonSerializer.Deserialize<DownloadParams>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new DownloadParams(".", null, false);
                await OnDownload(dlParams, ct).ConfigureAwait(false);
                return JsonSerializer.Serialize(new { ok = true });
            }).ConfigureAwait(false);
        }
        else if (path == "/invoice-details" && method == "GET")
        {
            await HandleAction(ctx, ct, async () =>
            {
                if (OnInvoiceDetails == null) throw new InvalidOperationException("Details not configured");
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
                if (OnCheckExisting == null) return "[]";
                string body = await new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding).ReadToEndAsync(ct).ConfigureAwait(false);
                CheckExistingParams checkParams = JsonSerializer.Deserialize<CheckExistingParams>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new CheckExistingParams(".", false, false);
                object result = await OnCheckExisting(checkParams).ConfigureAwait(false);
                return JsonSerializer.Serialize(result);
            }).ConfigureAwait(false);
        }
        else if (path == "/token-status" && method == "GET")
        {
            await HandleAction(ctx, ct, async () =>
            {
                if (OnTokenStatus == null) return JsonSerializer.Serialize(new { });
                object status = await OnTokenStatus().ConfigureAwait(false);
                return JsonSerializer.Serialize(status);
            }).ConfigureAwait(false);
        }
        else if (path == "/config-editor" && method == "GET")
        {
            await HandleAction(ctx, ct, async () =>
            {
                if (OnLoadConfig == null) return JsonSerializer.Serialize(new { });
                object data = await OnLoadConfig().ConfigureAwait(false);
                return JsonSerializer.Serialize(data, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            }).ConfigureAwait(false);
        }
        else if (path == "/config-editor" && method == "POST")
        {
            await HandleAction(ctx, ct, async () =>
            {
                string body = await new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding).ReadToEndAsync(ct).ConfigureAwait(false);
                string error = OnSaveConfig != null
                    ? await OnSaveConfig(body).ConfigureAwait(false)
                    : "";
                return string.IsNullOrEmpty(error)
                    ? JsonSerializer.Serialize(new { ok = true })
                    : JsonSerializer.Serialize(new { ok = false, error });
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
        catch (Exception ex)
        {
            string errJson = JsonSerializer.Serialize(new { error = ex.Message });
            byte[] body = Encoding.UTF8.GetBytes(errJson);
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.StatusCode = 500;
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
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
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
.search-form{display:flex;gap:.5rem;flex-wrap:wrap;align-items:end;margin-bottom:1rem;padding:1rem;background:#fff;border-radius:8px;box-shadow:0 1px 3px rgba(0,0,0,.1)}
.field{display:flex;flex-direction:column;gap:.2rem}
.field label{font-size:.75rem;font-weight:600;color:#555}
.field select,.field input{padding:.4rem .6rem;border:1px solid #ccc;border-radius:4px;font-size:.85rem}
.field input{width:140px}
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
.cfg-modal{width:600px;max-height:85vh}
.cfg-profile-card{border:1px solid #ddd;border-radius:8px;padding:.8rem 1rem;margin-bottom:.8rem;position:relative}
.cfg-profile-card .cfg-card-title{font-weight:600;font-size:.9rem;margin-bottom:.6rem;display:flex;align-items:center;gap:.5rem}
.cfg-field{display:flex;flex-direction:column;margin-bottom:.5rem}
.cfg-field label{font-size:.75rem;color:#666;margin-bottom:.2rem}
.cfg-field input,.cfg-field select{padding:.35rem .5rem;border:1px solid #ccc;border-radius:4px;font-size:.85rem}
.cfg-del{position:absolute;top:.5rem;right:.6rem;background:none;border:none;color:#c62828;cursor:pointer;font-size:1rem;line-height:1}
.cfg-del:hover{color:#b71c1c}
.cfg-pw-wrap{display:flex;gap:.3rem}
.cfg-pw-wrap input{flex:1}
.cfg-pw-wrap button{padding:.3rem .5rem;font-size:.8rem}
.btn-danger{background:#c62828;color:#fff}
.btn-danger:hover:not(:disabled){background:#b71c1c}
.prefs-panel{border-left:3px solid #546e7a}
.status{padding:.6rem .8rem;border-radius:8px;margin-bottom:.75rem;font-weight:500;font-size:.9rem}
.status.info{background:#e3f2fd;color:#1565c0}
.status.done{background:#e8f5e9;color:#2e7d32}
.status.error{background:#fbe9e7;color:#c62828}
.status.idle{background:#f5f5f5;color:#666}
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
.filter-bar{display:flex;gap:.4rem;align-items:center;flex-wrap:wrap;margin-bottom:.6rem;padding:.5rem .8rem;background:#fff;border-radius:8px;box-shadow:0 1px 3px rgba(0,0,0,.1);display:none}
.filter-bar.visible{display:flex}
.filter-label{font-size:.78rem;font-weight:600;color:#555;margin-right:.2rem}
.chip{display:inline-flex;align-items:center;padding:.25rem .7rem;border-radius:16px;font-size:.78rem;cursor:pointer;border:1.5px solid #bbb;background:#fff;color:#555;transition:all .15s;user-select:none}
.chip:hover{border-color:#888}
.chip.active{background:#1976d2;color:#fff;border-color:#1976d2}
.chip .chip-count{margin-left:.3rem;opacity:.7;font-size:.7rem}
.btn-details{background:none;border:1px solid #bbb;border-radius:4px;padding:.15rem .4rem;font-size:.75rem;cursor:pointer;color:#666;white-space:nowrap}
.btn-details:hover{border-color:#1976d2;color:#1976d2;background:#e3f2fd}
.btn-preview{background:none;border:1px solid #bbb;border-radius:4px;padding:.15rem .4rem;font-size:.75rem;cursor:pointer;color:#666;white-space:nowrap}
.btn-preview:hover{border-color:#2e7d32;color:#2e7d32;background:#e8f5e9}
.badge{display:inline-block;padding:.1rem .3rem;border-radius:3px;font-size:.58rem;font-weight:700;letter-spacing:.3px;margin-right:.15rem;line-height:1.2}
.badge-xml{background:#1976d2;color:#fff}
.badge-pdf{background:#c62828;color:#fff}
.badge-json{background:#e65100;color:#fff}
tr.has-files>td{background:rgba(46,125,50,.05)}
tr.has-files:hover>td{background:rgba(46,125,50,.1)}
.preview-page{padding:2rem;max-width:800px;margin:0 auto;font-size:.85rem;line-height:1.5}
.preview-title{text-align:center;font-size:1.1rem;font-weight:700;margin-bottom:.3rem}
.preview-subtitle{text-align:center;font-size:.8rem;color:#666;margin-bottom:1.2rem}
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
.detail-overlay{display:flex;position:fixed;top:0;left:0;width:100%;height:100%;background:rgba(0,0,0,.4);z-index:200;align-items:center;justify-content:center;overflow-y:auto;padding:1rem}
.detail-popover{position:relative;background:#fff;border-radius:10px;box-shadow:0 8px 30px rgba(0,0,0,.25);width:600px;max-width:95vw;max-height:90vh;overflow-y:auto;padding:0;font-size:.82rem;margin:auto}
.detail-popover .dp-header{display:flex;justify-content:space-between;align-items:center;padding:.6rem 1rem;background:#fafafa;border-bottom:1px solid #e0e0e0;border-radius:10px 10px 0 0;position:sticky;top:0;z-index:1}
.detail-popover .dp-header h3{margin:0;font-size:.9rem}
.detail-popover .dp-close{background:none;border:none;font-size:1.2rem;cursor:pointer;color:#888;padding:0 .3rem}
.detail-popover .dp-close:hover{color:#333}
.detail-popover .dp-body{padding:.8rem 1rem}
.detail-popover .dp-section{margin-bottom:.8rem}
.detail-popover .dp-section h4{font-size:.78rem;color:#555;text-transform:uppercase;letter-spacing:.5px;margin-bottom:.3rem;border-bottom:1px solid #eee;padding-bottom:.2rem}
.detail-popover .dp-row{display:flex;gap:.5rem;padding:.15rem 0}
.detail-popover .dp-label{font-weight:600;color:#555;min-width:120px;flex-shrink:0}
.detail-popover .dp-val{color:#333;word-break:break-word}
.detail-popover table{font-size:.78rem;margin-top:.3rem;box-shadow:none}
.detail-popover th{font-size:.72rem;padding:.3rem .5rem}
.detail-popover td{padding:.3rem .5rem;font-size:.78rem}
.detail-popover .dp-loading{text-align:center;padding:2rem;color:#999}
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
body.dark .field label{color:#aaa}
body.dark .field select,body.dark .field input{background:#2a2a2a;border-color:#444;color:#e0e0e0}
body.dark .prefs-panel{border-left-color:#78909c}
body.dark .status.info{background:#0d2137;color:#64b5f6}
body.dark .status.done{background:#1b3a1b;color:#81c784}
body.dark .status.error{background:#3e1212;color:#ef9a9a}
body.dark .status.idle{background:#1e1e1e;color:#888}
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
body.dark .chip{background:#2a2a2a;border-color:#555;color:#ccc}
body.dark .chip:hover{border-color:#888}
body.dark .chip.active{background:#1565c0;color:#fff;border-color:#1565c0}
body.dark .btn-details{border-color:#555;color:#aaa}
body.dark .btn-details:hover{border-color:#64b5f6;color:#64b5f6;background:#0d2137}
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
body.dark .btn-preview{border-color:#555;color:#aaa}
body.dark .btn-preview:hover{border-color:#81c784;color:#81c784;background:#1b3a1b}
body.dark tr.has-files>td{background:rgba(129,199,132,.06)}
body.dark tr.has-files:hover>td{background:rgba(129,199,132,.12)}
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
body.dark .cfg-field label{color:#aaa}
body.dark .cfg-field input,body.dark .cfg-field select{background:#2a2a2a;border-color:#444;color:#e0e0e0}
body.dark .token-info{color:#888}
body.dark .sort-arrow{color:#666}
</style>
</head>
<body>
<h1>KSeFCli - Faktury</h1>
<div class="search-form">
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
    <input id="fromDate" type="month" min="2026-02">
  </div>
  <div class="field">
    <label>Do (miesiac)</label>
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
  <button class="btn-auth auth-unknown" id="btnAuth" onclick="doAuth()" title="Odswierz token KSeF">&#128274; Autoryzuj</button>
  <span class="token-info" id="tokenInfo"></span>
  <button class="btn-prefs" id="btnPrefs" onclick="togglePrefs()" title="Preferencje">&#9881; Preferencje</button>
  <button class="btn-config" id="btnConfig" onclick="openConfigEditor()" title="Edytor konfiguracji">&#9998; Konfiguracja</button>
  <button class="btn-danger" onclick="doQuit()" title="Zamknij serwer GUI" style="margin-left:auto">&#9746; Zakoncz</button>
</div>
<div class="search-form prefs-panel" id="prefsPanel" style="padding:.6rem 1rem;gap:.8rem;display:none;flex-wrap:wrap">
  <label style="font-size:.82rem;display:flex;align-items:center;gap:.4rem;cursor:pointer"><input type="checkbox" id="darkMode" onchange="toggleDarkMode()"> Tryb ciemny</label>
  <label style="font-size:.82rem;display:flex;align-items:center;gap:.4rem;cursor:pointer"><input type="checkbox" id="previewDarkMode" onchange="savePrefs()"> Podglad ciemny</label>
  <span style="color:#ccc;margin:0 .3rem">|</span>
  <div class="field">
    <label>Katalog wyjsciowy</label>
    <div style="display:flex;gap:.3rem">
      <input id="outputDir" type="text" value="." placeholder="/tmp/faktury" style="width:240px" onchange="savePrefs()">
      <button class="btn-primary" type="button" onclick="openBrowser()" style="padding:.4rem .6rem;font-size:.8rem" title="Wybierz folder">&#128193;</button>
    </div>
  </div>
  <span style="color:#ccc;margin:0 .3rem">|</span>
  <label style="font-size:.82rem;display:flex;align-items:center;gap:.4rem;cursor:pointer"><input type="checkbox" id="separateByNip" onchange="savePrefs()"> Separuj po NIP (<span id="profileNipLabel"></span>)</label>
  <span style="color:#ccc;margin:0 .3rem">|</span>
  <label style="font-size:.82rem;display:flex;align-items:center;gap:.4rem;cursor:pointer"><input type="checkbox" id="customFilenames" onchange="savePrefs()"> Nazwy plikow: data-sprzedawca-waluta-ksef</label>
  <span style="color:#ccc;margin:0 .3rem">|</span>
  <span style="font-size:.78rem;font-weight:600;color:#555">Eksport:</span>
  <label style="font-size:.82rem;display:flex;align-items:center;gap:.3rem;cursor:pointer"><input type="checkbox" id="expXml" checked onchange="savePrefs()"> XML</label>
  <label style="font-size:.82rem;display:flex;align-items:center;gap:.3rem;cursor:pointer"><input type="checkbox" id="expPdf" checked onchange="savePrefs()"> PDF</label>
  <label style="font-size:.82rem;display:flex;align-items:center;gap:.3rem;cursor:pointer"><input type="checkbox" id="expJson" onchange="savePrefs()"> JSON</label>
  <span style="color:#ccc;margin:0 .3rem">|</span>
  <div class="field" style="flex-direction:row;align-items:center;gap:.3rem">
    <label style="font-size:.78rem;font-weight:600;color:#555;white-space:nowrap">Port LAN:</label>
    <input id="lanPort" type="number" value="8150" min="1024" max="65535" style="width:70px;font-size:.8rem;padding:.2rem .4rem" onchange="savePrefs()">
    <span style="font-size:.7rem;color:#999">(restart)</span>
  </div>
  <span style="color:#ccc;margin:0 .3rem">|</span>
  <button class="btn-sm btn-prefs" onclick="savePrefs();$('prefsPanel').style.display='none'" style="padding:.3rem .8rem">Zapisz preferencje</button>
</div>
<div id="setupBanner" style="display:none;align-items:center;gap:.8rem;background:#fff3e0;border:1px solid #ffb300;border-radius:8px;padding:.7rem 1rem;margin-bottom:.5rem;font-size:.9rem;color:#6d4c00">
  <span>&#9888;</span>
  <span>Brak konfiguracji. Skonfiguruj profil w edytorze, aby korzystac z aplikacji.</span>
  <button class="btn-config" onclick="openConfigEditor()" style="margin-left:auto;padding:.3rem .8rem;font-size:.82rem">&#9998; Otwórz edytor</button>
</div>
<div class="status idle" id="status">Wprowadz kryteria i kliknij "Szukaj".</div>
<div class="progress-wrap" id="progressWrap"><div class="progress-bar" id="bar"></div></div>
<div class="filter-bar" id="filterBar"><span class="filter-label">Waluta:</span></div>
<div class="sel-toolbar" id="selToolbar">
  <button class="btn-sm btn-outline" onclick="selectAll()">Zaznacz wszystkie</button>
  <button class="btn-sm btn-outline" onclick="clearSelection()">Odznacz wszystkie</button>
  <span class="sel-count" id="selCount"></span>
</div>
<div id="tableWrap"></div>
<div class="toolbar" id="downloadBar" style="display:none">
  <button class="btn-success" id="btnDownloadSel" onclick="doDownload(true)">Pobierz zaznaczone</button>
  <button class="btn-success" id="btnDownload" onclick="doDownload(false)" style="background:#1976d2">Pobierz wszystkie</button>
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
<div class="modal-overlay" id="configModal" onclick="onConfigModalOverlayClick(event)">
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
      <button class="btn-success" onclick="saveConfigEditor()" style="padding:.4rem 1.2rem">Zapisz</button>
    </div>
  </div>
</div>
<script>
const $ = id => document.getElementById(id);
const status = $('status'), bar = $('bar'), progressWrap = $('progressWrap'),
      tableWrap = $('tableWrap'), downloadBar = $('downloadBar'), filterBar = $('filterBar'),
      selToolbar = $('selToolbar'), selCount = $('selCount'),
      btnSearch = $('btnSearch'), btnDownload = $('btnDownload'), btnDownloadSel = $('btnDownloadSel'),
      countLabel = $('countLabel');
let invoices = [], total = 0, completed = 0, sortCol = null, sortAsc = true, es = null;
let activeCurrencies = new Set();
let selectedInvoices = new Set();
let fileStatus = [];

// Set default month to current month
(function initDates() {
  const now = new Date();
  const cur = now.getFullYear() + '-' + String(now.getMonth() + 1).padStart(2, '0');
  $('fromDate').value = cur;
  $('toDate').value = cur;
  const maxMonth = cur;
  $('fromDate').max = maxMonth;
  $('toDate').max = maxMonth;
})();

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
      if (p.darkMode) { $('darkMode').checked = true; document.body.classList.add('dark'); }
      if (p.previewDarkMode) { $('previewDarkMode').checked = true; }
      if (p.profileNip) $('profileNipLabel').textContent = p.profileNip;
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
loadPrefs();

function applySetupMode(required) {
  const banner = $('setupBanner');
  if (banner) banner.style.display = required ? 'flex' : 'none';
  const blocked = [btnSearch, btnDownload, btnDownloadSel, $('btnAuth')];
  for (const b of blocked) { if (b) b.disabled = required; }
  if (required) setTimeout(() => openConfigEditor(), 400);
}

// --- Token status ---
let tokenExpiry = null; // Date object for access token expiry
let tokenRefreshExpiry = null;

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
  } else if (diffMin <= 5) {
    btn.classList.add('auth-warning');
    info.textContent = 'wygasa o ' + timeStr;
    info.style.color = '#e65100';
  } else {
    info.textContent = 'wazny do ' + timeStr;
    info.style.color = '#2e7d32';
  }
}

// Fetch token status on load and periodically
fetchTokenStatus();
setInterval(() => { updateAuthButton(); }, 15000);
setInterval(() => { fetchTokenStatus(); }, 60000);

function savePrefs() {
  const prefs = {
    outputDir: $('outputDir').value || '.',
    exportXml: $('expXml').checked,
    exportJson: $('expJson').checked,
    exportPdf: $('expPdf').checked,
    customFilenames: $('customFilenames').checked,
    separateByNip: $('separateByNip').checked,
    darkMode: $('darkMode').checked,
    previewDarkMode: $('previewDarkMode').checked,
    selectedProfile: $('profileSelect').value,
    lanPort: parseInt($('lanPort').value) || 8150
  };
  fetch('/prefs', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify(prefs) }).catch(() => {});
}

function onProfileChange() {
  const chosen = $('profileSelect').value;
  savePrefs();
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
  setStatus('Profil zmieniony na "' + chosen + '".', 'idle');
  fetchTokenStatus();
}

function togglePrefs() {
  const panel = $('prefsPanel');
  const visible = panel.style.display !== 'none';
  panel.style.display = visible ? 'none' : 'flex';
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

function onConfigModalOverlayClick(e) {
  if (e.target === $('configModal')) closeConfigEditor();
}

function renderConfigEditor() {
  if (!cfgData) return;
  let html = '';
  // Active profile selector
  html += '<div class="cfg-field" style="margin-bottom:1rem"><label>Aktywny profil</label>';
  html += '<select id="cfgActiveProfile">';
  for (const p of cfgData.profiles) {
    const sel = p.name === cfgData.activeProfile ? ' selected' : '';
    html += '<option value="' + esc(p.name) + '"' + sel + '>' + esc(p.name) + '</option>';
  }
  html += '</select></div>';
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
  return '<div class="cfg-profile-card" id="cfgCard' + i + '">' +
    '<button class="cfg-del" onclick="deleteProfile(' + i + ')" title="Usun profil">&times;</button>' +
    '<div class="cfg-card-title">Profil #' + (i+1) + '</div>' +
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
  }
}

function addProfile() {
  if (!cfgData) return;
  cfgData.profiles.push({ name: 'nowy-profil', nip: '', environment: 'test', authMethod: 'token', token: '' });
  renderConfigEditor();
  $('cfgCard' + (cfgData.profiles.length - 1))?.scrollIntoView({ behavior: 'smooth' });
}

function deleteProfile(i) {
  if (!cfgData) return;
  if (cfgData.profiles.length <= 1) { alert('Musi pozostac co najmniej jeden profil.'); return; }
  if (!confirm('Usunac profil "' + cfgData.profiles[i].name + '"?')) return;
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
    });
  }
  const payload = { activeProfile, configFilePath: cfgData.configFilePath, profiles };
  try {
    const res = await fetch('/config-editor', { method: 'POST', headers: {'Content-Type':'application/json'}, body: JSON.stringify(payload) });
    const data = await res.json();
    if (data.ok) {
      cfgData = payload;
      $('cfgSaveMsg').textContent = 'Zapisano!';
      $('cfgSaveMsg').style.display = '';
      setTimeout(() => { $('cfgSaveMsg').style.display = 'none'; }, 3000);
      // Refresh profile dropdown and token status (new/edited profile = new token state)
      await loadPrefs();
      applySetupMode(false);
      await fetchTokenStatus();
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
  savePrefs();
}

function setStatus(text, cls) { status.textContent = text; status.className = 'status ' + cls; }

function getFilteredInvoices() {
  if (activeCurrencies.size === 0) return invoices;
  return invoices.filter(i => activeCurrencies.has(i.currency || ''));
}

function buildCurrencyFilter() {
  const counts = {};
  for (const inv of invoices) {
    const c = inv.currency || '(brak)';
    counts[c] = (counts[c] || 0) + 1;
  }
  const currencies = Object.keys(counts).sort();
  if (currencies.length <= 1) { filterBar.classList.remove('visible'); return; }
  let html = '<span class="filter-label">Waluta:</span>';
  for (const c of currencies) {
    const active = activeCurrencies.has(c) ? ' active' : '';
    html += '<span class="chip' + active + '" onclick="toggleCurrency(\'' + c + '\')">' + c + '<span class="chip-count">(' + counts[c] + ')</span></span>';
  }
  filterBar.innerHTML = html;
  filterBar.classList.add('visible');
}

function toggleCurrency(c) {
  if (activeCurrencies.has(c)) activeCurrencies.delete(c);
  else activeCurrencies.add(c);
  buildCurrencyFilter();
  renderTable();
  updateFilteredCount();
}

function updateFilteredCount() {
  const filtered = getFilteredInvoices();
  const suffix = activeCurrencies.size > 0 ? ' (filtr: ' + filtered.length + ' z ' + total + ')' : '';
  countLabel.textContent = total + ' faktur' + suffix;
}

function renderTable() {
  if (!invoices.length) { tableWrap.innerHTML = '<div class="empty">Brak faktur.</div>'; return; }
  const cols = [
    {key:'ksefNumber', label:'Numer KSeF'},
    {key:'invoiceNumber', label:'Numer faktury'},
    {key:'issueDate', label:'Data wystawienia'},
    {key:'sellerName', label:'Sprzedawca'},
    {key:'buyerName', label:'Nabywca'},
    {key:'grossAmount', label:'Kwota brutto', cls:'amount'},
    {key:'currency', label:'Waluta'},
  ];
  let sorted = [...getFilteredInvoices()];
  if (sortCol !== null) {
    sorted.sort((a,b) => {
      let va = a[sortCol] ?? '', vb = b[sortCol] ?? '';
      if (typeof va === 'number' && typeof vb === 'number') return sortAsc ? va - vb : vb - va;
      va = String(va).toLowerCase(); vb = String(vb).toLowerCase();
      return sortAsc ? va.localeCompare(vb) : vb.localeCompare(va);
    });
  }
  let html = '<table><thead><tr>';
  html += '<th style="width:2rem"><input type="checkbox" id="checkAll" onchange="toggleAll(this.checked)"></th>';
  html += '<th style="width:2rem"></th>';
  html += '<th style="width:3rem"></th>';
  html += '<th style="width:3rem"></th>';
  html += '<th style="width:5rem">Pliki</th>';
  for (const c of cols) {
    const arrow = sortCol === c.key ? (sortAsc ? ' &#9650;' : ' &#9660;') : '';
    html += '<th data-col="' + c.key + '" onclick="sortBy(\'' + c.key + '\')">' + c.label + '<span class="sort-arrow">' + arrow + '</span></th>';
  }
  html += '</tr></thead><tbody>';
  for (let i = 0; i < sorted.length; i++) {
    const inv = sorted[i];
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
    html += '<td><button class="btn-details" onclick="showDetails(' + idx + ', event)" title="Szczegoly faktury">&#128269;</button></td>';
    html += '<td><button class="btn-preview" onclick="showPreview(' + idx + ')" title="Podglad faktury">&#128196;</button></td>';
    html += '<td>' + badges + '</td>';
    html += '<td class="col-ksef" title="' + (inv.ksefNumber||'') + '">' + (inv.ksefNumber||'') + '</td>';
    html += '<td>' + (inv.invoiceNumber||'') + '</td>';
    html += '<td>' + date + '</td>';
    html += '<td class="col-name" title="NIP: ' + (inv.sellerNip||'') + '">' + (inv.sellerName||'') + '</td>';
    html += '<td class="col-name">' + (inv.buyerName||'') + '</td>';
    html += '<td class="amount">' + (inv.grossAmount != null ? inv.grossAmount.toFixed(2) : '') + '</td>';
    html += '<td>' + (inv.currency||'') + '</td>';
    html += '</tr>';
  }
  html += '</tbody></table>';
  tableWrap.innerHTML = html;
  updateSelectionUI();
}

function sortBy(col) {
  if (sortCol === col) sortAsc = !sortAsc;
  else { sortCol = col; sortAsc = true; }
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

function updateSelectionUI() {
  const n = selectedInvoices.size;
  selToolbar.classList.toggle('visible', invoices.length > 0);
  selCount.textContent = n > 0 ? 'Zaznaczono: ' + n : '';
  btnDownloadSel.disabled = n === 0;
  btnDownloadSel.textContent = n > 0 ? 'Pobierz zaznaczone (' + n + ')' : 'Pobierz zaznaczone';
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
        btnSearch.disabled = false; btnDownload.disabled = false;
        btnDownloadSel.disabled = selectedInvoices.size === 0;
        checkExisting();
        break;
      case 'error': {
        const row = document.getElementById('row-' + d.current);
        const icon = document.getElementById('icon-' + d.current);
        if (row) row.className = 'error';
        if (icon) icon.innerHTML = '&#10007;';
        if (d.fatal) {
          setStatus('Blad: ' + d.message, 'error');
          btnSearch.disabled = false; btnDownload.disabled = false;
          btnDownloadSel.disabled = selectedInvoices.size === 0;
        }
        break;
      }
    }
  };
}

let dlTotal = 0;
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

async function doSearch() {
  btnSearch.disabled = true; btnDownload.disabled = true; btnDownloadSel.disabled = true;
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
    invoices = await res.json();
    for (let i = 0; i < invoices.length; i++) invoices[i]._idx = i;
    total = invoices.length;
    countLabel.textContent = total + ' faktur';
    setStatus('Znaleziono ' + total + ' faktur.', total > 0 ? 'info' : 'idle');
    buildCurrencyFilter();
    renderTable();
    downloadBar.style.display = total > 0 ? 'flex' : 'none';
    btnSearch.disabled = false; btnDownload.disabled = total === 0; btnDownloadSel.disabled = true;
    checkExisting();
  } catch (err) {
    setStatus('Blad: ' + err.message, 'error');
    btnSearch.disabled = false;
  }
}

async function doDownload(selOnly) {
  const indices = selOnly ? [...selectedInvoices] : null;
  const dlCount = selOnly ? selectedInvoices.size : total;
  if (selOnly && dlCount === 0) { setStatus('Nie zaznaczono faktur.', 'error'); return; }

  btnSearch.disabled = true; btnDownload.disabled = true; btnDownloadSel.disabled = true;
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
      separateByNip: $('separateByNip').checked
    };
    const res = await fetch('/download', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify(body) });
    if (!res.ok) { const e = await res.json(); throw new Error(e.error || 'Download failed'); }
  } catch (err) {
    setStatus('Blad: ' + err.message, 'error');
    btnSearch.disabled = false; btnDownload.disabled = false; btnDownloadSel.disabled = selectedInvoices.size === 0;
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
  savePrefs();
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
function escAttr(s) { return s.replace(/\\/g, '\\\\').replace(/'/g, "\\'"); }

// --- Invoice Details ---
let detailOverlay = null;
const detailCache = {};

function closeDetails() {
  if (detailOverlay) { detailOverlay.remove(); detailOverlay = null; }
}

document.addEventListener('keydown', (e) => { if (e.key === 'Escape') { closeDetails(); closeConfigEditor(); } });

async function showDetails(idx, event) {
  closeDetails();

  const overlay = document.createElement('div');
  overlay.className = 'detail-overlay';
  overlay.addEventListener('click', (e) => { if (e.target === overlay) closeDetails(); });

  const pop = document.createElement('div');
  pop.className = 'detail-popover';
  pop.innerHTML = '<div class="dp-header"><h3>Szczegoly faktury</h3><button class="dp-close" onclick="closeDetails()">&times;</button></div><div class="dp-body"><div class="dp-loading"><span class="spinner">&#8635;</span> Pobieranie...</div></div>';
  overlay.appendChild(pop);
  document.body.appendChild(overlay);
  detailOverlay = overlay;

  try {
    let data = detailCache[idx];
    if (!data) {
      const res = await fetch('/invoice-details?idx=' + idx);
      if (!res.ok) { const e = await res.json(); throw new Error(e.error || 'Failed'); }
      data = await res.json();
      detailCache[idx] = data;
    }
    if (detailOverlay !== overlay) return; // closed while fetching
    renderDetails(pop, data);
  } catch (err) {
    if (detailOverlay === overlay) {
      pop.querySelector('.dp-body').innerHTML = '<div style="color:#c62828;padding:1rem">Blad: ' + escHtml(err.message) + '</div>';
    }
  }
}

function renderDetails(pop, d) {
  let html = '<div class="dp-section"><h4>Naglowek</h4>';
  html += dpRow('Data utworzenia', d.createdAt);
  html += dpRow('System', d.systemInfo);
  html += dpRow('Formularz', d.formCode + (d.schemaVersion ? ' (v' + d.schemaVersion + ')' : ''));
  html += dpRow('Typ faktury', d.invoiceType);
  html += dpRow('Waluta', d.currency);
  if (d.periodFrom || d.periodTo) html += dpRow('Okres', (d.periodFrom || '?') + ' — ' + (d.periodTo || '?'));
  html += '</div>';

  html += '<div class="dp-section"><h4>Kwoty</h4>';
  html += dpRow('Netto', d.netTotal);
  html += dpRow('VAT', d.vatTotal + (d.vatTotalCurrency ? ' (walutowy: ' + d.vatTotalCurrency + ')' : ''));
  html += dpRow('Brutto', d.grossTotal);
  html += '</div>';

  if (d.sellerAddress || d.buyerAddress) {
    html += '<div class="dp-section"><h4>Adresy</h4>';
    if (d.sellerAddress) html += dpRow('Sprzedawca', d.sellerAddress);
    if (d.buyerAddress) html += dpRow('Nabywca', d.buyerAddress);
    html += '</div>';
  }

  if (d.lineItems && d.lineItems.length > 0) {
    html += '<div class="dp-section"><h4>Pozycje (' + d.lineItems.length + ')</h4>';
    html += '<table><thead><tr><th>#</th><th>Nazwa</th><th>J.m.</th><th>Ilosc</th><th>Cena</th><th>Netto</th><th>Brutto</th><th>VAT%</th>';
    if (d.lineItems.some(l => l.exchangeRate)) html += '<th>Kurs</th>';
    html += '</tr></thead><tbody>';
    for (const l of d.lineItems) {
      html += '<tr><td>' + (l.nr||'') + '</td><td>' + escHtml(l.name||'') + '</td><td>' + (l.unit||'') + '</td><td>' + (l.qty||'') + '</td><td class="amount">' + (l.unitPrice||'') + '</td><td class="amount">' + (l.netAmount||'') + '</td><td class="amount">' + (l.grossAmount||'') + '</td><td>' + (l.vatRate||'') + '</td>';
      if (d.lineItems.some(li => li.exchangeRate)) html += '<td>' + (l.exchangeRate||'') + '</td>';
      html += '</tr>';
    }
    html += '</tbody></table></div>';
  }

  if (d.additionalDescriptions && d.additionalDescriptions.length > 0) {
    html += '<div class="dp-section"><h4>Dodatkowe opisy</h4>';
    for (const ad of d.additionalDescriptions) {
      html += dpRow(escHtml(ad.key || ''), escHtml(ad.value || ''));
    }
    html += '</div>';
  }

  pop.querySelector('.dp-body').innerHTML = html;
}

function dpRow(label, value) {
  if (!value && value !== 0) return '';
  return '<div class="dp-row"><span class="dp-label">' + label + '</span><span class="dp-val">' + value + '</span></div>';
}

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

  try {
    let data = detailCache[idx];
    if (!data) {
      const res = await fetch('/invoice-details?idx=' + idx);
      if (!res.ok) { const e = await res.json(); throw new Error(e.error || 'Failed'); }
      data = await res.json();
      detailCache[idx] = data;
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
  let html = '<div class="preview-page">';
  html += '<div class="preview-title">Faktura ' + escHtml(inv.invoiceNumber || '') + '</div>';
  html += '<div class="preview-subtitle">KSeF: ' + escHtml(inv.ksefNumber || '') + ' | Data: ' + (inv.issueDate ? inv.issueDate.substring(0,10) : '');
  if (d.invoiceType) html += ' | Typ: ' + escHtml(d.invoiceType);
  if (d.currency) html += ' | Waluta: ' + escHtml(d.currency);
  html += '</div>';

  html += '<div class="preview-parties">';
  html += '<div class="preview-party"><h5>Sprzedawca</h5>';
  html += '<div class="name">' + escHtml(inv.sellerName || '') + '</div>';
  if (inv.sellerNip) html += '<div class="nip">NIP: ' + escHtml(inv.sellerNip) + '</div>';
  if (d.sellerAddress) html += '<div class="addr">' + escHtml(d.sellerAddress) + '</div>';
  html += '</div>';
  html += '<div class="preview-party"><h5>Nabywca</h5>';
  html += '<div class="name">' + escHtml(inv.buyerName || '') + '</div>';
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
  if (d.grossTotal) html += '<tr><td class="label total">Brutto</td><td class="amount total">' + d.grossTotal + ' ' + (d.currency||'') + '</td></tr>';
  html += '</table></div>';

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
      separateByNip: $('separateByNip').checked
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
