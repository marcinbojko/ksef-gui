using System.ComponentModel;
using System.Text.Json;
using System.Xml.Linq;

using CommandLine;

using KSeF.Client.Core.Exceptions;
using KSeF.Client.Core.Interfaces.Clients;
using KSeF.Client.Core.Models.Authorization;
using KSeF.Client.Core.Models.Invoices;

using Microsoft.Extensions.DependencyInjection;

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace KSeFCli;

[Verb("Gui", HelpText = "Open interactive browser GUI for searching and downloading invoices.")]
public class GuiCommand : IWithConfigCommand
{
    [Option('o', "outputdir", Default = ".", HelpText = "Output directory to save downloaded files.")]
    public string OutputDir { get; set; } = ".";

    [Option('p', "pdf", HelpText = "Also generate PDF files when downloading.")]
    public bool Pdf { get; set; }

    [Option("useInvoiceNumber", HelpText = "Use InvoiceNumber instead of KsefNumber for filenames.")]
    public bool UseInvoiceNumber { get; set; }

    [Option("lan", HelpText = "Allow LAN access (listen on all network interfaces instead of localhost only).")]
    public bool Lan { get; set; }

    private List<InvoiceSummary>? _cachedInvoices;
    private IKSeFClient? _ksefClient;
    private IServiceScope? _scope;
    private WebProgressServer? _server;
    private Dictionary<string, string> _allProfiles = new(); // name → NIP
    private bool _setupRequired = false;

    private static readonly string PrefsPath = Path.Combine(CacheDir, "gui-prefs.json");

    private const int DefaultLanPort = 8150;

    private record GuiPrefs(
        string? OutputDir = null,
        bool? ExportXml = null,
        bool? ExportJson = null,
        bool? ExportPdf = null,
        bool? CustomFilenames = null,
        bool? SeparateByNip = null,
        string? SelectedProfile = null,
        int? LanPort = null,
        bool? DarkMode = null,
        bool? PreviewDarkMode = null);

    private record ProfileEditorData(
        string Name,
        string Nip,
        string Environment,
        string AuthMethod,
        string? Token,
        string? CertPrivateKeyFile,
        string? CertCertificateFile,
        string? CertPassword,
        string? CertPasswordEnv,
        string? CertPasswordFile);

    private record ConfigEditorData(
        string ActiveProfile,
        string ConfigFilePath,
        ProfileEditorData[] Profiles);

    private static GuiPrefs LoadPrefs()
    {
        try
        {
            if (File.Exists(PrefsPath))
            {
                string json = File.ReadAllText(PrefsPath);
                return JsonSerializer.Deserialize<GuiPrefs>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new GuiPrefs();
            }
        }
        catch { }
        return new GuiPrefs();
    }

    private static void SavePrefs(GuiPrefs prefs)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PrefsPath)!);
            File.WriteAllText(PrefsPath, JsonSerializer.Serialize(prefs));
        }
        catch { }
    }

    public override async Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        LogConfigSource();
        IServiceScope scope;
        try
        {
            scope = GetScope();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Config load warning (starting in setup mode): {ex.Message}");
            scope = new ServiceCollection().BuildServiceProvider().CreateScope();
        }
        using (scope)
        {
            return await ExecuteInScopeAsync(scope, cancellationToken).ConfigureAwait(false);
        }
    }

    public override async Task<int> ExecuteInScopeAsync(IServiceScope scope, CancellationToken cancellationToken)
    {
        // Check if config file exists before doing anything else
        _setupRequired = !File.Exists(Path.GetFullPath(ConfigFile));
        GuiPrefs savedPrefs = LoadPrefs();
        if (_setupRequired)
        {
            Console.WriteLine("No config file found. Opening setup wizard in GUI.");
            ConfigLoader.WriteTemplate(ConfigFile);
        }
        else
        {
            // Load all profiles from yaml (before Config() is first called)
            try
            {
                IDeserializer deserializer = new DeserializerBuilder()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();
                KsefCliConfig rawConfig = deserializer.Deserialize<KsefCliConfig>(File.ReadAllText(ConfigFile));
                foreach ((string? name, ProfileConfig? profile) in rawConfig.Profiles)
                {
                    _allProfiles[name] = profile.Nip;
                }
            }
            catch { }

            // Restore saved profile preference (must happen before first Config() call)
            if (string.IsNullOrEmpty(ActiveProfile)
                && !string.IsNullOrEmpty(savedPrefs.SelectedProfile)
                && _allProfiles.ContainsKey(savedPrefs.SelectedProfile))
            {
                ActiveProfile = savedPrefs.SelectedProfile;
                string restoredNip = _allProfiles.TryGetValue(ActiveProfile, out string? n) ? n : "?";
                Console.WriteLine($"Restored profile from preferences: {ActiveProfile} (NIP {restoredNip})");
            }
        }

        if (!_setupRequired && Pdf)
        {
            XML2PDFCommand.AssertPdfGeneratorAvailable();
        }

        Directory.CreateDirectory(OutputDir);

        // The scope passed in was created by ExecuteAsync BEFORE prefs were loaded (so it used the
        // yaml's active_profile, not the user's preferred profile). Reset the cache and build a fresh
        // scope now that ActiveProfile is correctly set.
        ResetCachedConfig();
        if (_setupRequired)
        {
            _scope = scope;
            _ksefClient = null;
        }
        else
        {
            try
            {
                IServiceScope newScope = GetScope();
                _scope?.Dispose();
                _scope = newScope;
                _ksefClient = _scope.ServiceProvider.GetRequiredService<IKSeFClient>();
            }
            catch (Exception ex)
            {
                // Saved/override profile no longer exists — clear it and let YAML decide
                Console.WriteLine($"Profile '{ActiveProfile}' could not be loaded: {ex.Message}. Falling back to YAML active profile.");
                ActiveProfile = "";
                ResetCachedConfig();
                try
                {
                    IServiceScope newScope = GetScope();
                    _scope?.Dispose();
                    _scope = newScope;
                    _ksefClient = _scope.ServiceProvider.GetRequiredService<IKSeFClient>();
                }
                catch (Exception ex2)
                {
                    Console.WriteLine($"Config load warning (entering setup mode): {ex2.Message}");
                    _setupRequired = true;
                    _scope = scope;
                    _ksefClient = null;
                }
            }
        }

        int lanPort = Lan ? (savedPrefs.LanPort ?? DefaultLanPort) : 0;
        using WebProgressServer server = new WebProgressServer(lan: Lan, port: lanPort);
        _server = server;

        server.OnSearch = SearchAsync;
        server.OnDownload = DownloadAsync;
        server.OnAuth = AuthAsync;
        server.OnInvoiceDetails = InvoiceDetailsAsync;
        server.OnQuit = () =>
        {
            Console.WriteLine("GUI: user requested quit.");
            Environment.Exit(0);
        };
        server.OnLoadPrefs = () =>
        {
            GuiPrefs prefs = LoadPrefs();
            return Task.FromResult<object>(new
            {
                outputDir = prefs.OutputDir ?? OutputDir,
                exportXml = prefs.ExportXml ?? true,
                exportJson = prefs.ExportJson ?? false,
                exportPdf = prefs.ExportPdf ?? true,
                customFilenames = prefs.CustomFilenames ?? false,
                separateByNip = prefs.SeparateByNip ?? false,
                profileNip = _setupRequired ? "" : Config().Nip,
                selectedProfile = ActiveProfile,
                allProfiles = _allProfiles,
                lanPort = prefs.LanPort ?? DefaultLanPort,
                darkMode = prefs.DarkMode ?? false,
                previewDarkMode = prefs.PreviewDarkMode ?? false,
                setupRequired = _setupRequired,
            });
        };
        server.OnSavePrefs = (json) =>
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            string? newProfile = root.TryGetProperty("selectedProfile", out JsonElement sp) ? sp.GetString() : null;
            Console.WriteLine($"OnSavePrefs: selectedProfile={newProfile ?? "(null)"}, ActiveProfile={ActiveProfile}, switching={!string.IsNullOrEmpty(newProfile) && newProfile != ActiveProfile}");
            SavePrefs(new GuiPrefs(
                OutputDir: root.TryGetProperty("outputDir", out JsonElement od) ? od.GetString() : null,
                ExportXml: root.TryGetProperty("exportXml", out JsonElement ex) ? ex.GetBoolean() : null,
                ExportJson: root.TryGetProperty("exportJson", out JsonElement ej) ? ej.GetBoolean() : null,
                ExportPdf: root.TryGetProperty("exportPdf", out JsonElement ep) ? ep.GetBoolean() : null,
                CustomFilenames: root.TryGetProperty("customFilenames", out JsonElement cf) ? cf.GetBoolean() : null,
                SeparateByNip: root.TryGetProperty("separateByNip", out JsonElement sn) ? sn.GetBoolean() : null,
                SelectedProfile: newProfile,
                LanPort: root.TryGetProperty("lanPort", out JsonElement lp) ? lp.GetInt32() : null,
                DarkMode: root.TryGetProperty("darkMode", out JsonElement dm) ? dm.GetBoolean() : null,
                PreviewDarkMode: root.TryGetProperty("previewDarkMode", out JsonElement pdm) ? pdm.GetBoolean() : null
            ));
            if (!string.IsNullOrEmpty(newProfile) && newProfile != ActiveProfile)
            {
                string previousProfile = ActiveProfile;
                ActiveProfile = newProfile;
                ResetCachedConfig();
                try
                {
                    IServiceScope switchedScope = GetScope();
                    IKSeFClient switchedClient = switchedScope.ServiceProvider.GetRequiredService<IKSeFClient>();
                    _scope?.Dispose();
                    _scope = switchedScope;
                    _ksefClient = switchedClient;
                    _cachedInvoices = null; // clear only after successful switch
                    string switchedNip = _allProfiles.TryGetValue(ActiveProfile, out string? switchedNipVal) ? switchedNipVal : "?";
                    Console.WriteLine($"Profile switched to: {ActiveProfile} (NIP {switchedNip})");
                }
                catch
                {
                    // Rollback: restore the valid previous profile so Config() keeps working
                    ActiveProfile = previousProfile;
                    ResetCachedConfig();
                    throw;
                }
            }
            return Task.CompletedTask;
        };
        server.OnCheckExisting = (checkParams) =>
        {
            if (_cachedInvoices == null)
            {
                return Task.FromResult<object>(Array.Empty<object>());
            }

            string outputDir = string.IsNullOrWhiteSpace(checkParams.OutputDir) ? OutputDir : checkParams.OutputDir;
            if (checkParams.SeparateByNip)
            {
                string nip = Config().Nip;
                if (!string.IsNullOrEmpty(nip))
                {
                    outputDir = Path.Combine(outputDir, nip);
                }
            }

            var result = _cachedInvoices.Select(inv =>
            {
                string fileName = BuildFileName(inv, checkParams.CustomFilenames);
                return new
                {
                    xml = File.Exists(Path.Combine(outputDir, $"{fileName}.xml")),
                    pdf = File.Exists(Path.Combine(outputDir, $"{fileName}.pdf")),
                    json = File.Exists(Path.Combine(outputDir, $"{fileName}.json")),
                };
            }).ToArray();

            return Task.FromResult<object>(result);
        };
        server.OnTokenStatus = () =>
        {
            try
            {
                TokenStore tokenStore = GetTokenStore();
                TokenStore.Key key = GetTokenStoreKey();
                TokenStore.Data? storedToken = tokenStore.GetToken(key);
                if (storedToken != null)
                {
                    return Task.FromResult<object>(new
                    {
                        accessTokenValidUntil = storedToken.Response.AccessToken.ValidUntil.ToLocalTime().ToString("o"),
                        refreshTokenValidUntil = storedToken.Response.RefreshToken.ValidUntil.ToLocalTime().ToString("o"),
                    });
                }
            }
            catch { }
            return Task.FromResult<object>(new { accessTokenValidUntil = (string?)null, refreshTokenValidUntil = (string?)null });
        };

        server.OnLoadConfig = () =>
        {
            IDeserializer deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
            KsefCliConfig rawConfig;
            try
            {
                rawConfig = deserializer.Deserialize<KsefCliConfig>(File.ReadAllText(ConfigFile));
            }
            catch
            {
                rawConfig = new KsefCliConfig { ActiveProfile = "", Profiles = new() };
            }
            ProfileEditorData[] profiles = rawConfig.Profiles
                .Select(kvp => new ProfileEditorData(
                    Name: kvp.Key,
                    Nip: kvp.Value.Nip,
                    Environment: kvp.Value.Environment,
                    AuthMethod: kvp.Value.Certificate != null ? "certificate" : "token",
                    Token: kvp.Value.Token,
                    CertPrivateKeyFile: kvp.Value.Certificate?.Private_Key_File,
                    CertCertificateFile: kvp.Value.Certificate?.Certificate_File,
                    CertPassword: kvp.Value.Certificate?.Password,
                    CertPasswordEnv: kvp.Value.Certificate?.Password_Env,
                    CertPasswordFile: kvp.Value.Certificate?.Password_File))
                .ToArray();
            return Task.FromResult<object>(new ConfigEditorData(
                ActiveProfile: rawConfig.ActiveProfile,
                ConfigFilePath: Path.GetFullPath(ConfigFile),
                Profiles: profiles));
        };
        server.OnSaveConfig = (json) =>
        {
            try
            {
                ConfigEditorData data = JsonSerializer.Deserialize<ConfigEditorData>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new InvalidOperationException("Failed to parse config data.");

                Dictionary<string, ProfileConfig> profiles = data.Profiles.ToDictionary(
                    p => p.Name,
                    p => p.AuthMethod == "certificate"
                        ? new ProfileConfig
                        {
                            Nip = p.Nip,
                            Environment = p.Environment,
                            Certificate = new CertificateConfig
                            {
                                Private_Key_File = p.CertPrivateKeyFile,
                                Certificate_File = p.CertCertificateFile,
                                Password = p.CertPassword,
                                Password_Env = p.CertPasswordEnv,
                                Password_File = p.CertPasswordFile,
                            }
                        }
                        : new ProfileConfig
                        {
                            Nip = p.Nip,
                            Environment = p.Environment,
                            Token = p.Token,
                        });

                KsefCliConfig newConfig = new KsefCliConfig
                {
                    ActiveProfile = data.ActiveProfile,
                    Profiles = profiles,
                };

                string configPath = Path.GetFullPath(ConfigFile);
                Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
                File.WriteAllText(configPath, ConfigLoader.Serialize(newConfig));
                Console.WriteLine($"Config saved: {configPath} ({profiles.Count} profile(s), active={data.ActiveProfile})");

                // Reload profile list for the profile dropdown
                _allProfiles.Clear();
                foreach ((string? name, ProfileConfig? profile) in profiles)
                {
                    _allProfiles[name] = profile.Nip;
                }

                // Update active profile and flush all cached config/token state
                // so the next API call reads the freshly saved yaml from disk
                string previousActiveProfile = ActiveProfile;
                ActiveProfile = data.ActiveProfile;
                ResetCachedConfig();
                try
                {
                    IServiceScope savedScope = GetScope();
                    IKSeFClient savedClient = savedScope.ServiceProvider.GetRequiredService<IKSeFClient>();
                    _scope?.Dispose();
                    _scope = savedScope;
                    _ksefClient = savedClient;
                }
                catch
                {
                    ActiveProfile = previousActiveProfile;
                    ResetCachedConfig();
                    throw;
                }

                // Clear setup mode — config now exists
                _setupRequired = false;

                return Task.FromResult("");
            }
            catch (Exception ex)
            {
                return Task.FromResult(ex.Message);
            }
        };

        server.Start(cancellationToken);
        Console.WriteLine($"GUI running at {server.LocalUrl}");
        if (Lan)
        {
            Console.WriteLine($"LAN access enabled — accessible on all network interfaces, port {server.Port}");
        }

        server.OpenBrowser();

        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }

        return 0;
    }

    private async Task<object> SearchAsync(SearchParams searchParams, CancellationToken ct)
    {
        ProfileConfig searchProfile = Config();
        Log.LogInformation($"GUI: search [profile={ActiveProfile}, nip={searchProfile.Nip}, env={searchProfile.Environment}, subject={searchParams.SubjectType}, from={searchParams.From}, to={searchParams.To ?? "–"}]");
        if (!Enum.TryParse(searchParams.SubjectType, true, out InvoiceSubjectType subjectType))
        {
            subjectType = searchParams.SubjectType.ToLowerInvariant() switch
            {
                "1" or "sprzedawca" => InvoiceSubjectType.Subject1,
                "2" or "nabywca" => InvoiceSubjectType.Subject2,
                "3" => InvoiceSubjectType.Subject3,
                "4" => InvoiceSubjectType.SubjectAuthorized,
                _ => throw new FormatException($"Invalid SubjectType: {searchParams.SubjectType}")
            };
        }

        if (!Enum.TryParse(searchParams.DateType, true, out DateType dateType))
        {
            throw new InvalidEnumArgumentException($"Invalid DateType: {searchParams.DateType}");
        }

        DateTime parsedFromDate = await ParseDate.Parse(searchParams.From).ConfigureAwait(false);
        DateTime? parsedToDate = null;
        if (!string.IsNullOrEmpty(searchParams.To))
        {
            parsedToDate = await ParseDate.Parse(searchParams.To).ConfigureAwait(false);
        }

        InvoiceQueryFilters filters = new InvoiceQueryFilters
        {
            SubjectType = subjectType,
            DateRange = new DateRange
            {
                From = parsedFromDate,
                To = parsedToDate,
                DateType = dateType,
            }
        };

        string accessToken = await GetAccessToken(_scope!, ct).ConfigureAwait(false);

        List<InvoiceSummary> allInvoices = new();
        PagedInvoiceResponse pagedResponse;
        int currentOffset = 0;
        const int pageSize = 100;
        const int maxRetries = 5;
        const int interPageDelayMs = 200;

        do
        {
            Log.LogInformation($"Fetching page with offset {currentOffset} and size {pageSize}");
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    pagedResponse = await _ksefClient!.QueryInvoiceMetadataAsync(
                        filters,
                        accessToken,
                        pageOffset: currentOffset,
                        pageSize: pageSize,
                        cancellationToken: ct).ConfigureAwait(false);
                    break;
                }
                catch (KsefRateLimitException ex) when (attempt < maxRetries)
                {
                    TimeSpan delay = ex.RecommendedDelay + TimeSpan.FromSeconds(attempt * 2);
                    Log.LogWarning($"Rate limited (HTTP 429). Retrying in {delay.TotalSeconds:F0}s... (attempt {attempt + 1}/{maxRetries})");
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
            }

            if (pagedResponse.Invoices != null)
            {
                allInvoices.AddRange(pagedResponse.Invoices);
            }

            currentOffset += pageSize;

            if (pagedResponse.HasMore == true)
            {
                await Task.Delay(interPageDelayMs, ct).ConfigureAwait(false);
            }
        } while (pagedResponse.HasMore == true);

        Log.LogInformation($"Found {allInvoices.Count} invoices.");
        _cachedInvoices = allInvoices;

        return allInvoices.Select(i => new
        {
            ksefNumber = i.KsefNumber,
            invoiceNumber = i.InvoiceNumber,
            issueDate = i.IssueDate.ToString("o"),
            sellerName = i.Seller?.Name,
            sellerNip = i.Seller?.Nip,
            buyerName = i.Buyer?.Name,
            grossAmount = i.GrossAmount,
            netAmount = i.NetAmount,
            vatAmount = i.VatAmount,
            currency = i.Currency,
        }).ToArray();
    }

    private async Task DownloadAsync(DownloadParams dlParams, CancellationToken ct)
    {
        if (_cachedInvoices == null || _cachedInvoices.Count == 0)
        {
            throw new InvalidOperationException("No invoices found. Search first.");
        }

        string outputDir = string.IsNullOrWhiteSpace(dlParams.OutputDir) ? OutputDir : dlParams.OutputDir;
        if (dlParams.SeparateByNip)
        {
            string nip = Config().Nip;
            if (!string.IsNullOrEmpty(nip))
            {
                outputDir = Path.Combine(outputDir, nip);
            }
        }
        Directory.CreateDirectory(outputDir);

        // Use a temp workdir for intermediate files; only copy desired output to final dir
        string workDir = Path.Combine(Path.GetTempPath(), $"ksefcli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        HashSet<int>? selected = dlParams.SelectedIndices is { Length: > 0 }
            ? new HashSet<int>(dlParams.SelectedIndices)
            : null;

        List<(int idx, InvoiceSummary inv)> toDownload = new();
        for (int i = 0; i < _cachedInvoices.Count; i++)
        {
            if (selected == null || selected.Contains(i))
            {
                toDownload.Add((i, _cachedInvoices[i]));
            }
        }

        bool wantPdf = dlParams.ExportPdf || Pdf;

        try
        {
            for (int n = 0; n < toDownload.Count; n++)
            {
                (int i, InvoiceSummary inv) = toDownload[n];
                string fileName = BuildFileName(inv, dlParams.CustomFilenames);

                if (_server != null)
                {
                    await _server.SendEventAsync("invoice_start", new { current = i, name = fileName, progress = n, total = toDownload.Count }).ConfigureAwait(false);
                }

                // Fetch XML from API (always needed — source data for all formats), retry on 429
                const int dlMaxRetries = 5;
                string invoiceXml = null!;
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        invoiceXml = await _ksefClient!.GetInvoiceAsync(
                            inv.KsefNumber,
                            await GetAccessToken(_scope!, ct).ConfigureAwait(false),
                            ct).ConfigureAwait(false);
                        break;
                    }
                    catch (KsefRateLimitException ex) when (attempt < dlMaxRetries)
                    {
                        TimeSpan delay = ex.RecommendedDelay + TimeSpan.FromSeconds(attempt * 2);
                        Log.LogWarning($"Rate limited downloading {inv.KsefNumber}. Retrying in {delay.TotalSeconds:F0}s... (attempt {attempt + 1}/{dlMaxRetries})");
                        await Task.Delay(delay, ct).ConfigureAwait(false);
                    }
                }

                // Write desired formats to workdir, then move to output
                if (dlParams.ExportJson)
                {
                    string tmpJson = Path.Combine(workDir, $"{fileName}.json");
                    await File.WriteAllTextAsync(tmpJson, JsonSerializer.Serialize(inv), ct).ConfigureAwait(false);
                    File.Move(tmpJson, Path.Combine(outputDir, $"{fileName}.json"), overwrite: true);
                }

                if (dlParams.ExportXml)
                {
                    string tmpXml = Path.Combine(workDir, $"{fileName}.xml");
                    await File.WriteAllTextAsync(tmpXml, invoiceXml, ct).ConfigureAwait(false);
                    File.Move(tmpXml, Path.Combine(outputDir, $"{fileName}.xml"), overwrite: true);
                    Console.WriteLine($"Saved invoice {inv.KsefNumber} to {Path.Combine(outputDir, $"{fileName}.xml")}");
                }

                if (wantPdf)
                {
                    if (_server != null)
                    {
                        await _server.SendEventAsync("invoice_done", new { current = i, file = fileName, pdf = true, progress = n, total = toDownload.Count }).ConfigureAwait(false);
                    }

                    byte[] pdfContent = await XML2PDFCommand.XML2PDF(invoiceXml, Quiet, ct).ConfigureAwait(false);
                    string tmpPdf = Path.Combine(workDir, $"{fileName}.pdf");
                    await File.WriteAllBytesAsync(tmpPdf, pdfContent, ct).ConfigureAwait(false);
                    string finalPdf = Path.Combine(outputDir, $"{fileName}.pdf");
                    File.Move(tmpPdf, finalPdf, overwrite: true);
                    Console.WriteLine($"Saved PDF for {inv.KsefNumber} to {finalPdf}");

                    if (_server != null)
                    {
                        await _server.SendEventAsync("pdf_done", new { current = i, file = finalPdf, progress = n, total = toDownload.Count }).ConfigureAwait(false);
                    }
                }
                else
                {
                    if (_server != null)
                    {
                        await _server.SendEventAsync("invoice_complete", new { current = i, file = fileName, progress = n, total = toDownload.Count }).ConfigureAwait(false);
                    }
                }
            }
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { }
        }

        SavePrefs(new GuiPrefs(
            OutputDir: dlParams.SeparateByNip ? Path.GetDirectoryName(outputDir) ?? outputDir : outputDir,
            ExportXml: dlParams.ExportXml,
            ExportJson: dlParams.ExportJson,
            ExportPdf: wantPdf,
            CustomFilenames: dlParams.CustomFilenames,
            SeparateByNip: dlParams.SeparateByNip));

        if (_server != null)
        {
            await _server.SendEventAsync("all_done", new { count = toDownload.Count }).ConfigureAwait(false);
        }
    }

    private string BuildFileName(InvoiceSummary inv, bool customScheme)
    {
        if (!customScheme)
        {
            return UseInvoiceNumber ? inv.InvoiceNumber : inv.KsefNumber;
        }

        string date = inv.IssueDate.ToString("yyyy-MM-dd");
        string seller = SanitizeFileName(inv.Seller?.Name ?? "nieznany");
        string currency = inv.Currency ?? "PLN";
        string ksef = inv.KsefNumber;
        return $"{date}-{seller}-{currency}-{ksef}";
    }

    private static string SanitizeFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string sanitized = string.Join("", name.Select(c => Array.IndexOf(invalid, c) >= 0 || c == ' ' ? '_' : c));
        if (sanitized.Length > 60)
        {
            sanitized = sanitized[..60];
        }

        return sanitized.Trim();
    }

    private async Task<string> AuthAsync(CancellationToken ct)
    {
        ProfileConfig activeProfile = Config();
        Log.LogInformation($"GUI: forcing re-authentication [profile={ActiveProfile}, nip={activeProfile.Nip}, env={activeProfile.Environment}]");
        AuthenticationOperationStatusResponse response = await Auth(_scope!, ct).ConfigureAwait(false);
        TokenStore tokenStore = GetTokenStore();
        TokenStore.Key key = GetTokenStoreKey();
        tokenStore.SetToken(key, new TokenStore.Data(response));
        string validUntil = response.AccessToken.ValidUntil.ToLocalTime().ToString("HH:mm:ss");
        Log.LogInformation($"GUI: auth OK, access token valid until {response.AccessToken.ValidUntil}");
        return $"Token wazny do {validUntil}";
    }

    private async Task<object> InvoiceDetailsAsync(int idx, CancellationToken ct)
    {
        if (_cachedInvoices == null || idx < 0 || idx >= _cachedInvoices.Count)
        {
            throw new InvalidOperationException($"Invalid invoice index: {idx}");
        }

        InvoiceSummary inv = _cachedInvoices[idx];
        string accessToken = await GetAccessToken(_scope!, ct).ConfigureAwait(false);
        string xml = await _ksefClient!.GetInvoiceAsync(inv.KsefNumber, accessToken, ct).ConfigureAwait(false);

        XDocument doc = XDocument.Parse(xml);
        XNamespace ns = "http://crd.gov.pl/wzor/2025/06/25/13775/";

        XElement? naglowek = doc.Descendants(ns + "Naglowek").FirstOrDefault();
        XElement? fa = doc.Descendants(ns + "Fa").FirstOrDefault();
        XElement? podmiot1 = doc.Descendants(ns + "Podmiot1").FirstOrDefault();
        XElement? podmiot2 = doc.Descendants(ns + "Podmiot2").FirstOrDefault();

        // Line items
        var lineItems = doc.Descendants(ns + "FaWiersz").Select(w => new
        {
            nr = w.Element(ns + "NrWierszaFa")?.Value,
            name = w.Element(ns + "P_7")?.Value,
            unit = w.Element(ns + "P_8A")?.Value,
            qty = w.Element(ns + "P_8B")?.Value,
            unitPrice = w.Element(ns + "P_9A")?.Value,
            netAmount = w.Element(ns + "P_11")?.Value,
            grossAmount = w.Element(ns + "P_11A")?.Value,
            vatRate = w.Element(ns + "P_12")?.Value,
            exchangeRate = w.Element(ns + "KursWaluty")?.Value,
        }).ToArray();

        // Additional descriptions
        var additionalDesc = doc.Descendants(ns + "DodatkowyOpis").Select(d => new
        {
            key = d.Element(ns + "Klucz")?.Value,
            value = d.Element(ns + "Wartosc")?.Value,
        }).ToArray();

        // Period
        XElement? okres = fa?.Element(ns + "OkresFa");

        // Addresses
        string? addr1 = FormatAddress(podmiot1?.Element(ns + "Adres"), ns);
        string? addr2 = FormatAddress(podmiot2?.Element(ns + "Adres"), ns);

        return new
        {
            createdAt = naglowek?.Element(ns + "DataWytworzeniaFa")?.Value,
            systemInfo = naglowek?.Element(ns + "SystemInfo")?.Value,
            schemaVersion = naglowek?.Element(ns + "KodFormularza")?.Attribute("wersjaSchemy")?.Value,
            formCode = naglowek?.Element(ns + "KodFormularza")?.Value,
            invoiceType = fa?.Element(ns + "RodzajFaktury")?.Value,
            currency = fa?.Element(ns + "KodWaluty")?.Value,
            periodFrom = okres?.Element(ns + "P_6_Od")?.Value,
            periodTo = okres?.Element(ns + "P_6_Do")?.Value,
            sellerAddress = addr1,
            buyerAddress = addr2,
            netTotal = fa?.Element(ns + "P_13_1")?.Value,
            vatTotal = fa?.Element(ns + "P_14_1")?.Value,
            vatTotalCurrency = fa?.Element(ns + "P_14_1W")?.Value,
            grossTotal = fa?.Element(ns + "P_15")?.Value,
            lineItems,
            additionalDescriptions = additionalDesc,
        };
    }

    private static string? FormatAddress(XElement? adres, XNamespace ns)
    {
        if (adres == null)
        {
            return null;
        }

        string? l1 = adres.Element(ns + "AdresL1")?.Value;
        string? l2 = adres.Element(ns + "AdresL2")?.Value;
        if (l1 == null && l2 == null)
        {
            return null;
        }

        return $"{l1}, {l2}";
    }
}
