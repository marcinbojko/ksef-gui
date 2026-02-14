using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

using Xunit;

namespace KSeFCli.Tests;

public class WebProgressServerTests : IDisposable
{
    private WebProgressServer? _server;

    public void Dispose()
    {
        _server?.Dispose();
    }

    [Fact]
    public void Constructor_DefaultPort_CreatesServerWithRandomPort()
    {
        _server = new WebProgressServer();
        Assert.True(_server.Port > 0);
        Assert.False(_server.Lan);
    }

    [Fact]
    public void Constructor_SpecificPort_UsesProvidedPort()
    {
        int port = 18888;
        _server = new WebProgressServer(port: port);
        Assert.Equal(port, _server.Port);
    }

    [Fact]
    public void Constructor_LanMode_SetsLanProperty()
    {
        _server = new WebProgressServer(lan: true);
        Assert.True(_server.Lan);
    }

    [Fact]
    public void LocalUrl_ReturnsCorrectUrl()
    {
        _server = new WebProgressServer(port: 19000);
        Assert.Equal("http://localhost:19000/", _server.LocalUrl);
    }

    [Fact]
    public void MkdirRoot_CanBeSet()
    {
        string testPath = "/tmp/test";
        _server = new WebProgressServer() { MkdirRoot = testPath };
        Assert.Equal(testPath, _server.MkdirRoot);
    }

    [Fact]
    public void MkdirRoot_DefaultsToRoot()
    {
        _server = new WebProgressServer();
        Assert.Equal("/", _server.MkdirRoot);
    }

    [Fact]
    public void DefaultBrowseDir_DefaultsToUserHome()
    {
        _server = new WebProgressServer();
        Assert.Equal(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            _server.DefaultBrowseDir);
    }

    [Fact]
    public async Task Start_StartsServerSuccessfully()
    {
        _server = new WebProgressServer();
        CancellationTokenSource cts = new CancellationTokenSource();
        _server.Start(cts.Token);
        await Task.Delay(100);
        using HttpClient client = new HttpClient();
        HttpResponseMessage response = await client.GetAsync(_server.LocalUrl);
        Assert.True(response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.NotFound);
        cts.Cancel();
    }

    [Fact]
    public async Task SendEventAsync_WithNoClients_DoesNotThrow()
    {
        _server = new WebProgressServer();
        CancellationTokenSource cts = new CancellationTokenSource();
        _server.Start(cts.Token);
        await _server.SendEventAsync("test", new { message = "hello" });
        cts.Cancel();
    }

    [Fact]
    public async Task PrefsEndpoint_Get_ReturnsEmptyObjectWhenNoCallback()
    {
        _server = new WebProgressServer();
        CancellationTokenSource cts = new CancellationTokenSource();
        _server.Start(cts.Token);
        using HttpClient client = new HttpClient();
        HttpResponseMessage response = await client.GetAsync($"{_server.LocalUrl}prefs");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string json = await response.Content.ReadAsStringAsync();
        Assert.NotNull(json);
        cts.Cancel();
    }

    [Fact]
    public async Task PrefsEndpoint_Get_CallsOnLoadPrefs()
    {
        _server = new WebProgressServer();
        bool callbackInvoked = false;
        object expectedData = new { test = "value" };
        _server.OnLoadPrefs = () =>
        {
            callbackInvoked = true;
            return Task.FromResult(expectedData);
        };
        CancellationTokenSource cts = new CancellationTokenSource();
        _server.Start(cts.Token);
        using HttpClient client = new HttpClient();
        HttpResponseMessage response = await client.GetAsync($"{_server.LocalUrl}prefs");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(callbackInvoked);
        string json = await response.Content.ReadAsStringAsync();
        Assert.Contains("test", json);
        Assert.Contains("value", json);
        cts.Cancel();
    }

    [Fact]
    public async Task PrefsEndpoint_Post_CallsOnSavePrefs()
    {
        _server = new WebProgressServer();
        string? receivedJson = null;
        _server.OnSavePrefs = (json) =>
        {
            receivedJson = json;
            return Task.CompletedTask;
        };
        CancellationTokenSource cts = new CancellationTokenSource();
        _server.Start(cts.Token);
        string testData = "{\"setting\":\"value\"}";
        using HttpClient client = new HttpClient();
        HttpResponseMessage response = await client.PostAsync($"{_server.LocalUrl}prefs",
            new StringContent(testData, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(receivedJson);
        Assert.Contains("setting", receivedJson);
        cts.Cancel();
    }

    [Fact]
    public async Task AuthEndpoint_Post_CallsOnAuth()
    {
        _server = new WebProgressServer();
        bool callbackInvoked = false;
        _server.OnAuth = (ct) =>
        {
            callbackInvoked = true;
            return Task.FromResult("Token valid until 12:00");
        };
        CancellationTokenSource cts = new CancellationTokenSource();
        _server.Start(cts.Token);
        using HttpClient client = new HttpClient();
        HttpResponseMessage response = await client.PostAsync($"{_server.LocalUrl}auth",
            new StringContent("", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(callbackInvoked);
        string json = await response.Content.ReadAsStringAsync();
        Assert.Contains("ok", json);
        Assert.Contains("message", json);
        cts.Cancel();
    }

    [Fact]
    public async Task AuthEndpoint_WithoutCallback_ReturnsError()
    {
        _server = new WebProgressServer();
        CancellationTokenSource cts = new CancellationTokenSource();
        _server.Start(cts.Token);
        using HttpClient client = new HttpClient();
        HttpResponseMessage response = await client.PostAsync($"{_server.LocalUrl}auth",
            new StringContent("", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string json = await response.Content.ReadAsStringAsync();
        Assert.Contains("error", json);
        cts.Cancel();
    }

    [Fact]
    public async Task SearchEndpoint_Post_CallsOnSearch()
    {
        _server = new WebProgressServer();
        SearchParams? receivedParams = null;
        _server.OnSearch = (searchParams, ct) =>
        {
            receivedParams = searchParams;
            return Task.FromResult<object>(new[] { new { ksefNumber = "123" } });
        };
        CancellationTokenSource cts = new CancellationTokenSource();
        _server.Start(cts.Token);
        string requestBody = JsonSerializer.Serialize(new
        {
            subjectType = "1",
            from = "2024-01-01",
            to = "2024-01-31",
            dateType = "InvoiceDate"
        });
        using HttpClient client = new HttpClient();
        HttpResponseMessage response = await client.PostAsync($"{_server.LocalUrl}search",
            new StringContent(requestBody, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(receivedParams);
        Assert.Equal("1", receivedParams.SubjectType);
        Assert.Equal("2024-01-01", receivedParams.From);
        Assert.Equal("2024-01-31", receivedParams.To);
        cts.Cancel();
    }

    [Fact]
    public async Task DownloadEndpoint_Post_CallsOnDownload()
    {
        _server = new WebProgressServer();
        DownloadParams? receivedParams = null;
        _server.OnDownload = (downloadParams, ct) =>
        {
            receivedParams = downloadParams;
            return Task.CompletedTask;
        };
        CancellationTokenSource cts = new CancellationTokenSource();
        _server.Start(cts.Token);
        string requestBody = JsonSerializer.Serialize(new
        {
            outputDir = "/tmp/test",
            selectedIndices = new[] { 0, 1, 2 },
            customFilenames = true,
            exportXml = true,
            exportJson = false,
            exportPdf = true
        });
        using HttpClient client = new HttpClient();
        HttpResponseMessage response = await client.PostAsync($"{_server.LocalUrl}download",
            new StringContent(requestBody, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(receivedParams);
        Assert.Equal("/tmp/test", receivedParams.OutputDir);
        Assert.NotNull(receivedParams.SelectedIndices);
        Assert.Equal(3, receivedParams.SelectedIndices.Length);
        Assert.True(receivedParams.CustomFilenames);
        cts.Cancel();
    }

    [Fact]
    public async Task InvoiceDetailsEndpoint_Get_CallsOnInvoiceDetails()
    {
        _server = new WebProgressServer();
        int? receivedIndex = null;
        _server.OnInvoiceDetails = (idx, ct) =>
        {
            receivedIndex = idx;
            return Task.FromResult<object>(new { invoiceNumber = "FV/2024/001" });
        };
        CancellationTokenSource cts = new CancellationTokenSource();
        _server.Start(cts.Token);
        using HttpClient client = new HttpClient();
        HttpResponseMessage response = await client.GetAsync($"{_server.LocalUrl}invoice-details?idx=5");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(5, receivedIndex);
        string json = await response.Content.ReadAsStringAsync();
        Assert.Contains("invoiceNumber", json);
        cts.Cancel();
    }

    [Fact]
    public async Task CheckExistingEndpoint_Post_CallsOnCheckExisting()
    {
        _server = new WebProgressServer();
        CheckExistingParams? receivedParams = null;
        _server.OnCheckExisting = (checkParams) =>
        {
            receivedParams = checkParams;
            return Task.FromResult<object>(Array.Empty<object>());
        };
        CancellationTokenSource cts = new CancellationTokenSource();
        _server.Start(cts.Token);
        string requestBody = JsonSerializer.Serialize(new
        {
            outputDir = "/tmp/output",
            customFilenames = false,
            separateByNip = true
        });
        using HttpClient client = new HttpClient();
        HttpResponseMessage response = await client.PostAsync($"{_server.LocalUrl}check-existing",
            new StringContent(requestBody, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(receivedParams);
        Assert.Equal("/tmp/output", receivedParams.OutputDir);
        Assert.False(receivedParams.CustomFilenames);
        Assert.True(receivedParams.SeparateByNip);
        cts.Cancel();
    }

    [Fact]
    public async Task TokenStatusEndpoint_Get_CallsOnTokenStatus()
    {
        _server = new WebProgressServer();
        bool callbackInvoked = false;
        _server.OnTokenStatus = () =>
        {
            callbackInvoked = true;
            return Task.FromResult<object>(new { valid = true });
        };
        CancellationTokenSource cts = new CancellationTokenSource();
        _server.Start(cts.Token);
        using HttpClient client = new HttpClient();
        HttpResponseMessage response = await client.GetAsync($"{_server.LocalUrl}token-status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(callbackInvoked);
        cts.Cancel();
    }

    [Fact]
    public async Task ConfigEndpoint_Get_CallsOnLoadConfig()
    {
        _server = new WebProgressServer();
        bool callbackInvoked = false;
        _server.OnLoadConfig = () =>
        {
            callbackInvoked = true;
            return Task.FromResult<object>(new { activeProfile = "test" });
        };
        CancellationTokenSource cts = new CancellationTokenSource();
        _server.Start(cts.Token);
        using HttpClient client = new HttpClient();
        HttpResponseMessage response = await client.GetAsync($"{_server.LocalUrl}config-editor");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(callbackInvoked);
        cts.Cancel();
    }

    [Fact]
    public async Task ConfigEndpoint_Post_CallsOnSaveConfig()
    {
        _server = new WebProgressServer();
        string? receivedJson = null;
        _server.OnSaveConfig = (json) =>
        {
            receivedJson = json;
            return Task.FromResult("");
        };
        CancellationTokenSource cts = new CancellationTokenSource();
        _server.Start(cts.Token);
        string testConfig = "{\"activeProfile\":\"prod\"}";
        using HttpClient client = new HttpClient();
        HttpResponseMessage response = await client.PostAsync($"{_server.LocalUrl}config-editor",
            new StringContent(testConfig, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(receivedJson);
        Assert.Contains("activeProfile", receivedJson);
        cts.Cancel();
    }

    [Fact]
    public async Task QuitEndpoint_Post_CallsOnQuit()
    {
        _server = new WebProgressServer();
        bool callbackInvoked = false;
        _server.OnQuit = () => { callbackInvoked = true; };
        CancellationTokenSource cts = new CancellationTokenSource();
        _server.Start(cts.Token);
        using HttpClient client = new HttpClient();
        try
        {
            await client.PostAsync($"{_server.LocalUrl}quit",
                new StringContent("", Encoding.UTF8, "application/json"));
        }
        catch
        {
        }
        await Task.Delay(100);
        Assert.True(callbackInvoked);
        cts.Cancel();
    }

    [Fact]
    public void Dispose_StopsServer()
    {
        _server = new WebProgressServer();
        CancellationTokenSource cts = new CancellationTokenSource();
        _server.Start(cts.Token);
        _server.Dispose();
        _server = null;
    }

    [Fact]
    public async Task ErrorHandling_SearchEndpoint_ReturnsErrorJson()
    {
        _server = new WebProgressServer();
        _server.OnSearch = (searchParams, ct) => throw new InvalidOperationException("Test error");
        CancellationTokenSource cts = new CancellationTokenSource();
        _server.Start(cts.Token);
        string requestBody = JsonSerializer.Serialize(new
        {
            subjectType = "1",
            from = "2024-01-01",
            dateType = "InvoiceDate"
        });
        using HttpClient client = new HttpClient();
        HttpResponseMessage response = await client.PostAsync($"{_server.LocalUrl}search",
            new StringContent(requestBody, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string json = await response.Content.ReadAsStringAsync();
        Assert.Contains("error", json);
        Assert.Contains("Test error", json);
        cts.Cancel();
    }

    [Fact]
    public async Task EventsEndpoint_ReturnsEventStream()
    {
        _server = new WebProgressServer();
        CancellationTokenSource cts = new CancellationTokenSource();
        _server.Start(cts.Token);
        using HttpClient client = new HttpClient();
        CancellationTokenSource requestCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            using HttpResponseMessage response = await client.GetAsync($"{_server.LocalUrl}events",
                HttpCompletionOption.ResponseHeadersRead, requestCts.Token);
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        }
        catch (TaskCanceledException)
        {
        }
        cts.Cancel();
    }

    [Fact]
    public async Task MultipleRequests_HandleConcurrently()
    {
        _server = new WebProgressServer();
        int callCount = 0;
        _server.OnTokenStatus = async () =>
        {
            Interlocked.Increment(ref callCount);
            await Task.Delay(50).ConfigureAwait(false);
            return new { valid = true };
        };
        CancellationTokenSource cts = new CancellationTokenSource();
        _server.Start(cts.Token);
        using HttpClient client = new HttpClient();
        Task<HttpResponseMessage>[] tasks = new Task<HttpResponseMessage>[5];
        for (int i = 0; i < 5; i++)
        {
            tasks[i] = client.GetAsync($"{_server.LocalUrl}token-status");
        }
        HttpResponseMessage[] responses = await Task.WhenAll(tasks).ConfigureAwait(false);
        Assert.Equal(5, callCount);
        foreach (HttpResponseMessage response in responses)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        cts.Cancel();
    }
}
