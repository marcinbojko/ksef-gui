using System.Text.Json;

using CommandLine;

using KSeF.Client.Core.Models.Authorization;

namespace KSeFCli;

[Verb("TokenAuth", HelpText = "Authenticate using a KSeF token")]
public class TokenAuthCommand : IWithConfigCommand
{
    public override async Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        AuthenticationOperationStatusResponse tokenResponse = await TokenAuth(cancellationToken).ConfigureAwait(false);
        Console.Out.WriteLine(JsonSerializer.Serialize(tokenResponse));
        return 0;
    }
}
