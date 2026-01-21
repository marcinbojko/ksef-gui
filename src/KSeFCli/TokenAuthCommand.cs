using System.Diagnostics;
using System.Text.Json;
using CommandLine;
using KSeF.Client.Core.Interfaces.Clients;
using KSeF.Client.Core.Interfaces.Services;
using KSeF.Client.Core.Models;
using KSeF.Client.Core.Models.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KSeFCli;

[Verb("TokenAuth", HelpText = "Authenticate using a KSeF token")]
public class TokenAuthCommand : GlobalCommand
{
    public override async Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        var tokenResponse = await TokenAuth(cancellationToken).ConfigureAwait(false);
        Console.Out.WriteLine(JsonSerializer.Serialize(tokenResponse));
        return 0;
    }
}
