using System.Text.Json;

using CommandLine;

using KSeF.Client.Core.Models.Authorization;

namespace KSeFCli;

[Verb("CertAuth", HelpText = "Authenticate using a certificate")]
public class CertAuthCommand : IWithConfigCommand
{
    public override async Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        AuthenticationOperationStatusResponse tokenResponse = await CertAuth(cancellationToken).ConfigureAwait(false);
        Console.WriteLine(JsonSerializer.Serialize(tokenResponse));
        return 0;
    }
}
