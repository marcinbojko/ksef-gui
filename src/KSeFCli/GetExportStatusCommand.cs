using System.Text.Json;
using CommandLine;
using KSeF.Client.Core.Interfaces.Clients;
using KSeF.Client.Core.Models.Invoices;
using Microsoft.Extensions.DependencyInjection;

namespace KSeFCli;

[Verb("GetExportStatus", HelpText = "Checks the status of an asynchronous export operation")]
public class GetExportStatusCommand : GlobalCommand
{
    [Option('r', "reference-number", Required = true, HelpText = "Reference number of the export operation")]
    public string ReferenceNumber { get; set; }

    public override async Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        using IServiceScope scope = GetScope();
        IKSeFClient ksefClient = scope.ServiceProvider.GetRequiredService<IKSeFClient>();

        InvoiceExportStatusResponse exportStatus = await ksefClient.GetInvoiceExportStatusAsync(ReferenceNumber, Token, cancellationToken).ConfigureAwait(false);
        Console.WriteLine(JsonSerializer.Serialize(exportStatus));
        return 0;
    }
}
