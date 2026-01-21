using System.Text.Json;
using CommandLine;
using KSeF.Client.Core.Interfaces.Clients;
using KSeF.Client.Core.Models.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;


namespace KSeFCli;

[Verb("TokenRefresh", HelpText = "Refresh an existing session token")]
public class TokenRefreshCommand : GlobalCommand
{
    public override async Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        var config = Config();
        using IServiceScope scope = GetScope();
        IKSeFClient ksefClient = scope.ServiceProvider.GetRequiredService<IKSeFClient>();
        ILogger<Program> logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        if (string.IsNullOrEmpty(config.Token))
        {
            Console.Error.WriteLine("No refresh token provided. Use --token to provide a refresh token.");
            return 1;
        }
        logger.LogInformation("Refreshing token...");
        RefreshTokenResponse tokenResponse = await ksefClient.RefreshAccessTokenAsync(config.Token, cancellationToken).ConfigureAwait(false);
        Console.Out.WriteLine(JsonSerializer.Serialize(tokenResponse));
        logger.LogInformation("Token refreshed successfully.");
        return 0;
    }
}
