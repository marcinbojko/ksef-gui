using Spectre.Console;
using Spectre.Console.Cli;
using KsefCli.Commands.Auth;
using KsefCli.Commands.Faktura;
using KsefCli.Config;
using System.Threading.Tasks;

namespace KsefCli
{
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            // Load configuration
            var appConfig = ConfigLoader.LoadConfig(args);

            var app = new CommandApp();

            app.Configure(config =>
            {
                config.AddBranch("auth", auth =>
                {
                    auth.SetDescription("Manage KSeF authorization and tokens.");
                    auth.AddCommand<TokenRefreshCommand>("token").WithDescription("Refresh authentication token.");
                });

                config.AddBranch("faktura", faktura =>
                {
                    faktura.SetDescription("Manage KSeF invoices (upload, download, search).");
                    // Add wyslij and ls commands here later
                });
            });

            return await app.RunAsync(args);
        }
    }
}