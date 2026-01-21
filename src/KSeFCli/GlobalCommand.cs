using CommandLine;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using CommandLine;
using KSeF.Client.Api.Builders.Auth;
using KSeF.Client.Api.Services;
using KSeF.Client.Core.Interfaces.Clients;
using KSeF.Client.Core.Interfaces.Services;
using KSeF.Client.Core.Models;
using KSeF.Client.Core.Models.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using System.Diagnostics;
using System.Text.Json;
using CommandLine;
using KSeF.Client.Core.Interfaces.Clients;
using KSeF.Client.Core.Interfaces.Services;
using KSeF.Client.Core.Models;
using KSeF.Client.Core.Models.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using KSeF.Client.Api.Services;
using KSeF.Client.Api.Services.Internal;
using KSeF.Client.ClientFactory;
using KSeF.Client.Clients;
using KSeF.Client.Core.Interfaces.Clients;
using KSeF.Client.Core.Interfaces.Services;
using KSeF.Client.DI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KSeFCli;

public abstract class GlobalCommand
{
    [Option('c', "config", HelpText = "Path to config file", Required = true)]
    public string ConfigFile { get; set; } = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".config", "ksefcli", "ksefcli.yaml");

    [Option('a', "active", HelpText = "Active profile name")]
    public string ActiveProfile { get; set; } = "";

    [Option("cache", HelpText = "Active profile name")]
    public string TokenCache { get; set; } = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".cache", "ksefcli", "ksefcli.json");

    [Option('v', "verbose", HelpText = "Enable verbose logging")]
    public bool Verbose { get; set; }

    [Option('q', "quiet", HelpText = "Enable quiet mode (warnings and errors only)")]
    public bool Quiet { get; set; }


    private readonly Lazy<ProfileConfig> _cachedProfile;
    private readonly Lazy<KsefCliConfig> _cachedConfig;
    private readonly Lazy<TokenStore> _tokenStore;

    public GlobalCommand()
    {
        _cachedConfig = new Lazy<KsefCliConfig>(() =>
        {
            return KsefConfigLoader.Load(ConfigFile, ActiveProfile);
        });
        _cachedProfile = new Lazy<ProfileConfig>(() =>
        {
            var config = _cachedConfig.Value;
            return config.Profiles[config.ActiveProfile];
        });
        _tokenStore = new Lazy<TokenStore>(() => new TokenStore(TokenCache));
    }

    public abstract Task<int> ExecuteAsync(CancellationToken cancellationToken);

    protected TokenStore TokenStore => _tokenStore.Value;

    public ProfileConfig Config() => _cachedProfile.Value;

    public TokenStore.Key getTokenStoreKey()
    {
        var config = _cachedConfig.Value;
        var profile = Config();
        return new TokenStore.Key(config.ActiveProfile, profile.Nip, profile.Environment);
    }

    private static void PrintXmlToConsole(string xml, string title)
    {
        Console.WriteLine($"----- {title} -----");
        Console.WriteLine(xml);
        Console.WriteLine($"----- KONIEC: {title} -----\n");
    }


    public async Task<AuthenticationOperationStatusResponse> Auth(CancellationToken cancellationToken)
    {
        var config = Config();
        return config.AuthMethod switch
        {
            AuthMethod.KsefToken => await TokenAuth(cancellationToken).ConfigureAwait(false),
            AuthMethod.Xades => await CertAuth(cancellationToken).ConfigureAwait(false),
            _ => throw new Exception($"Invalid authmethod in profile: {config.Environment}")
        };
    }

    public async Task<AuthenticationOperationStatusResponse> TokenAuth(CancellationToken cancellationToken)
    {
        var config = Config();
        if (config.AuthMethod != AuthMethod.KsefToken)
            throw new InvalidOperationException("This command requires token authentication.");

        using IServiceScope scope = GetScope();
        IKSeFClient ksefClient = scope.ServiceProvider.GetRequiredService<IKSeFClient>();
        ICryptographyService cryptographyService = scope.ServiceProvider.GetRequiredService<ICryptographyService>();
        ILogger<Program> logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

        logger.LogInformation("1. Getting challenge");
        AuthenticationChallengeResponse challenge = await ksefClient.GetAuthChallengeAsync().ConfigureAwait(false);
        long timestampMs = challenge.Timestamp.ToUnixTimeMilliseconds();
        string ksefToken = config.Token ?? throw new InvalidOperationException("KSeF token is missing");
        logger.LogInformation("1. Przygotowanie i szyfrowanie tokena");
        // Przygotuj "token|timestamp" i zaszyfruj RSA-OAEP SHA-256 zgodnie z wymaganiem API
        string tokenWithTimestamp = $"{ksefToken}|{timestampMs}";
        byte[] tokenBytes = System.Text.Encoding.UTF8.GetBytes(tokenWithTimestamp);
        byte[] encrypted = cryptographyService.EncryptKsefTokenWithRSAUsingPublicKey(tokenBytes);
        string encryptedTokenB64 = Convert.ToBase64String(encrypted);
        logger.LogInformation("2. Wysłanie żądania uwierzytelnienia tokenem KSeF");
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
        logger.LogInformation("3. Sprawdzenie statusu uwierzytelniania");
        DateTime startTime = DateTime.UtcNow;
        TimeSpan timeout = TimeSpan.FromMinutes(2);
        AuthStatus status;
        do
        {
            status = await ksefClient.GetAuthStatusAsync(signature.ReferenceNumber, signature.AuthenticationToken.Token).ConfigureAwait(false);
            logger.LogInformation($"      Status: {status.Status.Code} - {status.Status.Description} | upłynęło: {DateTime.UtcNow - startTime:mm\\:ss}");
            if (status.Status.Code != 200)
            {
                await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);

            }
        }
        while (status.Status.Code == 100 && (DateTime.UtcNow - startTime) < timeout);
        if (status.Status.Code != 200)
        {
            throw new InvalidOperationException($"Uwierzytelnienie nie powiodło się lub przekroczono czas oczekiwania. Kod: {status.Status.Code}, Opis: {status.Status.Description}");
        }
        logger.LogInformation("4. Uzyskanie tokena dostępowego (accessToken)");
        AuthenticationOperationStatusResponse tokenResponse = await ksefClient.GetAccessTokenAsync(signature.AuthenticationToken.Token).ConfigureAwait(false);
        return tokenResponse;
    }

    public async Task<AuthenticationOperationStatusResponse> CertAuth(CancellationToken cancellationToken)
    {
        var config = Config();
        if (config.AuthMethod != AuthMethod.Xades)
        {
            throw new InvalidOperationException("This command requires certificate authentication.");
        }

        using IServiceScope scope = GetScope();
        IKSeFClient ksefClient = scope.ServiceProvider.GetRequiredService<IKSeFClient>();
        ICryptographyService cryptoService = scope.ServiceProvider.GetRequiredService<ICryptographyService>();
        SignatureService signatureService = scope.ServiceProvider.GetRequiredService<SignatureService>();
        ILogger<Program> logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();


        X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12FromFile(config.Certificate!.Certificate, config.Certificate!.Password);

        // 1. Get Auth Challenge
        Console.WriteLine("[2] Pobieranie wyzwania (challenge) z KSeF...");
        AuthenticationChallengeResponse challengeResponse = await ksefClient.GetAuthChallengeAsync().ConfigureAwait(false);
        Console.WriteLine($"    Challenge: {challengeResponse.Challenge}");
        // 2. Prepare and Sign AuthTokenRequest
        Console.WriteLine("[3] Budowanie AuthTokenRequest (builder)...");
        AuthenticationTokenRequest authTokenRequest = AuthTokenRequestBuilder
            .Create()
            .WithChallenge(challengeResponse.Challenge)
            .WithContext(AuthenticationTokenContextIdentifierType.Nip, config.Nip)
            .WithIdentifierType(config.Certificate!.SubjectIdentifierType)
            .Build();
        // 4) Serializacja do XML
        Console.WriteLine("[4] Serializacja żądania do XML (unsigned)...");
        string unsignedXml = AuthenticationTokenRequestSerializer.SerializeToXmlString(authTokenRequest);
        PrintXmlToConsole(unsignedXml, "XML przed podpisem");
        Console.WriteLine("[6] Podpisywanie XML (XAdES)...");

        string signedXml = SignatureService.Sign(unsignedXml, certificate);
        PrintXmlToConsole(signedXml, "XML po podpisie (XAdES)");
        // 7) Przesłanie podpisanego XML do KSeF
        Console.WriteLine("[7] Wysyłanie podpisanego XML do KSeF...");
        SignatureResponse submission = await ksefClient.SubmitXadesAuthRequestAsync(signedXml, verifyCertificateChain: false).ConfigureAwait(false);
        Console.WriteLine($"    ReferenceNumber: {submission.ReferenceNumber}");
        // 8) Odpytanie o status
        Console.WriteLine("[8] Odpytanie o status operacji uwierzytelnienia...");
        DateTime startTime = DateTime.UtcNow;
        TimeSpan timeout = TimeSpan.FromMinutes(2);
        AuthStatus status;
        do
        {
            status = await ksefClient.GetAuthStatusAsync(submission.ReferenceNumber, submission.AuthenticationToken.Token).ConfigureAwait(false);
            Console.WriteLine($"      Status: {status.Status.Code} - {status.Status.Description} | upłynęło: {DateTime.UtcNow - startTime:mm\\:ss}");
            if (status.Status.Code != 200)
            {
                await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
        }
        while (status.Status.Code == 100 && (DateTime.UtcNow - startTime) < timeout);
        if (status.Status.Code != 200)
        {
            throw new InvalidOperationException($"Uwierzytelnienie nie powiodło się lub przekroczono czas oczekiwania. Kod: {status.Status.Code}, Opis: {status.Status.Description}");
        }
        // 9) Pobranie access token
        Console.WriteLine("[9] Pobieranie access token...");
        AuthenticationOperationStatusResponse tokenResponse = await ksefClient.GetAccessTokenAsync(submission.AuthenticationToken.Token).ConfigureAwait(false);
        return tokenResponse;
    }

    public async Task<string> GetAccessToken(CancellationToken cancellationToken)
    {
        AuthenticationOperationStatusResponse data = await Auth(cancellationToken).ConfigureAwait(false);
        return data.AccessToken.Token;
    }

    public IServiceScope GetScope()
    {
        ProfileConfig config = Config();
        IServiceCollection services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            LogLevel KsefCliLogLevel = LogLevel.Information;
            LogLevel MicrosoftLogLevel = LogLevel.Warning;
            LogLevel SystemLogLevel = LogLevel.Warning;

            if (Verbose)
            {
                KsefCliLogLevel = LogLevel.Debug;
                MicrosoftLogLevel = LogLevel.Debug;
                SystemLogLevel = LogLevel.Debug;
            }
            if (Quiet)
            {
                KsefCliLogLevel = LogLevel.Warning;
            }

            builder.AddFilter("KSeFCli", KsefCliLogLevel)
                   .AddFilter("Microsoft", MicrosoftLogLevel)
                   .AddFilter("System", SystemLogLevel)
                   .AddConsole(options =>
                   {
                       options.LogToStandardErrorThreshold = LogLevel.Trace;
                   })
                   .AddSimpleConsole(options =>
                   {
                       options.SingleLine = true;
                       options.TimestampFormat = "HH:mm:ss ";
                   });
        });

        KSeF.Client.ClientFactory.Environment environment = config.Environment.ToUpper() switch
        {
            "PROD" => KSeF.Client.ClientFactory.Environment.Prod,
            "DEMO" => KSeF.Client.ClientFactory.Environment.Demo,
            "TEST" => KSeF.Client.ClientFactory.Environment.Test,
            _ => throw new Exception($"Invalid environment in profile: {config.Environment}")
        };

        services.Configure<ProfileConfig>(options =>
        {
            options.Nip = config.Nip;
            options.Token = config.Token;
            options.Environment = config.Environment;
            options.Certificate = config.Certificate;
        });

        services.AddKSeFClient(options =>
        {
            options.BaseUrl = KsefEnvironmentConfig.BaseUrls[environment];
        });

        services.AddSingleton<ICryptographyClient, CryptographyClient>();
        services.AddSingleton<ICertificateFetcher, DefaultCertificateFetcher>();
        services.AddSingleton<ICryptographyService, CryptographyService>();
        // Rejestracja usługi hostowanej (Hosted Service) jako singleton na potrzeby testów
        services.AddSingleton<CryptographyWarmupHostedService>();

        ServiceProvider provider = services.BuildServiceProvider();

        IServiceScope scope = provider.CreateScope();

        // opcjonalne: inicjalizacja lub inne czynności startowe
        // Uruchomienie usługi hostowanej w trybie blokującym (domyślnym) na potrzeby testów
        scope.ServiceProvider.GetRequiredService<CryptographyWarmupHostedService>()
                   .StartAsync(CancellationToken.None).GetAwaiter().GetResult();

        return scope;
    }
}
