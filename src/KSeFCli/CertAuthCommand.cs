using System.Text.Json;

using CommandLine;

using KSeF.Client.Core.Models.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace KSeFCli;

[Verb("CertAuth", HelpText = "Authenticate using a certificate")]
public class CertAuthCommand : IWithConfigCommand
{
    public override async Task<int> ExecuteInScopeAsync(IServiceScope scope, CancellationToken cancellationToken)
    {
        AuthenticationOperationStatusResponse tokenResponse = await CertAuth(scope, cancellationToken).ConfigureAwait(false);
        Console.WriteLine(JsonSerializer.Serialize(tokenResponse));
        return 0;
    }
}
