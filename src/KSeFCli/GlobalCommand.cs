using CommandLine;
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

    private readonly Lazy<ProfileConfig> _cachedProfile;

    public GlobalCommand()
    {
        _cachedProfile = new Lazy<ProfileConfig>(() =>
        {
            var config = KsefConfigLoader.Load(ConfigFile, ActiveProfile);
            return config.Profiles[config.ActiveProfile];
        });
    }

    public abstract Task<int> ExecuteAsync(CancellationToken cancellationToken);

    public ProfileConfig Config() => _cachedProfile.Value;

    public IServiceScope GetScope()
    {
        ProfileConfig config = Config();
        IServiceCollection services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.AddFilter("KSeFCli", LogLevel.Information)
                   .AddFilter("Microsoft", LogLevel.Warning)
                   .AddFilter("System", LogLevel.Warning)
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
