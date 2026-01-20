using KSeF.Client.Api.Services;
using KSeF.Client.Api.Services.Internal;
using KSeF.Client.Clients;
using KSeF.Client.Core.Interfaces.Clients;
using KSeF.Client.Core.Interfaces.Services;
using KSeF.Client.DI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console.Cli;
using Spectre.Console.Cli.Extensions.DependencyInjection;

namespace KSeFCli;

internal class Program
{
    public static async Task<int> Main(string[] args)
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
            options.BaseUrl = KsefEnvironmentsUris.TEST;
        });


        services.AddSingleton<ICryptographyClient, CryptographyClient>();
        services.AddSingleton<ICertificateFetcher, DefaultCertificateFetcher>();
        services.AddSingleton<ICryptographyService>(sp =>
        {
            ICertificateFetcher fetcher = sp.GetRequiredService<ICertificateFetcher>();
            CryptographyService service = new CryptographyService(fetcher);
            service.WarmupAsync().GetAwaiter().GetResult();
            return service;
        });

        ITypeRegistrar registrar = new DependencyInjectionRegistrar(services);
        CommandApp app = new CommandApp(registrar);

        app.Configure(config =>
        {
            config.AddCommand<GetFakturaCommand>("GetFaktura");
            config.AddCommand<SzukajFakturCommand>("SzukajFaktur");
            config.AddCommand<ExportInvoicesCommand>("ExportInvoices");
            config.AddCommand<GetExportStatusCommand>("GetExportStatus");
            config.AddCommand<TokenAuthCommand>("TokenAuth");
            config.AddCommand<TokenRefreshCommand>("TokenRefresh");
            config.AddCommand<CertAuthCommand>("CertAuth");
        });
        return await app.RunAsync(args).ConfigureAwait(false);
    }
}
