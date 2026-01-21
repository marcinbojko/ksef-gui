using CommandLine;
using KSeF.Client.Api.Services;
using KSeF.Client.Api.Services.Internal;
using KSeF.Client.Clients;
using KSeF.Client.Core.Interfaces.Clients;
using KSeF.Client.Core.Interfaces.Services;
using KSeF.Client.DI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KSeFCli;

public class GlobalCommand
{
    [Option('t', "token", HelpText = "Session token")]
    public string Token { get; set; } = Environment.GetEnvironmentVariable("KSEF_TOKEN") ?? string.Empty;

    [Option('s', "server", HelpText = "KSeF server address")]
    public string Server { get; set; } = Environment.GetEnvironmentVariable("KSEF_URL") ?? string.Empty;

    [Option('n', "nip", HelpText = "Tax Identification Number (NIP)")]
    public string Nip { get; set; } = Environment.GetEnvironmentVariable("KSEF_NIP") ?? string.Empty;

    public virtual Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(0);
    }
    public IServiceScope GetScope()
    {
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
        services.AddKSeFClient(options =>
        {
            options.BaseUrl = Server ?? KsefEnvironmentsUris.TEST;
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
