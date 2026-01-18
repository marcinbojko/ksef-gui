using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using KSeFCli.Config;
using Spectre.Console;

namespace KSeFCli.Services
{
    public class TokenStore
    {
        private readonly AppConfig _config;

        public TokenStore(AppConfig config)
        {
            _config = config;
        }

        public async Task SaveTokenAsync(Token token)
        {
            try
            {
                var tokenFilePath = GetTokenFilePath();
                var directory = Path.GetDirectoryName(tokenFilePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
                var jsonString = JsonSerializer.Serialize(token, jsonOptions);
                await File.WriteAllTextAsync(tokenFilePath, jsonString);
                AnsiConsole.MarkupLine($"[green]Token saved to {tokenFilePath}[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error saving token: {ex.Message}[/]");
            }
        }

        public async Task<Token?> LoadTokenAsync()
        {
            try
            {
                var tokenFilePath = GetTokenFilePath();
                if (!File.Exists(tokenFilePath))
                {
                    AnsiConsole.MarkupLine($"[yellow]No token found at {tokenFilePath}[/]");
                    return null;
                }

                var jsonString = await File.ReadAllTextAsync(tokenFilePath);
                var token = JsonSerializer.Deserialize<Token>(jsonString);

                if (token != null && token.IsExpired)
                {
                    AnsiConsole.MarkupLine("[yellow]Loaded token is expired.[/]");
                    return null;
                }

                return token;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error loading token: {ex.Message}[/]");
                return null;
            }
        }

        public void DeleteToken()
        {
            var tokenFilePath = GetTokenFilePath();
            if (File.Exists(tokenFilePath))
            {
                File.Delete(tokenFilePath);
                AnsiConsole.MarkupLine($"[green]Token deleted from {tokenFilePath}[/]");
            }
        }

        private string GetTokenFilePath()
        {
            var path = _config.TokenStore.Path;
            // Expand ~ to user's home directory
            if (path.StartsWith("~"))
            {
                string homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                path = Path.Combine(homeDirectory, path.Substring(1).TrimStart(Path.DirectorySeparatorChar));
            }
            return path;
        }
    }
}