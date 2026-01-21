using System.Text.Json;
using CommandLine;
using KSeF.Client.Core.Interfaces.Clients;
using Microsoft.Extensions.DependencyInjection;

namespace KSeFCli;

[Verb("GetFaktura", HelpText = "Get a single invoice by KSeF number")]
public class GetFakturaCommand : GlobalCommand
{
    [Option('k', "ksef-number", Required = true, HelpText = "KSeF invoice number")]
    public string KsefNumber { get; set; }

    public override async Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        var config = Config();
        using IServiceScope scope = GetScope();
        IKSeFClient ksefClient = scope.ServiceProvider.GetRequiredService<IKSeFClient>();
        string invoice = await ksefClient.GetInvoiceAsync(KsefNumber, config.Token, cancellationToken).ConfigureAwait(false);
        Console.WriteLine(JsonSerializer.Serialize(new { Invoice = invoice }));
        return 0;
    }
}
