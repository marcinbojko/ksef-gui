using System.Text.Json;

using CommandLine;

using KSeF.Client.Core.Models.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace KSeFCli;

[Verb("TokenAuth", HelpText = "Authenticate using a KSeF token")]
public class TokenAuthCommand : IWithConfigCommand
{
    public override async Task<int> ExecuteInScopeAsync(IServiceScope scope, CancellationToken cancellationToken)
    {
        AuthenticationOperationStatusResponse tokenResponse = await TokenAuth(scope, cancellationToken).ConfigureAwait(false);
        Console.Out.WriteLine(JsonSerializer.Serialize(tokenResponse));
        return 0;
    }
}
