using Microsoft.Extensions.Configuration;
using Spectre.Console;
using System.IO;

namespace KsefCli.Config
{
    public static class ConfigLoader
    {
        public static AppConfig LoadConfig(string[] args)
        {
            var configBuilder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables("KSEFCLI_") // Prefix environment variables with KSEFCLI_
                .AddCommandLine(args); // Allow command line arguments to override

            // Optionally, add a user-specific config file
            // string userConfigPath = Path.Combine(
            //     Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            //     ".config", "ksefcli", "config.json");
            // if (File.Exists(userConfigPath))
            // {
            //     configBuilder.AddJsonFile(userConfigPath, optional: true, reloadOnChange: true);
            // }

            var configuration = configBuilder.Build();
            var appConfig = new AppConfig();
            configuration.Bind(appConfig);

            ValidateConfig(appConfig);

            return appConfig;
        }

        private static void ValidateConfig(AppConfig config)
        {
            // Basic validation example
            if (string.IsNullOrWhiteSpace(config.KsefApi.BaseUrl))
            {
                AnsiConsole.MarkupLine("[red]Error: KsefApi:BaseUrl is not configured.[/]");
                throw new InvalidOperationException("KsefApi:BaseUrl is required.");
            }
            // Add more validation rules here
        }
    }
}