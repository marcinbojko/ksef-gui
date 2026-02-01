using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using CommandLine;
using KSeF.Client.Api.Builders.Auth;
using KSeF.Client.Api.Services;
using KSeF.Client.Core.Interfaces.Clients;
using KSeF.Client.Core.Interfaces.Services;
using KSeF.Client.Core.Models;
using KSeF.Client.Core.Models.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KSeFCli;

[Verb("CertAuth", HelpText = "Authenticate using a certificate")]
public class CertAuthCommand : IWithConfigCommand
{
    public override async Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        var tokenResponse = await CertAuth(cancellationToken).ConfigureAwait(false);
        Console.WriteLine(JsonSerializer.Serialize(tokenResponse));
        return 0;
    }
}
