using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using CommandLine;

using KSeF.Client.Api.Builders.Auth;
using KSeF.Client.Api.Services;
using KSeF.Client.ClientFactory;
using KSeF.Client.Core.Interfaces.Clients;
using KSeF.Client.Core.Interfaces.Services;
using KSeF.Client.Core.Models;
using KSeF.Client.Core.Models.Authorization;
using KSeF.Client.DI;
using KSeF.Client.Extensions;

using Microsoft.Extensions.DependencyInjection;

namespace KSeFCli;

public abstract class IWithConfigCommand : IGlobalCommand
{
    [Option('c', "config", HelpText = "Path to config file")]
    public string ConfigFile { get; set; } = System.Environment.GetEnvironmentVariable("KSEFCLI_CONFIG") ?? ResolveDefaultConfigPath();

    private static string ResolveDefaultConfigPath()
    {
        string cwdPath = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "ksefcli.yaml");
        if (System.IO.File.Exists(cwdPath)) return cwdPath;

        string exePath = System.IO.Path.Combine(AppContext.BaseDirectory, "ksefcli.yaml");
        if (System.IO.File.Exists(exePath)) return exePath;

        return System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            ".config", "ksefcli", "ksefcli.yaml");
    }

    [Option('a', "active", HelpText = "Active profile name")]
    public string ActiveProfile { get; set; } = System.Environment.GetEnvironmentVariable("KSEFCLI_ACTIVE") ?? "";

    [Option("cache", HelpText = "Path to token cache file")]
    public string TokenCache { get; set; } = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".cache", "ksefcli", "ksefcli.json");

    [Option("no-tokencache", HelpText = "Disable token cache usage")]
    public bool NoTokenCache { get; set; }

    private Lazy<ProfileConfig> _cachedProfile;
    private Lazy<KsefCliConfig> _cachedConfig;
    private Lazy<TokenStore> _tokenStore;

    public IWithConfigCommand()
    {
        _cachedConfig = new Lazy<KsefCliConfig>(() => ConfigLoader.Load(ConfigFile, ActiveProfile));
        _cachedProfile = new Lazy<ProfileConfig>(() =>
        {
            KsefCliConfig config = _cachedConfig.Value;
            return config.Profiles[config.ActiveProfile];
        });
        _tokenStore = new Lazy<TokenStore>(() => new TokenStore(TokenCache));
    }

    /// <summary>
    /// Discards the cached config, profile, and token store so they are reloaded from disk on next access.
    /// Call after modifying the config file at runtime (e.g. from the GUI config editor).
    /// </summary>
    protected void ResetCachedConfig()
    {
        _cachedConfig = new Lazy<KsefCliConfig>(() => ConfigLoader.Load(ConfigFile, ActiveProfile));
        _cachedProfile = new Lazy<ProfileConfig>(() =>
        {
            KsefCliConfig config = _cachedConfig.Value;
            return config.Profiles[config.ActiveProfile];
        });
        _tokenStore = new Lazy<TokenStore>(() => new TokenStore(TokenCache));
    }

    protected TokenStore GetTokenStore() => _tokenStore.Value;

    public ProfileConfig Config() => _cachedProfile.Value;

    public TokenStore.Key GetTokenStoreKey()
    {
        KsefCliConfig config = _cachedConfig.Value;
        ProfileConfig profile = Config();
        return new TokenStore.Key(config.ActiveProfile, profile);
    }

    private static string StatusInfoToString(StatusInfo statusInfo)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append($"Code: {statusInfo.Code}, Description: {statusInfo.Description}");
        if (statusInfo.Details != null && statusInfo.Details.Any())
        {
            sb.Append($", Details: [{string.Join(", ", statusInfo.Details)}]");
        }
        if (statusInfo.Extensions != null && statusInfo.Extensions.Any())
        {
            sb.Append($", Extensions: {{{string.Join(", ", statusInfo.Extensions.Select(kv => $"{kv.Key}: {kv.Value}"))}}}");
        }
        return sb.ToString();
    }

    private static void PrintXmlToConsole(string xml, string title)
    {
        Log.LogInformation($"----- {title} -----");
        Log.LogInformation(xml);
        Log.LogInformation($"----- KONIEC: {title} -----\n");
    }


    public override async Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        LogConfigSource();
        using var scope = GetScope();
        return await ExecuteInScopeAsync(scope, cancellationToken).ConfigureAwait(false);
    }

    private void LogConfigSource()
    {
        string cfgPath = System.IO.Path.GetFullPath(ConfigFile);
        string? envVar = System.Environment.GetEnvironmentVariable("KSEFCLI_CONFIG");

        string source;
        if (!string.IsNullOrEmpty(envVar))
        {
            source = $"env KSEFCLI_CONFIG";
        }
        else
        {
            string cwdCandidate = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "ksefcli.yaml"));
            string exeCandidate = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(AppContext.BaseDirectory, "ksefcli.yaml"));

            if (cfgPath == cwdCandidate)
                source = "found in current directory";
            else if (cfgPath == exeCandidate)
                source = "found next to executable";
            else
                source = "default (~/.config/ksefcli/)";
        }

        bool exists = System.IO.File.Exists(cfgPath);
        Console.WriteLine($"Config: {cfgPath} [{source}]{(exists ? "" : " [not found]")}");
    }

    public abstract Task<int> ExecuteInScopeAsync(IServiceScope scope, CancellationToken cancellationToken);

    public async Task<AuthenticationOperationStatusResponse> Auth(IServiceScope scope, CancellationToken cancellationToken)
    {
        ProfileConfig config = Config();
        AuthenticationOperationStatusResponse response = config.AuthMethod switch
        {
            AuthMethod.KsefToken => await TokenAuth(scope, cancellationToken).ConfigureAwait(false),
            AuthMethod.Xades => await CertAuth(scope, cancellationToken).ConfigureAwait(false),
            _ => throw new Exception($"Invalid authmethod in profile: {config.Environment}")
        };
        Log.LogInformation($"Access token valid until: {response.AccessToken.ValidUntil} . Refresh token valid until: {response.RefreshToken.ValidUntil}");
        return response;
    }

    public async Task<AuthenticationOperationStatusResponse> TokenAuth(IServiceScope scope, CancellationToken cancellationToken)
    {
        ProfileConfig config = Config();
        if (config.AuthMethod != AuthMethod.KsefToken)
        {
            throw new InvalidOperationException("This command requires token authentication.");
        }

        IKSeFClient ksefClient = scope.ServiceProvider.GetRequiredService<IKSeFClient>();
        ICryptographyService cryptographyService = await GetCryptographicService(scope, cancellationToken).ConfigureAwait(false);

        Log.LogInformation("1. Getting challenge");
        AuthenticationChallengeResponse challenge = await ksefClient.GetAuthChallengeAsync().ConfigureAwait(false);
        long timestampMs = challenge.Timestamp.ToUnixTimeMilliseconds();
        string ksefToken = config.Token ?? throw new InvalidOperationException("KSeF token is missing");
        Log.LogInformation("1. Przygotowanie i szyfrowanie tokena");
        // Przygotuj "token|timestamp" i zaszyfruj RSA-OAEP SHA-256 zgodnie z wymaganiem API
        string tokenWithTimestamp = $"{ksefToken}|{timestampMs}";
        byte[] tokenBytes = System.Text.Encoding.UTF8.GetBytes(tokenWithTimestamp);
        byte[] encrypted = cryptographyService.EncryptKsefTokenWithRSAUsingPublicKey(tokenBytes);
        string encryptedTokenB64 = Convert.ToBase64String(encrypted);
        Log.LogInformation("2. Wysłanie żądania uwierzytelnienia tokenem KSeF");
        Trace.Assert(!string.IsNullOrEmpty(config.Nip), "--nip jest empty");
        AuthenticationKsefTokenRequest request = new AuthenticationKsefTokenRequest
        {
            Challenge = challenge.Challenge,
            ContextIdentifier = new AuthenticationTokenContextIdentifier
            {
                Type = AuthenticationTokenContextIdentifierType.Nip,
                Value = config.Nip
            },
            EncryptedToken = encryptedTokenB64,
            AuthorizationPolicy = null
        };
        SignatureResponse signature = await ksefClient.SubmitKsefTokenAuthRequestAsync(request, new CancellationToken()).ConfigureAwait(false);
        Log.LogInformation("3. Sprawdzenie statusu uwierzytelniania");
        DateTime startTime = DateTime.UtcNow;
        TimeSpan timeout = TimeSpan.FromMinutes(2);
        AuthStatus status;
        do
        {
            status = await ksefClient.GetAuthStatusAsync(signature.ReferenceNumber, signature.AuthenticationToken.Token).ConfigureAwait(false);
            Log.LogInformation($"      Status: {StatusInfoToString(status.Status)} | upłynęło: {DateTime.UtcNow - startTime:mm\\:ss}");
            if (status.Status.Code != 200)
            {
                await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

            }
        }
        while (status.Status.Code == 100 && (DateTime.UtcNow - startTime) < timeout);
        if (status.Status.Code != 200)
        {
            throw new InvalidOperationException($"Uwierzytelnienie nie powiodło się lub przekroczono czas oczekiwania. {StatusInfoToString(status.Status)}");
        }
        Log.LogInformation("4. Uzyskanie tokena dostępowego (accessToken)");
        AuthenticationOperationStatusResponse tokenResponse = await ksefClient.GetAccessTokenAsync(signature.AuthenticationToken.Token).ConfigureAwait(false);
        return tokenResponse;
    }

    public async Task<AuthenticationOperationStatusResponse> CertAuth(IServiceScope scope, CancellationToken cancellationToken)
    {
        ProfileConfig config = Config();
        if (config.AuthMethod != AuthMethod.Xades)
        {
            throw new InvalidOperationException("This command requires certificate authentication.");
        }

        IKSeFClient ksefClient = scope.ServiceProvider.GetRequiredService<IKSeFClient>();
        ICryptographyService cryptoService = await GetCryptographicService(scope, cancellationToken).ConfigureAwait(false);

        byte[] certBytes = Encoding.UTF8.GetBytes(config.Certificate!.Certificate!);
        X509Certificate2 publicCert = certBytes.LoadCertificate();
        X509Certificate2 certificate = publicCert.MergeWithPemKey(config.Certificate.Private_Key!, config.Certificate.Password);

        // 1. Get Auth Challenge
        Log.LogInformation("[2] Pobieranie wyzwania (challenge) z KSeF...");
        AuthenticationChallengeResponse challengeResponse = await ksefClient.GetAuthChallengeAsync().ConfigureAwait(false);
        Log.LogInformation($"    Challenge: {challengeResponse.Challenge}");
        // 2. Prepare and Sign AuthTokenRequest
        Log.LogInformation("[3] Budowanie AuthTokenRequest (builder)...");
        AuthenticationTokenRequest authTokenRequest = AuthTokenRequestBuilder
            .Create()
            .WithChallenge(challengeResponse.Challenge)
            .WithContext(AuthenticationTokenContextIdentifierType.Nip, config.Nip)
            .WithIdentifierType(config.Certificate!.SubjectIdentifierType)
            .Build();
        // 4) Serializacja do XML
        Log.LogInformation("[4] Serializacja żądania do XML (unsigned)...");
        string unsignedXml = AuthenticationTokenRequestSerializer.SerializeToXmlString(authTokenRequest);
        PrintXmlToConsole(unsignedXml, "XML przed podpisem");
        Log.LogInformation("[6] Podpisywanie XML (XAdES)...");

        string signedXml = SignatureService.Sign(unsignedXml, certificate);
        PrintXmlToConsole(signedXml, "XML po podpisie (XAdES)");
        // 7) Przesłanie podpisanego XML do KSeF
        Log.LogInformation("[7] Wysyłanie podpisanego XML do KSeF...");
        SignatureResponse submission = await ksefClient.SubmitXadesAuthRequestAsync(signedXml, verifyCertificateChain: false).ConfigureAwait(false);
        Log.LogInformation($"    ReferenceNumber: {submission.ReferenceNumber}");
        // 8) Odpytanie o status
        Log.LogInformation("[8] Odpytanie o status operacji uwierzytelnienia...");
        DateTime startTime = DateTime.UtcNow;
        TimeSpan timeout = TimeSpan.FromMinutes(2);
        AuthStatus status;
        do
        {
            status = await ksefClient.GetAuthStatusAsync(submission.ReferenceNumber, submission.AuthenticationToken.Token).ConfigureAwait(false);
            Log.LogInformation($"      Status: {StatusInfoToString(status.Status)} | upłynęło: {DateTime.UtcNow - startTime:mm\\:ss}");
            if (status.Status.Code != 200)
            {
                await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
        }
        while (status.Status.Code == 100 && (DateTime.UtcNow - startTime) < timeout);
        if (status.Status.Code != 200)
        {
            throw new InvalidOperationException($"Uwierzytelnienie nie powiodło się lub przekroczono czas oczekiwania. {StatusInfoToString(status.Status)}");
        }
        // 9) Pobranie access token
        Log.LogInformation("[9] Pobieranie access token...");
        AuthenticationOperationStatusResponse tokenResponse = await ksefClient.GetAccessTokenAsync(submission.AuthenticationToken.Token).ConfigureAwait(false);
        return tokenResponse;
    }

    public async Task<string> GetAccessToken(IServiceScope scope, CancellationToken cancellationToken)
    {
        if (NoTokenCache)
        {
            Log.LogInformation("Token cache disabled, starting new auth");
            AuthenticationOperationStatusResponse response = await Auth(scope, cancellationToken).ConfigureAwait(false);
            return response.AccessToken.Token;
        }

        TokenStore tokenStore = GetTokenStore();
        TokenStore.Key key = GetTokenStoreKey();
        TokenStore.Data? storedToken = tokenStore.GetToken(key);

        if (storedToken == null || storedToken.Response.RefreshToken.ValidUntil < DateTime.UtcNow.AddMinutes(1))
        {
            Log.LogInformation("No valid token found in store, starting new auth");
            AuthenticationOperationStatusResponse response = await Auth(scope, cancellationToken).ConfigureAwait(false);
            tokenStore.SetToken(key, new TokenStore.Data(response));
            return response.AccessToken.Token;
        }

        if (storedToken.Response.AccessToken.ValidUntil < DateTime.UtcNow.AddMinutes(10))
        {
            Log.LogInformation("Refreshing token");
            AuthenticationOperationStatusResponse refreshedResponse = await TokenRefresh(scope, storedToken.Response.RefreshToken, cancellationToken).ConfigureAwait(false);
            tokenStore.SetToken(key, new TokenStore.Data(refreshedResponse));
            return refreshedResponse.AccessToken.Token;
        }

        return storedToken.Response.AccessToken.Token;
    }

    public async Task<AuthenticationOperationStatusResponse> TokenRefresh(IServiceScope scope, TokenInfo refreshToken, CancellationToken cancellationToken)
    {
        IKSeFClient ksefClient = scope.ServiceProvider.GetRequiredService<IKSeFClient>();
        RefreshTokenResponse response = await ksefClient.RefreshAccessTokenAsync(refreshToken.Token, cancellationToken).ConfigureAwait(false);
        return new AuthenticationOperationStatusResponse
        {
            AccessToken = response.AccessToken,
            RefreshToken = refreshToken,
        };
    }

    protected IServiceScope GetScope()
    {
        ProfileConfig config = Config();
        IServiceCollection services = new ServiceCollection();
        KSeF.Client.ClientFactory.Environment environment = config.Environment.ToUpper() switch
        {
            "PROD" => KSeF.Client.ClientFactory.Environment.Prod,
            "DEMO" => KSeF.Client.ClientFactory.Environment.Demo,
            "TEST" => KSeF.Client.ClientFactory.Environment.Test,
            _ => throw new Exception($"Invalid environment in profile: {config.Environment}")
        };
        services.AddSingleton(config);
        services.AddKSeFClient(options =>
        {
            options.BaseUrl = KsefEnvironmentConfig.BaseUrls[environment];
        });
        ServiceCollectionExtensions.AddCryptographyClient(services);
        ServiceProvider provider = services.BuildServiceProvider();
        IServiceScope scope = provider.CreateScope();
        return scope;
    }

    public async Task<ICryptographyService> GetCryptographicService(IServiceScope scope, CancellationToken cancellationToken)
    {
        ICryptographyService cryptographyService = scope.ServiceProvider.GetRequiredService<ICryptographyService>();
        await cryptographyService.WarmupAsync(cancellationToken).ConfigureAwait(false);
        return cryptographyService;
    }
}

