using System.ComponentModel;
using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

using CommandLine;

using KSeF.Client.Core.Exceptions;
using KSeF.Client.Core.Interfaces.Clients;
using KSeF.Client.Core.Interfaces.Services;
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

    [Option("port", HelpText = "Override the listening port (takes priority over saved preferences).")]
    public int? PortOverride { get; set; }

    private List<InvoiceSummary>? _cachedInvoices;
    private SearchParams? _lastSearchParams;
    private IKSeFClient? _ksefClient;
    private IServiceScope? _scope;
    private WebProgressServer? _server;
    private InvoiceCache _invoiceCache = null!; // initialized in RunAsync after logger is configured
    private Dictionary<string, string> _allProfiles = new(); // name → NIP
    private bool _setupRequired = false;

    private static readonly string PrefsPath = Path.Join(ConfigDir, "gui-prefs.json");
    // Legacy location used before preferences were moved from ~/.cache to ~/.config.
    private static readonly string LegacyPrefsPath = Path.Join(CacheDir, "gui-prefs.json");
    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    private const int DefaultLanPort = 18150;

    /// <summary>Persistent GUI-only preferences per profile name (not stored in YAML).</summary>
    private record ProfilePrefs(
        bool? IncludeInAutoRefresh = null,
        string? SlackWebhookUrl = null,
        string? TeamsWebhookUrl = null,
        string? NotificationEmail = null,
        bool? ExtendedNotifications = null,
        bool? AutoRefreshCurrentMonth = null);

    private record GuiPrefs(
        string? OutputDir = null,
        bool? ExportXml = null,
        bool? ExportJson = null,
        bool? ExportPdf = null,
        bool? CustomFilenames = null,
        bool? SeparateByNip = null,
        string? SelectedProfile = null,
        int? LanPort = null,
        bool? ListenOnAll = null,
        bool? DarkMode = null,
        bool? PreviewDarkMode = null,
        bool? DetailsDarkMode = null,
        string? PdfColorScheme = null,
        int? AutoRefreshMinutes = null,
        bool? JsonConsoleLog = null,
        int? DisplayLimit = null,
        string? SmtpHost = null,
        int? SmtpPort = null,
        string? SmtpSecurity = null,
        string? SmtpUser = null,
        string? SmtpPassword = null,
        string? SmtpFrom = null,
        bool? ShowIncomeChart = null,
        Dictionary<string, ProfilePrefs>? ProfilePrefs = null);

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
        string? CertPasswordFile,
        bool IncludeInAutoRefresh = true,
        string? SlackWebhookUrl = null,
        string? TeamsWebhookUrl = null,
        string? NotificationEmail = null,
        bool ExtendedNotifications = false,
        bool AutoRefreshCurrentMonth = true);

    private record ConfigEditorData(
        string ActiveProfile,
        string ConfigFilePath,
        ProfileEditorData[] Profiles);

    private static GuiPrefs LoadPrefs()
    {
        // One-time migration: move gui-prefs.json from the old cache location to ~/.config.
        if (!File.Exists(PrefsPath) && File.Exists(LegacyPrefsPath))
        {
            bool moved = false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PrefsPath)!);
                File.Move(LegacyPrefsPath, PrefsPath);
                Log.LogInformation($"[prefs] Migrated gui-prefs.json from {LegacyPrefsPath} to {PrefsPath}");
                moved = true;
            }
            catch (IOException ex)
            {
                Log.LogWarning($"[prefs] File.Move migration failed ({ex.GetType().Name}: {ex.Message}); trying non-destructive copy.");
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.LogWarning($"[prefs] File.Move migration failed ({ex.GetType().Name}: {ex.Message}); trying non-destructive copy.");
            }

            if (!moved)
            {
                // Non-destructive fallback: copy without removing the legacy file.
                // If this also fails, the read loop below will still find and parse LegacyPrefsPath.
                try
                {
                    File.Copy(LegacyPrefsPath, PrefsPath, overwrite: false);
                    Log.LogInformation($"[prefs] Copied gui-prefs.json to {PrefsPath} (legacy file preserved at {LegacyPrefsPath})");
                }
                catch (IOException ex)
                {
                    Log.LogWarning($"[prefs] File.Copy also failed ({ex.GetType().Name}: {ex.Message}); will read legacy prefs directly.");
                }
                catch (UnauthorizedAccessException ex)
                {
                    Log.LogWarning($"[prefs] File.Copy also failed ({ex.GetType().Name}: {ex.Message}); will read legacy prefs directly.");
                }
            }
        }

        // Try PrefsPath first, then fall back to LegacyPrefsPath (still present when copy failed).
        // Log and continue to the next candidate on any read/parse failure; return defaults only
        // if both are unavailable or unparseable.
        foreach (string candidate in new[] { PrefsPath, LegacyPrefsPath })
        {
            try
            {
                if (!File.Exists(candidate)) { continue; }
                string json = File.ReadAllText(candidate);
                // Log instead of silently swallowing: a JsonException here (e.g. from a partially
                // written file) would cause every subsequent SavePrefs call to use an empty GuiPrefs
                // as the base, wiping SMTP credentials and webhook URLs from disk.
                return JsonSerializer.Deserialize<GuiPrefs>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new GuiPrefs();
            }
            catch (IOException ex)
            {
                Log.LogWarning($"[prefs] Failed to load {candidate}: {ex.GetType().Name}: {ex.Message}.");
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.LogWarning($"[prefs] Failed to load {candidate}: {ex.GetType().Name}: {ex.Message}.");
            }
            catch (JsonException ex)
            {
                Log.LogWarning($"[prefs] Failed to parse {candidate}: {ex.GetType().Name}: {ex.Message}.");
            }
        }
        return new GuiPrefs();
    }

    private static void SavePrefs(GuiPrefs prefs)
    {
        // Unique suffix per invocation avoids collisions when saves run concurrently.
        string tempPath = Path.Join(Path.GetDirectoryName(PrefsPath)!, $".gui-prefs-{Guid.NewGuid():N}.tmp");
        bool committed = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PrefsPath)!);
            // Write atomically: write to a unique temp file first, then rename.
            // File.WriteAllText is not atomic — a crash mid-write leaves a corrupt JSON file
            // that permanently breaks LoadPrefs (silent catch → empty GuiPrefs → data loss).
            File.WriteAllText(tempPath, JsonSerializer.Serialize(prefs));
            File.Move(tempPath, PrefsPath, overwrite: true);
            committed = true;
        }
        catch (IOException ex)
        {
            Log.LogWarning($"[prefs] Failed to save {PrefsPath}: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.LogWarning($"[prefs] Failed to save {PrefsPath}: {ex.Message}");
        }
        catch (JsonException ex)
        {
            Log.LogWarning($"[prefs] Failed to save {PrefsPath}: {ex.Message}");
        }
        finally
        {
            if (!committed)
            {
                // Best-effort cleanup; File.Delete does not throw if file does not exist.
                try { File.Delete(tempPath); }
                catch (IOException)
                {
                    // Swallow I/O errors during best-effort cleanup; main failure is already logged.
                }
                catch (UnauthorizedAccessException ex)
                {
                    // Log permission issues so repeated cleanup failures are visible for diagnostics.
                    Log.LogWarning($"[prefs] Failed to delete temporary prefs file '{tempPath}': {ex.Message}");
                }
            }
        }
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
            Log.LogWarning($"Config load warning (starting in setup mode): {ex.Message}");
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
        Log.ConfigureLogging(Verbose, Quiet, savedPrefs.JsonConsoleLog ?? false);
        _invoiceCache = new InvoiceCache();
        if (_setupRequired)
        {
            Log.LogInformation("No config file found. Opening setup wizard in GUI.");
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
                Log.LogInformation($"Restored profile from preferences: {ActiveProfile} (NIP {restoredNip})");
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
                Log.LogWarning($"Profile '{ActiveProfile}' could not be loaded: {ex.Message}. Falling back to YAML active profile.");
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
                    Log.LogWarning($"Config load warning (entering setup mode): {ex2.Message}");
                    _setupRequired = true;
                    _scope = scope;
                    _ksefClient = null;
                }
            }
        }

        int lanPort = PortOverride ?? savedPrefs.LanPort ?? DefaultLanPort;
        bool listenOnAll = Lan || (savedPrefs.ListenOnAll ?? false);
        string resolvedOutputDir = Path.GetFullPath(OutputDir);
        using WebProgressServer server = new WebProgressServer(lan: listenOnAll, port: lanPort)
        {
            MkdirRoot = resolvedOutputDir,
            DefaultBrowseDir = Directory.Exists(resolvedOutputDir)
                ? resolvedOutputDir
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        _server = server;

        server.OnSearch = SearchAsync;
        server.OnDownload = DownloadAsync;
        server.OnDownloadSummary = GenerateSummaryAsync;
        server.OnAuth = AuthAsync;
        server.OnInvoiceDetails = InvoiceDetailsAsync;
        server.OnQuit = () =>
        {
            Log.LogInformation("GUI: user requested quit.");
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
                profileName = ActiveProfile,
                selectedProfile = ActiveProfile,
                allProfiles = _allProfiles,
                lanPort = prefs.LanPort ?? DefaultLanPort,
                listenOnAll = prefs.ListenOnAll ?? false,
                serverUrl = server.LocalUrl,
                darkMode = prefs.DarkMode ?? false,
                previewDarkMode = prefs.PreviewDarkMode ?? false,
                detailsDarkMode = prefs.DetailsDarkMode ?? false,
                pdfColorScheme = prefs.PdfColorScheme ?? "navy",
                autoRefreshMinutes = prefs.AutoRefreshMinutes ?? 0,
                jsonConsoleLog = prefs.JsonConsoleLog ?? false,
                displayLimit = prefs.DisplayLimit ?? 50,
                profilePrefs = prefs.ProfilePrefs ?? new Dictionary<string, ProfilePrefs>(),
                smtpHost = prefs.SmtpHost,
                smtpPort = prefs.SmtpPort,
                smtpSecurity = prefs.SmtpSecurity,
                smtpUser = prefs.SmtpUser,
                hasSmtpPassword = !string.IsNullOrEmpty(prefs.SmtpPassword),
                smtpFrom = prefs.SmtpFrom,
                showIncomeChart = prefs.ShowIncomeChart ?? true,
                setupRequired = _setupRequired,
            });
        };
        server.OnSavePrefs = (json) =>
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            string? newProfile = root.TryGetProperty("selectedProfile", out JsonElement sp) ? sp.GetString() : null;
            bool jsonConsoleLog = root.TryGetProperty("jsonConsoleLog", out JsonElement jcl) && jcl.GetBoolean();
            Log.LogDebug($"SavePrefs: selectedProfile={newProfile ?? "(null)"}, activeProfile={ActiveProfile}, switching={!string.IsNullOrEmpty(newProfile) && newProfile != ActiveProfile}");
            // Load existing prefs first so we can preserve fields not sent by JS (e.g. ProfilePrefs with webhook URLs).
            GuiPrefs existingPrefs = LoadPrefs();
            GuiPrefs newPrefs = new GuiPrefs(
                OutputDir: root.TryGetProperty("outputDir", out JsonElement od) ? od.GetString() : null,
                ExportXml: root.TryGetProperty("exportXml", out JsonElement ex) ? ex.GetBoolean() : null,
                ExportJson: root.TryGetProperty("exportJson", out JsonElement ej) ? ej.GetBoolean() : null,
                ExportPdf: root.TryGetProperty("exportPdf", out JsonElement ep) ? ep.GetBoolean() : null,
                CustomFilenames: root.TryGetProperty("customFilenames", out JsonElement cf) ? cf.GetBoolean() : null,
                SeparateByNip: root.TryGetProperty("separateByNip", out JsonElement sn) ? sn.GetBoolean() : null,
                SelectedProfile: newProfile,
                LanPort: root.TryGetProperty("lanPort", out JsonElement lp) ? lp.GetInt32() : null,
                ListenOnAll: root.TryGetProperty("listenOnAll", out JsonElement loa) ? loa.GetBoolean() : null,
                DarkMode: root.TryGetProperty("darkMode", out JsonElement dm) ? dm.GetBoolean() : null,
                PreviewDarkMode: root.TryGetProperty("previewDarkMode", out JsonElement pdm) ? pdm.GetBoolean() : null,
                DetailsDarkMode: root.TryGetProperty("detailsDarkMode", out JsonElement ddm) ? ddm.GetBoolean() : null,
                PdfColorScheme: root.TryGetProperty("pdfColorScheme", out JsonElement pcs) ? pcs.GetString() : null,
                AutoRefreshMinutes: root.TryGetProperty("autoRefreshMinutes", out JsonElement arm) ? arm.GetInt32() : null,
                JsonConsoleLog: jsonConsoleLog,
                DisplayLimit: root.TryGetProperty("displayLimit", out JsonElement dl) ? dl.GetInt32() : null,
                SmtpHost: root.TryGetProperty("smtpHost", out JsonElement smh) ? smh.GetString() : null,
                SmtpPort: root.TryGetProperty("smtpPort", out JsonElement smpo) && smpo.TryGetInt32(out int smpoVal) ? smpoVal : (int?)null,
                SmtpSecurity: root.TryGetProperty("smtpSecurity", out JsonElement smse) ? smse.GetString() : null,
                SmtpUser: root.TryGetProperty("smtpUser", out JsonElement smui)
                    ? smui.GetString()
                    : null,
                // Preserve existing password if JS sends null/empty
                // (password field may be blank when re-opening prefs).
                SmtpPassword: root.TryGetProperty("smtpPassword", out JsonElement smpw)
                    && smpw.ValueKind != JsonValueKind.Null
                    && !string.IsNullOrEmpty(smpw.GetString())
                    ? smpw.GetString()
                    : existingPrefs.SmtpPassword,
                SmtpFrom: root.TryGetProperty("smtpFrom", out JsonElement smfr)
                    ? smfr.GetString()
                    : null,
                ShowIncomeChart: !root.TryGetProperty("showIncomeChart", out JsonElement sic) || sic.GetBoolean(),
                // Preserve ProfilePrefs from disk — JS savePrefs() never sends it,
                // so this prevents webhook URLs from being wiped on every prefs save.
                ProfilePrefs: existingPrefs.ProfilePrefs);
            SavePrefs(newPrefs);
            Log.LogInformation($"[prefs] Saved — autoRefresh={newPrefs.AutoRefreshMinutes ?? 0}min, darkMode={newPrefs.DarkMode ?? false}, smtpHost={newPrefs.SmtpHost ?? "(none)"}");
            Log.ConfigureLogging(Verbose, Quiet, jsonConsoleLog);
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
                    _lastSearchParams = null;
                    string switchedNip = _allProfiles.TryGetValue(ActiveProfile, out string? switchedNipVal) ? switchedNipVal : "?";
                    Log.LogInformation($"Profile switched to: {ActiveProfile} (NIP {switchedNip})");

                    // Load the new profile's cached invoices (if any)
                    try
                    {
                        (List<InvoiceSummary>? cached, SearchParams? cachedParams, _) = _invoiceCache.Load(GetProfileCacheKey());
                        if (cached != null && cached.Count > 0)
                        {
                            _cachedInvoices = cached;
                            _lastSearchParams = cachedParams;
                            Log.LogInformation($"[cache] profile-switch restore — {cached.Count} invoices loaded from DB for profile '{ActiveProfile}'");
                        }
                    }
                    catch (Exception cacheEx)
                    {
                        Log.LogWarning($"Failed to load invoice cache for new profile: {cacheEx.Message}");
                    }
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
        server.OnCachedInvoices = () =>
        {
            if (_cachedInvoices == null || _cachedInvoices.Count == 0)
            {
                return Task.FromResult<object>(new { invoices = Array.Empty<object>(), @params = (object?)null });
            }

            // Project SearchParams into camelCase anonymous object — SearchParams record
            // serializes as PascalCase by default, which JS cannot read with data.params.from etc.
            object? paramsObj = _lastSearchParams == null ? null : (object)new
            {
                subjectType = _lastSearchParams.SubjectType,
                from = _lastSearchParams.From,
                to = _lastSearchParams.To,
                dateType = _lastSearchParams.DateType,
            };
            return Task.FromResult<object>(new
            {
                invoices = MapInvoicesToJson(_cachedInvoices, _scope?.ServiceProvider.GetService<IVerificationLinkService>()),
                @params = paramsObj,
            });
        };
        server.OnTokenStatus = async () =>
        {
            try
            {
                TokenStore tokenStore = GetTokenStore();
                TokenStore.Key key = GetTokenStoreKey();
                TokenStore.Data? storedToken = tokenStore.GetToken(key);
                if (storedToken != null)
                {
                    // Auto-refresh if access token is expired/near-expiry but refresh token is valid
                    if (storedToken.Response.AccessToken.ValidUntil < DateTime.UtcNow.AddMinutes(1)
                        && storedToken.Response.RefreshToken.ValidUntil > DateTime.UtcNow.AddMinutes(1))
                    {
                        Log.LogInformation("GUI: token-status — access token expired, auto-refreshing");
                        AuthenticationOperationStatusResponse refreshed =
                            await TokenRefresh(_scope!, storedToken.Response.RefreshToken, CancellationToken.None).ConfigureAwait(false);
                        tokenStore.SetToken(key, new TokenStore.Data(refreshed));
                        storedToken = new TokenStore.Data(refreshed);
                        Log.LogInformation($"GUI: token-status auto-refresh OK — access token valid until {refreshed.AccessToken.ValidUntil:HH:mm:ss}");
                    }
                    return (object)new
                    {
                        accessTokenValidUntil = storedToken.Response.AccessToken.ValidUntil.ToLocalTime().ToString("o"),
                        refreshTokenValidUntil = storedToken.Response.RefreshToken.ValidUntil.ToLocalTime().ToString("o"),
                    };
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning($"GUI: token-status check failed: {ex.Message}");
            }
            return (object)new { accessTokenValidUntil = (string?)null, refreshTokenValidUntil = (string?)null };
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
            // Include GUI-only per-profile prefs (not stored in YAML)
            GuiPrefs loadedPrefs = LoadPrefs();
            Dictionary<string, ProfilePrefs> ppMap = loadedPrefs.ProfilePrefs ?? [];
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
                    CertPasswordFile: kvp.Value.Certificate?.Password_File,
                    IncludeInAutoRefresh: !ppMap.TryGetValue(kvp.Key, out ProfilePrefs? pp) || pp.IncludeInAutoRefresh != false,
                    SlackWebhookUrl: pp?.SlackWebhookUrl,
                    TeamsWebhookUrl: pp?.TeamsWebhookUrl,
                    NotificationEmail: pp?.NotificationEmail,
                    ExtendedNotifications: pp?.ExtendedNotifications ?? false,
                    AutoRefreshCurrentMonth: pp?.AutoRefreshCurrentMonth ?? true))
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
                Log.LogInformation($"Config saved: {configPath} ({profiles.Count} profile(s), active={data.ActiveProfile})");

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

                // Persist per-profile GUI prefs (includeInAutoRefresh) to gui-prefs.json (not YAML)
                GuiPrefs existingPrefs = LoadPrefs();
                Dictionary<string, ProfilePrefs> updatedPpMap = existingPrefs.ProfilePrefs
                    ?? new Dictionary<string, ProfilePrefs>();
                foreach (ProfileEditorData p in data.Profiles)
                {
                    // Bool? fields use null to represent the default value, keeping JSON compact.
                    // ExtendedNotifications default=false  → store true | null
                    // AutoRefreshCurrentMonth default=true → store null | false
                    updatedPpMap[p.Name] = new ProfilePrefs(
                        IncludeInAutoRefresh: p.IncludeInAutoRefresh,
                        SlackWebhookUrl: string.IsNullOrWhiteSpace(p.SlackWebhookUrl) ? null : p.SlackWebhookUrl,
                        TeamsWebhookUrl: string.IsNullOrWhiteSpace(p.TeamsWebhookUrl) ? null : p.TeamsWebhookUrl,
                        NotificationEmail: string.IsNullOrWhiteSpace(p.NotificationEmail) ? null : p.NotificationEmail,
                        ExtendedNotifications: p.ExtendedNotifications ? true : null,       // null = false (default)
                        AutoRefreshCurrentMonth: p.AutoRefreshCurrentMonth ? null : false);  // null = true  (default)
                }
                // Remove entries for profiles that no longer exist
                foreach (string stale in updatedPpMap.Keys.Except(data.Profiles.Select(p => p.Name)).ToList())
                {
                    updatedPpMap.Remove(stale);
                }
                SavePrefs(existingPrefs with { ProfilePrefs = updatedPpMap });

                // Clear setup mode — config now exists
                _setupRequired = false;

                return Task.FromResult("");
            }
            catch (Exception ex)
            {
                return Task.FromResult(ex.Message);
            }
        };

        server.OnAbout = () =>
        {
            System.Reflection.Assembly asm = System.Reflection.Assembly.GetExecutingAssembly();
            string version = System.Reflection.CustomAttributeExtensions
                .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(asm)
                .FirstOrDefault(a => a.Key == "Version")?.Value ?? "unknown";
            string buildDate = System.Reflection.CustomAttributeExtensions
                .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(asm)
                .FirstOrDefault(a => a.Key == "BuildDate")?.Value ?? "unknown";
            return Task.FromResult((object)new
            {
                version,
                buildDate,
                author = "Marcin Bojko",
                github = "https://github.com/marcinbojko/ksef-gui",
            });
        };

        server.OnTestNotification = async (body, ct) =>
        {
            string profileName;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(body);
                profileName = doc.RootElement.TryGetProperty("profileName", out JsonElement pn) ? pn.GetString() ?? ActiveProfile : ActiveProfile;
            }
            catch (JsonException) { profileName = ActiveProfile; }
            catch (ArgumentException) { profileName = ActiveProfile; }

            GuiPrefs prefs = LoadPrefs();
            ProfilePrefs? pp = prefs.ProfilePrefs?.GetValueOrDefault(profileName);
            string? slackUrl = pp?.SlackWebhookUrl;
            string? teamsUrl = pp?.TeamsWebhookUrl;
            string? emailTo = pp?.NotificationEmail;
            bool extended = pp?.ExtendedNotifications ?? false;
            IReadOnlyList<InvoiceSummary> fakeInvoices = MakeFakeInvoices();
            List<(string Name, Task<bool> Task)> tasks = [];
            if (!string.IsNullOrEmpty(slackUrl))
            {
                tasks.Add(("Slack", SendSlackNotificationAsync(slackUrl, profileName, fakeInvoices, extended, ct, throwOnHttpError: false)));
            }
            if (!string.IsNullOrEmpty(teamsUrl))
            {
                tasks.Add(("Teams", SendTeamsNotificationAsync(teamsUrl, profileName, fakeInvoices, extended, ct, throwOnHttpError: false)));
            }
            if (!string.IsNullOrEmpty(emailTo))
            {
                tasks.Add(("Email", SendEmailNotificationAsync(emailTo, profileName, fakeInvoices, extended, prefs, ct, throwOnError: false)));
            }
            if (tasks.Count == 0)
            {
                return "Brak skonfigurowanych kanałów powiadomień dla tego profilu.";
            }
            try
            {
                await Task.WhenAll(tasks.Select(t => (Task)t.Task)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (AggregateException) { /* individual task faults examined below */ }
            List<string> okChannels = tasks.Where(t => t.Task.IsCompletedSuccessfully && t.Task.Result).Select(t => t.Name).ToList();
            List<string> failedChannels = tasks.Where(t => t.Task.IsFaulted || (t.Task.IsCompletedSuccessfully && !t.Task.Result)).Select(t => t.Name).ToList();
            if (failedChannels.Count == 0)
            {
                return $"Wysłano test do {okChannels.Count} kanał(ów): {string.Join(", ", okChannels)}.";
            }
            if (okChannels.Count > 0)
            {
                return $"Wysłano do: {string.Join(", ", okChannels)}. Błąd w: {string.Join(", ", failedChannels)}.";
            }
            return $"Błąd wysyłki do: {string.Join(", ", failedChannels)}.";
        };

        server.OnTestEmail = async (body, ct) =>
        {
            string toEmail;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(body);
                toEmail = doc.RootElement.TryGetProperty("toEmail", out JsonElement te) ? te.GetString() ?? "" : "";
            }
            catch (JsonException) { toEmail = ""; }

            if (string.IsNullOrWhiteSpace(toEmail))
            {
                return "Podaj adres e-mail odbiorcy.";
            }

            GuiPrefs prefs = LoadPrefs();
            if (string.IsNullOrWhiteSpace(prefs.SmtpHost))
            {
                return "Brak skonfigurowanego serwera SMTP. Zapisz ustawienia i spróbuj ponownie.";
            }

            Log.LogInformation($"[test-email] Sending test to '{MaskEmail(toEmail)}' via {prefs.SmtpHost}:{prefs.SmtpPort ?? 587} ({prefs.SmtpSecurity ?? "StartTls"})...");
            bool extendedTest = prefs.ProfilePrefs?.GetValueOrDefault(ActiveProfile)?.ExtendedNotifications ?? false;
            await SendEmailNotificationAsync(toEmail, "test", MakeFakeInvoices(), extendedTest, prefs, ct, throwOnError: true).ConfigureAwait(false);
            return $"Testowy e-mail wysłany do: {toEmail}";
        };

        server.OnNotificationStatus = () =>
        {
            KsefCliConfig statusConfig;
            try { statusConfig = CurrentConfig; }
            catch (InvalidOperationException) { return Task.FromResult<object>(new { }); }
            Dictionary<string, object> result = [];
            foreach ((string profName, ProfileConfig profCfg) in statusConfig.Profiles)
            {
                string profKey = new TokenStore.Key(profName, profCfg).ToCacheKey();
                InvoiceCache.NotificationStatus status = _invoiceCache.LoadNotificationStatus(profKey);
                result[profName] = new
                {
                    lastSentAt = status.LastSentAt,
                    pendingRetries = status.PendingRetries,
                    channelsOk = status.LastChannelsOk,
                    channelsFailed = status.LastChannelsFailed,
                    hasErrors = status.PendingRetries > 0,
                };
            }
            return Task.FromResult<object>(result);
        };

        server.Start(cancellationToken);
        Log.LogInformation($"GUI running at {server.LocalUrl}");
        _ = Task.Run(() => RunBackgroundProfileRefreshAsync(cancellationToken), cancellationToken);
        if (Lan)
        {
            Log.LogInformation($"LAN access enabled — accessible on all network interfaces, port {server.Port}");
        }

        // Pre-populate invoice cache from SQLite so the browser table is filled on first open.
        if (!_setupRequired)
        {
            try
            {
                string profileKey = GetProfileCacheKey();
                (List<InvoiceSummary>? cached, SearchParams? cachedParams, DateTime? fetchedAt) = _invoiceCache.Load(profileKey);
                if (cached != null && cached.Count > 0)
                {
                    _cachedInvoices = cached;
                    _lastSearchParams = cachedParams;
                    Log.LogInformation($"[cache] startup restore — {cached.Count} invoices loaded from DB (fetched {fetchedAt:u})");
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning($"Failed to load invoice cache: {ex.Message}");
            }
        }

        server.OpenBrowser();

        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }

        return 0;
    }

    /// <summary>Parses <see cref="SearchParams"/> into <see cref="InvoiceQueryFilters"/>.</summary>
    private static async Task<InvoiceQueryFilters> BuildFiltersAsync(SearchParams sp, CancellationToken ct)
    {
        if (!Enum.TryParse(sp.SubjectType, true, out InvoiceSubjectType subjectType))
        {
            subjectType = sp.SubjectType?.ToLowerInvariant() switch
            {
                "1" or "sprzedawca" => InvoiceSubjectType.Subject1,
                "2" or "nabywca" => InvoiceSubjectType.Subject2,
                "3" => InvoiceSubjectType.Subject3,
                "4" => InvoiceSubjectType.SubjectAuthorized,
                _ => throw new FormatException($"Invalid SubjectType: {sp.SubjectType}")
            };
        }

        if (!Enum.TryParse(sp.DateType, true, out DateType dateType))
        {
            throw new InvalidEnumArgumentException($"Invalid DateType: {sp.DateType}");
        }

        DateTime parsedFromDate = await ParseDate.Parse(sp.From).ConfigureAwait(false);
        DateTime? parsedToDate = null;
        if (!string.IsNullOrEmpty(sp.To))
        {
            parsedToDate = await ParseDate.Parse(sp.To).ConfigureAwait(false);
        }

        return new InvoiceQueryFilters
        {
            SubjectType = subjectType,
            DateRange = new DateRange { From = parsedFromDate, To = parsedToDate, DateType = dateType },
        };
    }

    /// <summary>Pages through the KSeF API and returns all invoice summaries matching the filters.
    /// The KSeF API enforces a hard limit of 10 000 results per query. If this limit is reached
    /// (<c>IsTruncated == true</c>), the method returns partial results and sets
    /// <c>WasTruncated</c> to <see langword="true"/> so callers can warn the user.</summary>
    private static async Task<(List<InvoiceSummary> Invoices, bool WasTruncated)> FetchAllPagesAsync(
        IKSeFClient client,
        InvoiceQueryFilters filters,
        string accessToken,
        CancellationToken ct,
        bool abortOn429 = false)
    {
        List<InvoiceSummary> all = new();
        PagedInvoiceResponse pagedResponse;
        int currentPage = 0;        // page number (0-based) — pageOffset in the API is a page number, not a record offset
        const int pageSize = 100;
        const int maxRetries = 5;
        const int interPageDelayMs = 200;
        const int maxApiOffset = 10_000;  // KSeF hard limit: pageOffset * pageSize must be < 10 000

        do
        {
            // Guard: page 100 × pageSize 100 = 10 000 records — the API rejects this with 21405.
            if (currentPage * pageSize >= maxApiOffset)
            {
                Log.LogWarning($"Reached KSeF API offset limit (page {currentPage}, record {currentPage * pageSize}). Returning {all.Count} partial results.");
                return (all, true);
            }

            Log.LogInformation($"Fetching page {currentPage} (records {currentPage * pageSize}–{currentPage * pageSize + pageSize - 1}) of size {pageSize}");
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    pagedResponse = await client.QueryInvoiceMetadataAsync(
                        filters,
                        accessToken,
                        pageOffset: currentPage,
                        pageSize: pageSize,
                        cancellationToken: ct).ConfigureAwait(false);
                    break;
                }
                catch (KsefApiException ex) when (
                    (ex.ErrorResponse?.Exception?.ExceptionDetailList != null &&
                     ex.ErrorResponse.Exception.ExceptionDetailList.Any(d => d.ExceptionCode == 21405)) ||
                    ex.Message.Contains("21405", StringComparison.Ordinal))
                {
                    Log.LogWarning($"KSeF API rejected page offset (21405) at page {currentPage}. Returning {all.Count} partial results.");
                    return (all, true);
                }
                catch (KsefRateLimitException ex) when (attempt < maxRetries && !abortOn429)
                {
                    TimeSpan delay = ex.RecommendedDelay + TimeSpan.FromSeconds(attempt * 2);
                    Log.LogWarning($"Rate limited (HTTP 429). Retrying in {delay.TotalSeconds:F0}s... (attempt {attempt + 1}/{maxRetries})");
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
            }

            if (pagedResponse.Invoices != null)
            {
                all.AddRange(pagedResponse.Invoices);
            }

            if (pagedResponse.IsTruncated)
            {
                Log.LogWarning($"KSeF API returned IsTruncated=true at page {currentPage}. Total results exceed 10 000. Returning {all.Count} partial results.");
                return (all, true);
            }

            currentPage++;  // advance to next page number

            if (pagedResponse.HasMore == true)
            {
                await Task.Delay(interPageDelayMs, ct).ConfigureAwait(false);
            }
        } while (pagedResponse.HasMore == true);

        return (all, false);
    }

    private async Task<object> SearchAsync(SearchParams searchParams, CancellationToken ct)
    {
        ProfileConfig searchProfile = Config();
        bool isCyclic = searchParams.Source == "auto";
        Log.LogInformation(isCyclic
            ? $"[ksef-api] cyclic-search [profile={ActiveProfile}, nip={searchProfile.Nip}, env={searchProfile.Environment}, subject={searchParams.SubjectType}, from={searchParams.From}, to={searchParams.To ?? "–"}]"
            : $"[ksef-api] search [profile={ActiveProfile}, nip={searchProfile.Nip}, env={searchProfile.Environment}, subject={searchParams.SubjectType}, from={searchParams.From}, to={searchParams.To ?? "–"}]");

        InvoiceQueryFilters filters = await BuildFiltersAsync(searchParams, ct).ConfigureAwait(false);
        string accessToken = await GetAccessToken(_scope!, ct).ConfigureAwait(false);
        (List<InvoiceSummary> allInvoices, bool wasTruncated) = await FetchAllPagesAsync(_ksefClient!, filters, accessToken, ct).ConfigureAwait(false);

        Log.LogInformation(isCyclic
            ? $"[ksef-api] cyclic-search done — {allInvoices.Count} invoices{(wasTruncated ? " (TRUNCATED — KSeF 10K limit)" : "")}"
            : $"[ksef-api] search done — {allInvoices.Count} invoices{(wasTruncated ? " (TRUNCATED — KSeF 10K limit)" : "")}");
        _cachedInvoices = allInvoices;
        // Cyclic searches keep the last manual params intact — only the invoice list is refreshed.
        if (!isCyclic)
        {
            _lastSearchParams = searchParams with { Source = null };
        }

        try
        {
            string profileKey = GetProfileCacheKey();
            if (isCyclic)
            {
                _invoiceCache.SaveInvoicesOnly(profileKey, allInvoices);
            }
            else
            {
                _invoiceCache.Save(profileKey, _lastSearchParams!, allInvoices);
            }
        }
        catch (Exception ex)
        {
            Log.LogWarning($"Failed to save invoice cache: {ex.Message}");
        }

        return new
        {
            invoices = MapInvoicesToJson(allInvoices, _scope?.ServiceProvider.GetService<IVerificationLinkService>()),
            truncated = wasTruncated,
        };
    }

    private string GetProfileCacheKey()
    {
        try
        {
            return GetTokenStoreKey().ToCacheKey();
        }
        catch
        {
            return "default";
        }
    }

    /// <summary>
    /// Background task — every 30 s checks whether the auto-refresh interval has elapsed,
    /// then refreshes all non-active profiles marked IncludeInAutoRefresh in gui-prefs.
    /// Runs for the lifetime of the app (cancelled via <paramref name="ct"/>).
    /// </summary>
    private async Task RunBackgroundProfileRefreshAsync(CancellationToken ct)
    {
        DateTime lastRun = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }

            GuiPrefs prefs = LoadPrefs();
            int minutes = prefs.AutoRefreshMinutes ?? 0;
            if (minutes < 10)
            {
                continue;
            }

            if ((DateTime.UtcNow - lastRun).TotalMinutes < minutes)
            {
                continue;
            }
            lastRun = DateTime.UtcNow;

            KsefCliConfig config;
            try { config = CurrentConfig; }
            catch { continue; }

            Dictionary<string, ProfilePrefs> ppMap = prefs.ProfilePrefs ?? [];
            string snapshotActive = ActiveProfile; // snapshot to catch mid-loop changes
            Log.LogDebug($"[bg-refresh] Cycle start — activeProfile='{snapshotActive}', profiles=[{string.Join(", ", config.Profiles.Keys)}]");
            foreach ((string name, ProfileConfig profile) in config.Profiles)
            {
                ProfilePrefs? profilePrefs = ppMap.TryGetValue(name, out ProfilePrefs? pp) ? pp : null;
                if (profilePrefs?.IncludeInAutoRefresh == false)
                {
                    Log.LogDebug($"[bg-refresh] Skipping '{name}' (includeInAutoRefresh=false)");
                    continue;
                }

                // Active profile: C# still runs (for webhooks/DB), but JS silentRefresh handles
                // the UI update — suppress the SSE event so the browser doesn't double-notify.
                bool isActiveProfile = name == snapshotActive;

                try
                {
                    await RefreshProfileInBackgroundAsync(name, profile, profilePrefs, prefs, isActiveProfile, ct).ConfigureAwait(false);
                }
                catch (KsefRateLimitException ex)
                {
                    Log.LogWarning($"[bg-refresh] Profile '{name}': rate-limited (retry-after {ex.RecommendedDelay.TotalSeconds:F0}s) — skipping this cycle");
                }
                catch (Exception ex)
                {
                    Log.LogWarning($"[bg-refresh] Profile '{name}': {ex.Message}");
                }
            }
        }
    }

    private async Task RefreshProfileInBackgroundAsync(string name, ProfileConfig profile, ProfilePrefs? profilePrefs, GuiPrefs prefs, bool isActiveProfile, CancellationToken ct)
    {
        Log.LogInformation($"[bg-refresh] Profile '{name}' (NIP {profile.Nip}, env {profile.Environment}) starting{(isActiveProfile ? " [active profile]" : "")}");
        using IServiceScope scope = GetScope(profile);
        string accessToken = await GetAccessTokenForProfile(name, profile, scope, ct).ConfigureAwait(false);

        string profileKey = new TokenStore.Key(name, profile).ToCacheKey();
        (List<InvoiceSummary>? prev, SearchParams? cachedParams, _) = _invoiceCache.Load(profileKey);
        HashSet<string> prevKeys = prev?.Select(i => i.KsefNumber).ToHashSet() ?? [];

        // Use last manual search params for this profile, or default to "this month".
        // Always clear To: auto-refresh must search up to now(), not a stale GUI date.
        // Optionally pin From to the start of the current month regardless of GUI setting.
        bool autoRefreshCurrentMonth = profilePrefs?.AutoRefreshCurrentMonth ?? true;
        SearchParams baseParams = cachedParams ?? new SearchParams("Subject2", "thismonth", null, "Issue");
        SearchParams sp = baseParams with
        {
            From = autoRefreshCurrentMonth ? "thismonth" : (!string.IsNullOrWhiteSpace(baseParams.From) ? baseParams.From : "thismonth"),
            To = null,
        };
        InvoiceQueryFilters filters = await BuildFiltersAsync(sp, ct).ConfigureAwait(false);

        IKSeFClient client = scope.ServiceProvider.GetRequiredService<IKSeFClient>();
        List<InvoiceSummary> invoices;
        bool wasTruncated;
        try
        {
            (invoices, wasTruncated) = await FetchAllPagesAsync(client, filters, accessToken, ct, abortOn429: true).ConfigureAwait(false);
            // Suggestion 1: invalidate the stored access token after every successful fetch.
            // KSeF revokes access tokens as soon as the search session completes, so ValidUntil
            // (~2 h) is misleading. Expiring it here ensures the next cycle does a TokenRefresh
            // instead of reusing a server-revoked token and cascading into three 401s.
            ExpireStoredAccessToken(name, profile);
        }
        catch (KsefApiException ex401) when (ex401.StatusCode == HttpStatusCode.Unauthorized)
        {
            // Suggestion 2: server revoked the token mid-fetch — expire it and retry once.
            Log.LogWarning($"[bg-refresh] '{name}': HTTP 401 mid-fetch — forcing token refresh and retrying once");
            ExpireStoredAccessToken(name, profile);
            // GetAccessTokenForProfile sees the expired access token and performs TokenRefresh.
            string freshToken = await GetAccessTokenForProfile(name, profile, scope, ct).ConfigureAwait(false);
            (invoices, wasTruncated) = await FetchAllPagesAsync(client, filters, freshToken, ct, abortOn429: true).ConfigureAwait(false);
            ExpireStoredAccessToken(name, profile);
        }

        if (wasTruncated)
        {
            Log.LogWarning($"[bg-refresh] Profile '{name}': results truncated at KSeF 10K limit — narrowing the date range may help");
        }

        if (prev == null)
        {
            _invoiceCache.Save(profileKey, sp, invoices);
            // First ever run for this profile: seed the notification baseline so we don't
            // blast the user with the entire invoice history on the first refresh cycle.
            _invoiceCache.MarkAsNotified(profileKey, invoices.Select(i => i.KsefNumber));
        }
        else
        {
            _invoiceCache.SaveInvoicesOnly(profileKey, invoices);
        }

        int newCount = invoices.Count(i => !prevKeys.Contains(i.KsefNumber));
        Log.LogInformation($"[bg-refresh] Profile '{name}': {invoices.Count} invoices, {newCount} new{(wasTruncated ? " (TRUNCATED)" : "")}");

        // For the active profile, update the in-memory cache so the browser sees fresh data
        // via /cached-invoices without needing a separate /search round-trip.
        if (isActiveProfile)
        {
            _cachedInvoices = invoices;
            _lastSearchParams = sp;
        }

        if (_server != null)
        {
            await _server.SendEventAsync("background_refresh", new
            {
                profileName = name,
                count = invoices.Count,
                newCount,
                truncated = wasTruncated,
            }).ConfigureAwait(false);
        }

        // Retry previously failed notification channels before dispatching new notifications.
        await DispatchPendingRetriesAsync(profileKey, invoices, name, profilePrefs, prefs, ct).ConfigureAwait(false);

        // Webhooks fire only for invoices that are both new (not in prev cache) and not yet
        // notified (not in notification_sent table). This prevents duplicate webhook calls
        // across refresh cycles and across app restarts.
        if (newCount > 0 && prev != null)
        {
            HashSet<string> alreadyNotified = _invoiceCache.LoadNotifiedKsefNumbers(profileKey);
            List<InvoiceSummary> toNotify = invoices
                .Where(i => !prevKeys.Contains(i.KsefNumber) && !alreadyNotified.Contains(i.KsefNumber))
                .ToList();

            if (toNotify.Count > 0)
            {
                // Deliberate at-most-once ordering: mark as notified BEFORE sending webhooks.
                // If a webhook call fails, the invoice is still marked and will not be retried
                // immediately — instead DispatchPendingRetriesAsync will retry on the next cycle
                // using exponential backoff (5, 10, 20 min, up to 3 attempts).
                _invoiceCache.MarkAsNotified(profileKey, toNotify.Select(i => i.KsefNumber));
                string? slackUrl = profilePrefs?.SlackWebhookUrl;
                string? teamsUrl = profilePrefs?.TeamsWebhookUrl;
                string? emailTo = profilePrefs?.NotificationEmail;
                bool extended = profilePrefs?.ExtendedNotifications ?? false;
                Log.LogDebug($"[bg-refresh] '{name}' channels: " +
                    $"Slack={(!string.IsNullOrEmpty(slackUrl) ? "configured" : "none")} " +
                    $"Teams={(!string.IsNullOrEmpty(teamsUrl) ? "configured" : "none")} " +
                    $"Email={(!string.IsNullOrEmpty(emailTo) ? MaskEmail(emailTo) : "none")}");
                List<(string Name, Task<bool> Task)> webhookTasks = [];
                if (!string.IsNullOrEmpty(slackUrl))
                {
                    webhookTasks.Add(("Slack", SendSlackNotificationAsync(slackUrl, name, toNotify, extended, ct)));
                }
                if (!string.IsNullOrEmpty(teamsUrl))
                {
                    webhookTasks.Add(("Teams", SendTeamsNotificationAsync(teamsUrl, name, toNotify, extended, ct)));
                }
                if (!string.IsNullOrEmpty(emailTo))
                {
                    webhookTasks.Add(("Email", SendEmailNotificationAsync(emailTo, name, toNotify, extended, prefs, ct)));
                }
                if (webhookTasks.Count > 0)
                {
                    await Task.WhenAll(webhookTasks.Select(t => (Task)t.Task)).ConfigureAwait(false);
                    List<string> channelsOk = webhookTasks.Where(t => t.Task.IsCompletedSuccessfully && t.Task.Result).Select(t => t.Name).ToList();
                    List<string> channelsFailed = webhookTasks.Where(t => !t.Task.IsCompletedSuccessfully || !t.Task.Result).Select(t => t.Name).ToList();
                    _invoiceCache.UpdateNotifiedChannels(profileKey, toNotify.Select(i => i.KsefNumber), channelsOk, channelsFailed);
                    if (channelsFailed.Count > 0)
                    {
                        Log.LogWarning($"[bg-refresh] '{name}': {channelsFailed.Count} channel(s) failed — [{string.Join(", ", channelsFailed)}] — will retry in 5 min");
                    }
                    if (_server != null)
                    {
                        await _server.SendEventAsync("notification_outcome", new
                        {
                            profileName = name,
                            invoiceCount = toNotify.Count,
                            channelsOk,
                            channelsFailed,
                            pendingRetries = channelsFailed.Count,
                        }).ConfigureAwait(false);
                    }
                }
                else
                {
                    Log.LogWarning($"[bg-refresh] Profile '{name}': {toNotify.Count} new invoice(s) detected but no notification channels configured (Slack, Teams, e-mail).");
                }
            }
        }
    }

    /// <summary>
    /// Retries delivery to channels that failed in a previous cycle. Matches pending ksef_numbers
    /// against the current invoice cache, sends per-channel, then updates retry state with
    /// exponential backoff (5→10→20 min, max 3 attempts total).
    /// </summary>
    private async Task DispatchPendingRetriesAsync(
        string profileKey, List<InvoiceSummary> invoices,
        string name, ProfilePrefs? profilePrefs, GuiPrefs prefs, CancellationToken ct)
    {
        List<InvoiceCache.PendingRetry> pending = _invoiceCache.LoadPendingRetries(profileKey);
        if (pending.Count == 0) { return; }

        IEnumerable<string> allFailedChannels = pending.SelectMany(p => p.ChannelsFailed).Distinct();
        Log.LogInformation($"[bg-retry] '{name}': {pending.Count} invoice(s) with pending retries — channels: [{string.Join(", ", allFailedChannels)}]");

        Dictionary<string, InvoiceSummary> invoiceMap = invoices.ToDictionary(i => i.KsefNumber);
        bool extended = profilePrefs?.ExtendedNotifications ?? false;
        string? slackUrl = profilePrefs?.SlackWebhookUrl;
        string? teamsUrl = profilePrefs?.TeamsWebhookUrl;
        string? emailTo = profilePrefs?.NotificationEmail;
        string slackHost = Uri.TryCreate(slackUrl, UriKind.Absolute, out Uri? slackUri)
            ? slackUri.Host : slackUrl ?? "";
        string teamsHost = Uri.TryCreate(teamsUrl, UriKind.Absolute, out Uri? teamsUri)
            ? teamsUri.Host : teamsUrl ?? "";

        // Build per-channel invoice lists from the current cache
        Dictionary<string, List<InvoiceSummary>> retryInvoicesByChannel = [];
        foreach (InvoiceCache.PendingRetry retry in pending)
        {
            foreach (string ch in retry.ChannelsFailed)
            {
                if (!retryInvoicesByChannel.ContainsKey(ch)) { retryInvoicesByChannel[ch] = []; }
                if (invoiceMap.TryGetValue(retry.KsefNumber, out InvoiceSummary? inv))
                {
                    retryInvoicesByChannel[ch].Add(inv);
                }
            }
        }

        // Track which channels were actually dispatched (vs. skipped due to missing URL/data)
        List<(string Channel, Task<bool> Task)> retryTasks = [];
        foreach ((string ch, List<InvoiceSummary> chInvoices) in retryInvoicesByChannel)
        {
            if (chInvoices.Count == 0)
            {
                Log.LogWarning($"[bg-retry] '{name}' {ch}: {pending.Count(p => p.ChannelsFailed.Contains(ch))} pending invoice(s) no longer in cache — will retry next cycle");
                continue;
            }
            switch (ch)
            {
                case "Slack" when !string.IsNullOrEmpty(slackUrl):
                    Log.LogInformation($"[bg-retry] '{name}' Slack: retrying {chInvoices.Count} invoice(s) (host={slackHost})");
                    retryTasks.Add(("Slack", SendSlackNotificationAsync(slackUrl, name, chInvoices, extended, ct)));
                    break;
                case "Teams" when !string.IsNullOrEmpty(teamsUrl):
                    Log.LogInformation($"[bg-retry] '{name}' Teams: retrying {chInvoices.Count} invoice(s) (host={teamsHost})");
                    retryTasks.Add(("Teams", SendTeamsNotificationAsync(teamsUrl, name, chInvoices, extended, ct)));
                    break;
                case "Email" when !string.IsNullOrEmpty(emailTo):
                    Log.LogInformation($"[bg-retry] '{name}' Email: retrying {chInvoices.Count} invoice(s) to {MaskEmail(emailTo)}");
                    retryTasks.Add(("Email", SendEmailNotificationAsync(emailTo, name, chInvoices, extended, prefs, ct)));
                    break;
                default:
                    Log.LogWarning($"[bg-retry] '{name}' {ch}: has pending retries but channel URL is no longer configured");
                    break;
            }
        }

        if (retryTasks.Count == 0) { return; }

        await Task.WhenAll(retryTasks.Select(t => (Task)t.Task))
            .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        HashSet<string> succeeded = retryTasks
            .Where(t => t.Task.IsCompletedSuccessfully && t.Task.Result)
            .Select(t => t.Channel).ToHashSet();
        HashSet<string> stillFailed = retryTasks
            .Where(t => !t.Task.IsCompletedSuccessfully || !t.Task.Result)
            .Select(t => t.Channel).ToHashSet();
        HashSet<string> dispatched = retryTasks.Select(t => t.Channel).ToHashSet();

        Log.LogInformation($"[bg-retry] '{name}' outcomes: ok=[{string.Join(",", succeeded)}] still-failed=[{string.Join(",", stillFailed)}]");

        foreach (InvoiceCache.PendingRetry retry in pending)
        {
            // Merge newly-succeeded channels into the ok list
            List<string> nowOk = [
                .. (retry.ChannelsOk?.Split(',').Where(s => !string.IsNullOrEmpty(s)) ?? []),
                .. retry.ChannelsFailed.Where(succeeded.Contains),
            ];
            // Channels still failing + channels that were skipped (no data/URL) stay in failed
            List<string> remaining = retry.ChannelsFailed
                .Where(ch => stillFailed.Contains(ch) || !dispatched.Contains(ch))
                .ToList();
            bool anyAttempted = retry.ChannelsFailed.Any(dispatched.Contains);
            int newRetryCount = anyAttempted ? retry.RetryCount + 1 : retry.RetryCount;
            _invoiceCache.UpdateRetryOutcome(profileKey, [retry.KsefNumber], nowOk, remaining, newRetryCount);
        }

        if (_server != null)
        {
            await _server.SendEventAsync("notification_outcome", new
            {
                profileName = name,
                isRetry = true,
                channelsOk = succeeded.ToList(),
                channelsFailed = stillFailed.ToList(),
            }).ConfigureAwait(false);
        }
    }

    private static async Task<bool> SendSlackNotificationAsync(
        string webhookUrl, string profileName, IReadOnlyList<InvoiceSummary> invoices, bool extended,
        CancellationToken ct, bool throwOnHttpError = false)
    {
        int count = invoices.Count;
        object payload;
        if (extended && count > 0)
        {
            // Block Kit — up to 5 invoice rows, then context footer
            const int maxRows = 5;
            List<object> blocks = new()
            {
                new
                {
                    type = "header",
                    text = new { type = "plain_text", text = $"KSeF: {count} nowych faktur — {profileName}", emoji = true },
                },
                new { type = "divider" },
            };
            foreach (InvoiceSummary inv in invoices.Take(maxRows))
            {
                string sellerNip = EscapeSlackMarkdown(inv.Seller?.Nip ?? "—");
                string sellerName = EscapeSlackMarkdown(inv.Seller?.Name ?? "—");
                blocks.Add(new
                {
                    type = "section",
                    fields = new[]
                    {
                        new { type = "mrkdwn", text = $"*Data:* {inv.IssueDate:yyyy-MM-dd}" },
                        new { type = "mrkdwn", text = $"*NIP:* {sellerNip}" },
                        new { type = "mrkdwn", text = $"*Sprzedawca:* {sellerName}" },
                    },
                });
                blocks.Add(new { type = "divider" });
            }
            if (count > maxRows)
            {
                blocks.Add(new
                {
                    type = "context",
                    elements = new[] { new { type = "mrkdwn", text = $"_… i {count - maxRows} więcej_" } },
                });
            }
            payload = new { blocks };
        }
        else
        {
            string escapedProfile = EscapeSlackMarkdown(profileName);
            string text = $"KSeF: {count} nowych faktur dla profilu *{escapedProfile}*";
            payload = new { text };
        }

        string json = JsonSerializer.Serialize(payload);
        using StringContent content = new StringContent(json, Encoding.UTF8, "application/json");
        string webhookHost = Uri.TryCreate(webhookUrl, UriKind.Absolute, out Uri? swUri)
            ? swUri.Host : webhookUrl;
        Log.LogDebug($"[slack-notify] POST to '{webhookHost}' for profile '{profileName}': {count} invoice(s)");
        try
        {
            using HttpResponseMessage resp = await _httpClient.PostAsync(webhookUrl, content, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (throwOnHttpError)
                {
                    throw new HttpRequestException($"HTTP {(int)resp.StatusCode}: {body.Trim()}", null, resp.StatusCode);
                }
                Log.LogWarning($"[slack-notify] HTTP {(int)resp.StatusCode} for profile '{profileName}' (host={webhookHost}): {body.Trim()}");
                return false;
            }
            Log.LogInformation($"[slack-notify] Delivered to '{webhookHost}' for profile '{profileName}': {count} invoice(s)");
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException ex) when (!throwOnHttpError)
        {
            Log.LogWarning($"[slack-notify] Timeout ({_httpClient.Timeout.TotalSeconds:F0}s) for profile '{profileName}' (host={webhookHost}): {ex.Message}");
            return false;
        }
        catch (HttpRequestException ex) when (!throwOnHttpError)
        {
            string netDetail = ex.InnerException is SocketException se
                ? $"SocketError={se.SocketErrorCode} — {se.Message}"
                : ex.Message;
            Log.LogWarning($"[slack-notify] Network error for profile '{profileName}' (host={webhookHost}): {netDetail}");
            return false;
        }
    }

    private static async Task<bool> SendTeamsNotificationAsync(
        string webhookUrl, string profileName, IReadOnlyList<InvoiceSummary> invoices, bool extended,
        CancellationToken ct, bool throwOnHttpError = false)
    {
        int count = invoices.Count;
        object payload;
        if (extended && count > 0)
        {
            const int maxRows = 8;
            List<object> facts = invoices.Take(maxRows).Select(inv =>
            {
                string sellerNip = EscapeForTeamsMarkdown(inv.Seller?.Nip ?? "—");
                string sellerName = EscapeForTeamsMarkdown(inv.Seller?.Name ?? "—");
                return (object)new
                {
                    name = $"{inv.IssueDate:yyyy-MM-dd} · {sellerNip}",
                    value = sellerName,
                };
            }).ToList();
            if (count > maxRows)
            {
                facts.Add(new { name = "…", value = $"i {count - maxRows} więcej" });
            }
            payload = new
            {
                @type = "MessageCard",
                @context = "https://schema.org/extensions",
                summary = $"KSeF: {count} nowych faktur",
                themeColor = "1A6FC4",
                title = $"KSeF: Nowe faktury — {profileName}",
                sections = new[]
                {
                    new
                    {
                        activityTitle = $"Znaleziono **{count}** nowych faktur",
                        facts,
                    },
                },
            };
        }
        else
        {
            payload = new
            {
                @type = "MessageCard",
                @context = "https://schema.org/extensions",
                summary = $"KSeF: {count} nowych faktur",
                themeColor = "1A6FC4",
                title = "KSeF: Nowe faktury",
                text = $"Profil **{profileName}**: {count} nowych faktur",
            };
        }

        string json = JsonSerializer.Serialize(payload);
        using StringContent content = new StringContent(json, Encoding.UTF8, "application/json");
        string webhookHost = Uri.TryCreate(webhookUrl, UriKind.Absolute, out Uri? twUri)
            ? twUri.Host : webhookUrl;
        Log.LogDebug($"[teams-notify] POST to '{webhookHost}' for profile '{profileName}': {count} invoice(s)");
        try
        {
            using HttpResponseMessage resp = await _httpClient.PostAsync(webhookUrl, content, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (throwOnHttpError)
                {
                    throw new HttpRequestException($"HTTP {(int)resp.StatusCode}: {body.Trim()}", null, resp.StatusCode);
                }
                Log.LogWarning($"[teams-notify] HTTP {(int)resp.StatusCode} for profile '{profileName}' (host={webhookHost}): {body.Trim()}");
                return false;
            }
            Log.LogInformation($"[teams-notify] Delivered to '{webhookHost}' for profile '{profileName}': {count} invoice(s)");
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException ex) when (!throwOnHttpError)
        {
            Log.LogWarning($"[teams-notify] Timeout ({_httpClient.Timeout.TotalSeconds:F0}s) for profile '{profileName}' (host={webhookHost}): {ex.Message}");
            return false;
        }
        catch (HttpRequestException ex) when (!throwOnHttpError)
        {
            string netDetail = ex.InnerException is SocketException se
                ? $"SocketError={se.SocketErrorCode} — {se.Message}"
                : ex.Message;
            Log.LogWarning($"[teams-notify] Network error for profile '{profileName}' (host={webhookHost}): {netDetail}");
            return false;
        }
    }

    /// <summary>Escapes Teams MessageCard Markdown characters to prevent accidental formatting.</summary>
    private static string EscapeForTeamsMarkdown(string text) =>
        text.Replace("\\", "\\\\").Replace("*", "\\*").Replace("_", "\\_")
            .Replace("`", "\\`").Replace("~", "\\~")
            .Replace("[", "\\[").Replace("]", "\\]")
            .Replace("(", "\\(").Replace(")", "\\)");

    /// <summary>Escapes Slack mrkdwn special characters to prevent accidental formatting.</summary>
    private static string EscapeSlackMarkdown(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
            .Replace("*", "\\*").Replace("_", "\\_").Replace("~", "\\~")
            .Replace("`", "\\`");

    /// <summary>Masks the local part of an e-mail address for safe logging (e.g. "j***e@example.com").</summary>
    private static string MaskEmail(string email)
    {
        int at = email.IndexOf('@');
        if (at <= 0) { return "***"; }
        string local = email[..at];
        string domain = email[at..];
        return local.Length <= 2
            ? new string('*', local.Length) + domain
            : local[0] + new string('*', local.Length - 2) + local[^1] + domain;
    }

    private async Task<bool> SendEmailNotificationAsync(
        string toEmail, string profileName, IReadOnlyList<InvoiceSummary> invoices, bool extended,
        GuiPrefs prefs, CancellationToken ct, bool throwOnError = false)
    {
        string host = prefs.SmtpHost ?? "";
        if (string.IsNullOrWhiteSpace(host))
        {
            if (throwOnError) { throw new InvalidOperationException("Brak skonfigurowanego serwera SMTP."); }
            Log.LogWarning($"[email-notify] SMTP host not configured, skipping for profile '{profileName}'");
            return false;
        }
        int port = prefs.SmtpPort ?? 587;

        // System.Net.Mail.SmtpClient only supports STARTTLS (explicit TLS, typically port 587).
        // It does NOT support implicit SSL/SMTPS (port 465). Fail fast with a clear message.
        if (string.Equals(prefs.SmtpSecurity, "Ssl", StringComparison.OrdinalIgnoreCase))
        {
            const string implicitSslMsg =
                "System.Net.Mail nie obsługuje implicit SSL (port 465 / SMTPS). " +
                "Zmień protokół na StartTls i port na 587, lub zainstaluj MailKit jako zamiennik.";
            if (throwOnError) { throw new NotSupportedException(implicitSslMsg); }
            Log.LogWarning($"[email-notify] Implicit SSL not supported — skipping for profile '{profileName}'. {implicitSslMsg}");
            return false;
        }

        // SmtpClient.Timeout only covers socket read/write, not the TCP connect phase.
        // Use a linked CancellationToken to enforce a hard wall-clock deadline for the entire call.
        int timeoutMs = throwOnError ? 15_000 : 20_000;
        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);
        CancellationToken deadline = timeoutCts.Token;

        // Phase 1: TCP pre-check (5 s sub-deadline) — distinguishes DNS/TCP from SMTP protocol errors.
        Log.LogDebug($"[email-notify] Phase 1: TCP connect {host}:{port}…");
        using CancellationTokenSource tcpCts = CancellationTokenSource.CreateLinkedTokenSource(deadline);
        tcpCts.CancelAfter(5_000);
        try
        {
            using TcpClient tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, tcpCts.Token).ConfigureAwait(false);
            Log.LogDebug($"[email-notify] Phase 1: TCP OK ({host}:{port}) — proceeding to SMTP handshake");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            string msg = $"Przekroczono limit czasu TCP dla {host}:{port} (5 s) — sprawdź adres serwera i port.";
            if (throwOnError) { throw new TimeoutException(msg); }
            Log.LogWarning($"[email-notify] TCP timeout for profile '{profileName}': {msg}");
            return false;
        }
        catch (SocketException ex)
        {
            string msg = $"Nie można nawiązać połączenia TCP z {host}:{port} — {ex.Message} (SocketError: {ex.SocketErrorCode})";
            if (throwOnError) { throw new InvalidOperationException(msg, ex); }
            Log.LogWarning($"[email-notify] TCP error for profile '{profileName}': {msg}");
            return false;
        }

        // Phase 2: SMTP handshake (TLS / EHLO / AUTH / DATA).
        Log.LogDebug($"[email-notify] Phase 2: SMTP handshake {prefs.SmtpSecurity ?? "StartTls"} user={prefs.SmtpUser ?? "(none)"}…");
        try
        {
            using SmtpClient smtp = new SmtpClient(host, port);
            smtp.EnableSsl = prefs.SmtpSecurity != "None";
            smtp.DeliveryMethod = SmtpDeliveryMethod.Network;
            smtp.Timeout = timeoutMs;
            if (!string.IsNullOrEmpty(prefs.SmtpUser))
            {
                smtp.Credentials = new NetworkCredential(prefs.SmtpUser, prefs.SmtpPassword ?? "");
            }
            string from = !string.IsNullOrWhiteSpace(prefs.SmtpFrom) ? prefs.SmtpFrom! : prefs.SmtpUser ?? "ksefcli@localhost";
            int count = invoices.Count;
            string subject = $"KSeF: {count} nowych faktur dla profilu {profileName}";
            string body = BuildEmailBody(profileName, invoices, extended);
            using MailMessage msg = new MailMessage(from, toEmail, subject, body)
            {
                IsBodyHtml = true,
            };
            await smtp.SendMailAsync(msg, deadline).ConfigureAwait(false);
            Log.LogInformation($"[email-notify] Sent to '{MaskEmail(toEmail)}' for profile '{profileName}': {count} invoice(s)");
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Deadline fired during SMTP handshake (TLS/EHLO/AUTH/DATA), not TCP — TCP already passed.
            string msg = $"Przekroczono limit czasu podczas handshake SMTP z {host}:{port} ({timeoutMs / 1000} s) — sprawdź ustawienia TLS i dane logowania.";
            if (throwOnError) { throw new TimeoutException(msg); }
            Log.LogWarning($"[email-notify] SMTP timeout for profile '{profileName}': {msg}");
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Include inner exception detail (e.g. SmtpException status code, auth failure reason)
            string inner = ex.InnerException is not null ? $" (inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message})" : "";
            string msg = $"Błąd SMTP ({ex.GetType().Name}): {ex.Message}{inner}";
            if (throwOnError) { throw new InvalidOperationException(msg, ex); }
            Log.LogWarning($"[email-notify] SMTP error for profile '{profileName}' ({host}:{port}): {ex.GetType().Name}: {ex.Message}{inner}");
            return false;
        }
    }

    private static IReadOnlyList<InvoiceSummary> MakeFakeInvoices() =>
    [
        new InvoiceSummary
        {
            KsefNumber = "1234567890-20240101-ABCDEF-01",
            InvoiceNumber = "FV/2024/001",
            IssueDate = DateTimeOffset.UtcNow.AddDays(-2),
            Seller = new KSeF.Client.Core.Models.Invoices.Seller { Nip = "1234567890", Name = "Przykładowa Firma Sp. z o.o." },
            Buyer = new KSeF.Client.Core.Models.Invoices.Buyer { Name = "Odbiorca Testowy Sp. z o.o." },
            GrossAmount = 1230.00m, NetAmount = 1000.00m, VatAmount = 230.00m, Currency = "PLN",
        },
        new InvoiceSummary
        {
            KsefNumber = "0987654321-20240101-FEDCBA-02",
            InvoiceNumber = "FV/2024/002",
            IssueDate = DateTimeOffset.UtcNow.AddDays(-1),
            Seller = new KSeF.Client.Core.Models.Invoices.Seller { Nip = "9876543210", Name = "Dostawca Usług S.A." },
            Buyer = new KSeF.Client.Core.Models.Invoices.Buyer { Name = "Odbiorca Testowy Sp. z o.o." },
            GrossAmount = 3690.00m, NetAmount = 3000.00m, VatAmount = 690.00m, Currency = "PLN",
        },
        new InvoiceSummary
        {
            KsefNumber = "1111222233-20240101-AABBCC-03",
            InvoiceNumber = "FV/2024/003",
            IssueDate = DateTimeOffset.UtcNow,
            Seller = new KSeF.Client.Core.Models.Invoices.Seller { Nip = "1111222233", Name = "Kowalski i Wspólnicy" },
            Buyer = new KSeF.Client.Core.Models.Invoices.Buyer { Name = "Odbiorca Testowy Sp. z o.o." },
            GrossAmount = 615.00m, NetAmount = 500.00m, VatAmount = 115.00m, Currency = "PLN",
        },
    ];

    private static string BuildEmailBody(string profileName, IReadOnlyList<InvoiceSummary> invoices, bool extended)
    {
        int count = invoices.Count;
        const int maxRows = 10;

        StringBuilder sb = new StringBuilder();
        sb.Append("""
            <!DOCTYPE html>
            <html lang="pl"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            </head>
            <body style="margin:0;padding:0;background:#f4f4f5;font-family:Arial,Helvetica,sans-serif;color:#333">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background:#f4f4f5">
            <tr><td style="padding:24px 16px">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0"
                   style="max-width:640px;margin:0 auto;background:#fff;border-radius:6px;
                          box-shadow:0 1px 4px rgba(0,0,0,.12)">
              <!-- Header -->
              <tr><td style="background:#1a6fc4;color:#fff;padding:20px 28px;border-radius:6px 6px 0 0">
                <span style="font-size:20px;font-weight:700;letter-spacing:-.3px">KSeF — Nowe faktury</span>
                <span style="float:right;font-size:13px;opacity:.85;margin-top:4px">
            """);
        sb.Append($"Profil: {WebUtility.HtmlEncode(profileName)}");
        sb.Append("""
                </span>
              </td></tr>
              <!-- Body -->
              <tr><td style="padding:24px 28px">
            """);
        sb.Append($"<p style=\"margin:0 0 16px\">Znaleziono <strong>{count}</strong> nowych faktur.");
        if (extended && count > 0)
        {
            sb.Append("""
                </p>
                <table role="presentation" width="100%" cellpadding="0" cellspacing="0"
                       style="border-collapse:collapse;font-size:13px">
                  <thead>
                    <tr style="background:#f0f4fa">
                      <th style="border:1px solid #dde3ec;padding:8px 10px;text-align:left;white-space:nowrap">Data</th>
                      <th style="border:1px solid #dde3ec;padding:8px 10px;text-align:left;white-space:nowrap">NIP</th>
                      <th style="border:1px solid #dde3ec;padding:8px 10px;text-align:left">Sprzedawca</th>
                    </tr>
                  </thead>
                  <tbody>
                """);
            foreach ((InvoiceSummary inv, int idx) in invoices.Take(maxRows).Select((inv, i) => (inv, i)))
            {
                string rowBg = idx % 2 == 0 ? "#fff" : "#f9fafb";
                string sellerName = WebUtility.HtmlEncode(inv.Seller?.Name ?? "—");
                string sellerNip = WebUtility.HtmlEncode(inv.Seller?.Nip ?? "—");
                sb.Append($"""
                    <tr style="background:{rowBg}">
                      <td style="border:1px solid #dde3ec;padding:7px 10px;white-space:nowrap">{inv.IssueDate:yyyy-MM-dd}</td>
                      <td style="border:1px solid #dde3ec;padding:7px 10px;white-space:nowrap">{sellerNip}</td>
                      <td style="border:1px solid #dde3ec;padding:7px 10px">{sellerName}</td>
                    </tr>
                    """);
            }
            sb.Append("  </tbody></table>");
            if (count > maxRows)
            {
                sb.Append($"<p style=\"margin:10px 0 0;font-size:12px;color:#666\">… i {count - maxRows} więcej faktur.</p>");
            }
        }
        else
        {
            sb.Append("</p>");
        }

        sb.Append("""
              </td></tr>
              <!-- Footer -->
              <tr><td style="background:#f0f4fa;padding:12px 28px;border-radius:0 0 6px 6px;
                             font-size:11px;color:#888;text-align:center">
                Wiadomość wysłana automatycznie przez KSeFCli
              </td></tr>
            </table>
            </td></tr></table>
            </body></html>
            """);
        return sb.ToString();
    }

    private static string? TryBuildVerificationUrl(
        IVerificationLinkService? linkSvc, string? nip, DateTimeOffset issueDate, string? invoiceHash)
    {
        if (linkSvc == null || string.IsNullOrEmpty(nip) || string.IsNullOrEmpty(invoiceHash))
        {
            return null;
        }
        try
        {
            return linkSvc.BuildInvoiceVerificationUrl(nip, issueDate.Date, invoiceHash);
        }
        catch (ArgumentException ex)
        {
            Log.LogWarning($"[ksef-api] Failed to build verification URL for NIP {nip}: {ex.Message}");
            return null;
        }
        catch (FormatException ex)
        {
            Log.LogWarning($"[ksef-api] Failed to build verification URL for NIP {nip}: {ex.Message}");
            return null;
        }
        catch (InvalidOperationException ex)
        {
            Log.LogWarning($"[ksef-api] Failed to build verification URL for NIP {nip}: {ex.Message}");
            return null;
        }
    }

    private static object MapInvoicesToJson(List<InvoiceSummary> invoices, IVerificationLinkService? linkSvc = null) =>
        invoices.Select(i => new
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
            invoiceHash = i.InvoiceHash,
            ksefVerificationUrl = TryBuildVerificationUrl(linkSvc, i.Seller?.Nip, i.IssueDate, i.InvoiceHash),
        }).ToArray();

    private Task<string> GenerateSummaryAsync(DownloadSummaryParams sumParams, CancellationToken ct)
    {
        // Snapshot mutable state immediately to avoid races with profile/cache switches
        List<InvoiceSummary>? cached = _cachedInvoices?.ToList();
        string profile = ActiveProfile;
        string? nip = sumParams.SeparateByNip ? Config().Nip : null;

        if (cached == null || cached.Count == 0)
        {
            throw new InvalidOperationException("Brak faktur. Wykonaj najpierw wyszukiwanie.");
        }

        string month = sumParams.Month; // "YYYY-MM"
        if (!DateOnly.TryParseExact(month, "yyyy-MM", out DateOnly parsedMonth))
        {
            throw new InvalidOperationException($"Nieprawidłowy format miesiąca: '{month}'. Oczekiwany format: RRRR-MM.");
        }

        string monthKey = parsedMonth.ToString("yyyy-MM");
        List<InvoiceSummary> filtered = cached
            .Where(i => i.IssueDate.ToString("yyyy-MM") == monthKey)
            .OrderBy(i => i.IssueDate)
            .ToList();

        string outputDir = string.IsNullOrWhiteSpace(sumParams.OutputDir) ? OutputDir : sumParams.OutputDir;
        if (!string.IsNullOrEmpty(nip))
        {
            outputDir = Path.Join(outputDir, nip);
        }
        Directory.CreateDirectory(outputDir);
        string filePath = Path.Join(outputDir, $"summary-{monthKey}.csv");

        Log.LogInformation($"[summary] Generating monthly summary for profile '{profile}', month={monthKey}, invoices in cache={cached.Count}, matching={filtered.Count}, output={filePath}");

        StringBuilder sb = new();
        sb.AppendLine($"Podsumowanie faktur za: {monthKey}");
        sb.AppendLine($"Liczba faktur: {filtered.Count}");
        sb.AppendLine();
        sb.AppendLine("Data wystawienia;Numer faktury;Sprzedawca;NIP sprzedawcy;Nabywca;Numer KSeF;Waluta;Kwota brutto");

        foreach (InvoiceSummary inv in filtered)
        {
            string date = inv.IssueDate.ToString("yyyy-MM-dd");
            string invoiceNumber = Csv(inv.InvoiceNumber);
            string sellerName = Csv(inv.Seller?.Name);
            string sellerNip = Csv(inv.Seller?.Nip);
            string buyerName = Csv(inv.Buyer?.Name);
            string ksefNumber = Csv(inv.KsefNumber);
            string currency = Csv(inv.Currency);
            string gross = inv.GrossAmount.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            sb.AppendLine($"{date};{invoiceNumber};{sellerName};{sellerNip};{buyerName};{ksefNumber};{currency};{gross}");
        }

        sb.AppendLine();
        foreach (IGrouping<string, InvoiceSummary> grp in filtered.GroupBy(i => i.Currency ?? ""))
        {
            decimal total = grp.Sum(i => i.GrossAmount);
            sb.AppendLine($"Razem {grp.Key}:;{total.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}");
        }

        // UTF-8 with BOM for Excel compatibility
        byte[] bom = [0xEF, 0xBB, 0xBF];
        byte[] content = Encoding.UTF8.GetBytes(sb.ToString());
        byte[] bytes = [.. bom, .. content];
        File.WriteAllBytes(filePath, bytes);

        Log.LogInformation($"[summary] Saved {filtered.Count} invoice(s) for {monthKey} to {filePath} ({bytes.Length} bytes)");
        return Task.FromResult(filePath);

        static string Csv(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }

            // Neutralize formula injection: Excel/Calc treat cells starting with =,+,-,@ as formulas
            if (value[0] is '=' or '+' or '-' or '@')
            {
                value = "'" + value;
            }

            if (value.Contains(';') || value.Contains('"') || value.Contains('\n'))
            {
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            }

            return value;
        }
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
                    Log.LogInformation($"Saved invoice {inv.KsefNumber} to {Path.Combine(outputDir, $"{fileName}.xml")}");
                }

                if (wantPdf)
                {
                    if (_server != null)
                    {
                        await _server.SendEventAsync("invoice_done", new { current = i, file = fileName, pdf = true, progress = n, total = toDownload.Count }).ConfigureAwait(false);
                    }

                    IVerificationLinkService? pdfLinkSvc = _scope?.ServiceProvider.GetService<IVerificationLinkService>();
                    string? ksefVerificationUrl = TryBuildVerificationUrl(pdfLinkSvc, inv.Seller?.Nip, inv.IssueDate, inv.InvoiceHash);

                    byte[] pdfContent = await XML2PDFCommand.XML2PDF(invoiceXml, Quiet, ct, dlParams.PdfColorScheme, inv.KsefNumber, ksefVerificationUrl).ConfigureAwait(false);
                    string tmpPdf = Path.Combine(workDir, $"{fileName}.pdf");
                    await File.WriteAllBytesAsync(tmpPdf, pdfContent, ct).ConfigureAwait(false);
                    string finalPdf = Path.Combine(outputDir, $"{fileName}.pdf");
                    File.Move(tmpPdf, finalPdf, overwrite: true);
                    Log.LogInformation($"Saved PDF for {inv.KsefNumber} to {finalPdf}");

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

        SavePrefs(LoadPrefs() with
        {
            OutputDir = dlParams.SeparateByNip ? Path.GetDirectoryName(outputDir) ?? outputDir : outputDir,
            ExportXml = dlParams.ExportXml,
            ExportJson = dlParams.ExportJson,
            ExportPdf = wantPdf,
            CustomFilenames = dlParams.CustomFilenames,
            SeparateByNip = dlParams.SeparateByNip,
        });

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

    internal static string SanitizeFileName(string name)
    {
        // Path.GetInvalidFileNameChars() is OS-specific; ':' is missing on Linux but invalid on Windows.
        // Include it explicitly for cross-platform filename compatibility.
        HashSet<char> invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { ':', '*', '?', '"', '<', '>', '|' };
        string sanitized = string.Join("", name.Select(c => invalid.Contains(c) || c == ' ' ? '_' : c));
        if (sanitized.Length > 60)
        {
            sanitized = sanitized[..60];
        }

        return sanitized.Trim();
    }

    private async Task<string> AuthAsync(CancellationToken ct)
    {
        ProfileConfig activeProfile = Config();
        Log.LogInformation($"GUI: session-refresh [profile={ActiveProfile}, nip={activeProfile.Nip}, env={activeProfile.Environment}]");
        AuthenticationOperationStatusResponse response = await Auth(_scope!, ct).ConfigureAwait(false);
        TokenStore tokenStore = GetTokenStore();
        TokenStore.Key key = GetTokenStoreKey();
        tokenStore.SetToken(key, new TokenStore.Data(response));
        string validUntil = response.AccessToken.ValidUntil.ToLocalTime().ToString("HH:mm:ss");
        Log.LogInformation($"GUI: session-refresh OK — access token valid until {response.AccessToken.ValidUntil:HH:mm:ss}, refresh token valid until {response.RefreshToken.ValidUntil:HH:mm:ss}");
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
        XNamespace fallbackNs = "http://crd.gov.pl/wzor/2025/06/25/13775/";
        XNamespace ns = (doc.Root == null || doc.Root.Name.Namespace == XNamespace.None)
            ? fallbackNs
            : doc.Root.Name.Namespace;

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
            netTotal = SumFaElements(fa, e => e.StartsWith("P_13_", StringComparison.Ordinal)),
            vatTotal = SumFaElements(fa, e => e.StartsWith("P_14_", StringComparison.Ordinal) && !e.EndsWith("W", StringComparison.Ordinal)),
            vatTotalCurrency = SumFaElements(fa, e => e.StartsWith("P_14_", StringComparison.Ordinal) && e.EndsWith("W", StringComparison.Ordinal)),
            grossTotal = fa?.Element(ns + "P_15")?.Value,
            lineItems,
            additionalDescriptions = additionalDesc,
        };
    }

    private static string? SumFaElements(XElement? fa, Func<string, bool> localNameFilter)
    {
        if (fa == null)
        {
            return null;
        }
        decimal sum = 0;
        bool any = false;
        foreach (XElement el in fa.Elements())
        {
            if (!localNameFilter(el.Name.LocalName))
            {
                continue;
            }
            if (decimal.TryParse(el.Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out decimal val))
            {
                sum += val;
                any = true;
            }
        }
        return any ? sum.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : null;
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
