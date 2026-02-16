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

    private static readonly string PrefsPath = Path.Combine(CacheDir, "gui-prefs.json");

    private const int DefaultLanPort = 18150;

    /// <summary>Persistent GUI-only preferences per profile name (not stored in YAML).</summary>
    private record ProfilePrefs(bool? IncludeInAutoRefresh = null);

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
        bool IncludeInAutoRefresh = true);

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
            SavePrefs(new GuiPrefs(
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
                ProfilePrefs: root.TryGetProperty("profilePrefs", out JsonElement pp)
                    ? JsonSerializer.Deserialize<Dictionary<string, ProfilePrefs>>(
                        pp.GetRawText(),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    : null
            ));
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
                invoices = MapInvoicesToJson(_cachedInvoices),
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
                    IncludeInAutoRefresh: !ppMap.TryGetValue(kvp.Key, out ProfilePrefs? pp) || pp.IncludeInAutoRefresh != false))
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
                    updatedPpMap[p.Name] = new ProfilePrefs(IncludeInAutoRefresh: p.IncludeInAutoRefresh);
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

    /// <summary>Pages through the KSeF API and returns all invoice summaries matching the filters.</summary>
    private static async Task<List<InvoiceSummary>> FetchAllPagesAsync(
        IKSeFClient client,
        InvoiceQueryFilters filters,
        string accessToken,
        CancellationToken ct)
    {
        List<InvoiceSummary> all = new();
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
                    pagedResponse = await client.QueryInvoiceMetadataAsync(
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
                all.AddRange(pagedResponse.Invoices);
            }

            currentOffset += pageSize;

            if (pagedResponse.HasMore == true)
            {
                await Task.Delay(interPageDelayMs, ct).ConfigureAwait(false);
            }
        } while (pagedResponse.HasMore == true);

        return all;
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
        List<InvoiceSummary> allInvoices = await FetchAllPagesAsync(_ksefClient!, filters, accessToken, ct).ConfigureAwait(false);

        Log.LogInformation(isCyclic
            ? $"[ksef-api] cyclic-search done — {allInvoices.Count} invoices"
            : $"[ksef-api] search done — {allInvoices.Count} invoices");
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

        return MapInvoicesToJson(allInvoices);
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
            if (minutes < 1)
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
            foreach ((string name, ProfileConfig profile) in config.Profiles)
            {
                if (name == ActiveProfile)
                {
                    continue; // JS silentRefresh handles the active profile
                }

                if (ppMap.TryGetValue(name, out ProfilePrefs? pp) && pp.IncludeInAutoRefresh == false)
                {
                    continue;
                }

                try
                {
                    await RefreshProfileInBackgroundAsync(name, profile, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.LogWarning($"[bg-refresh] Profile '{name}': {ex.Message}");
                }
            }
        }
    }

    private async Task RefreshProfileInBackgroundAsync(string name, ProfileConfig profile, CancellationToken ct)
    {
        Log.LogInformation($"[bg-refresh] Profile '{name}' (NIP {profile.Nip}, env {profile.Environment}) starting");
        using IServiceScope scope = GetScope(profile);
        string accessToken = await GetAccessTokenForProfile(name, profile, scope, ct).ConfigureAwait(false);

        string profileKey = new TokenStore.Key(name, profile).ToCacheKey();
        (List<InvoiceSummary>? prev, SearchParams? cachedParams, _) = _invoiceCache.Load(profileKey);
        HashSet<string> prevKeys = prev?.Select(i => i.KsefNumber).ToHashSet() ?? [];

        // Use last manual search params for this profile, or default to "this month"
        SearchParams sp = cachedParams ?? new SearchParams("Subject2", "thismonth", null, "Issue");
        InvoiceQueryFilters filters = await BuildFiltersAsync(sp, ct).ConfigureAwait(false);

        IKSeFClient client = scope.ServiceProvider.GetRequiredService<IKSeFClient>();
        List<InvoiceSummary> invoices = await FetchAllPagesAsync(client, filters, accessToken, ct).ConfigureAwait(false);

        if (prev == null)
        {
            _invoiceCache.Save(profileKey, sp, invoices);
        }
        else
        {
            _invoiceCache.SaveInvoicesOnly(profileKey, invoices);
        }

        int newCount = invoices.Count(i => !prevKeys.Contains(i.KsefNumber));
        Log.LogInformation($"[bg-refresh] Profile '{name}': {invoices.Count} invoices, {newCount} new");

        if (_server != null)
        {
            await _server.SendEventAsync("background_refresh", new
            {
                profileName = name,
                count = invoices.Count,
                newCount,
            }).ConfigureAwait(false);
        }
    }

    private static object MapInvoicesToJson(List<InvoiceSummary> invoices) =>
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
        }).ToArray();

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

                    byte[] pdfContent = await XML2PDFCommand.XML2PDF(invoiceXml, Quiet, ct, dlParams.PdfColorScheme).ConfigureAwait(false);
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
