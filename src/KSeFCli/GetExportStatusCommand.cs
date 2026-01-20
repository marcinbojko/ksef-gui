using System.ComponentModel;
using System.Text.Json;
using KSeF.Client.Core.Interfaces.Clients;
using KSeF.Client.Core.Models.Invoices;
using Spectre.Console.Cli;

namespace KSeFCli;


[Description("Checks the status of an asynchronous export operation")]
public class GetExportStatusCommand : AsyncCommand<GetExportStatusCommand.GetExportStatusSettings> {
    public class GetExportStatusSettings : GlobalSettings {
        [CommandOption("-r|--reference-number")]
        [Description("Reference number of the asynchronous export operation")]
        public string ReferenceNumber { get; set; } = null!;
    }
    public override async Task<int> ExecuteAsync(CommandContext context, GetExportStatusSettings settings) {
        IKSeFClient ksefClient = KSeFClientFactory.CreateKSeFClient(settings);
        try {
            InvoiceExportStatusResponse exportStatus = await ksefClient.GetInvoiceExportStatusAsync(
                settings.ReferenceNumber,
                settings.Token).ConfigureAwait(false);

            Console.WriteLine(JsonSerializer.Serialize(new { Status = "Success", ExportStatus = exportStatus }));
        } catch (Exception ex) {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { Status = "Error", Message = ex.Message }));
            return 1;
        }
        return 0;
    }
}
