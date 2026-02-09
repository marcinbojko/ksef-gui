using System.Text.Json;

using CommandLine;

using KSeF.Client.Core.Interfaces.Clients;
using KSeF.Client.Core.Models.Invoices;

using Microsoft.Extensions.DependencyInjection;

namespace KSeFCli;

[Verb("PobierzFaktury", HelpText = "Download invoices based on search criteria.")]
public class PobierzFakturyCommand : SzukajFakturCommand
{
    [Option('o', "outputdir", Required = true, HelpText = "Output directory to save files to.")]
    public required string OutputDir { get; set; }

    [Option('p', "pdf", HelpText = "Save also pdf files.")]
    public bool Pdf { get; set; }

    [Option("useInvoiceNumber", HelpText = "Use InvoiceNumber instead of KsefNumber for the filename to save invoices.")]
    public bool UseInvoiceNumber { get; set; }

    public override async Task<int> ExecuteInScopeAsync(IServiceScope scope, CancellationToken cancellationToken)
    {
        if (Pdf)
        {
            XML2PDFCommand.AssertPdfGeneratorAvailable();
        }

        Directory.CreateDirectory(OutputDir);

        IKSeFClient ksefClient = scope.ServiceProvider.GetRequiredService<IKSeFClient>();

        List<InvoiceSummary> invoices = await base.SzukajFaktury(scope, ksefClient, cancellationToken).ConfigureAwait(false);

        foreach (InvoiceSummary invoiceSummary in invoices)
        {
            string fileName = UseInvoiceNumber ? invoiceSummary.InvoiceNumber : invoiceSummary.KsefNumber;
            string jsonFilePath = Path.Combine(OutputDir, $"{fileName}.json");
            string xmlFilePath = Path.Combine(OutputDir, $"{fileName}.xml");

            await File.WriteAllTextAsync(jsonFilePath, JsonSerializer.Serialize(invoiceSummary), cancellationToken).ConfigureAwait(false);

            string invoiceXml = await ksefClient.GetInvoiceAsync(invoiceSummary.KsefNumber, await GetAccessToken(scope, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(xmlFilePath, invoiceXml, cancellationToken).ConfigureAwait(false);

            Console.WriteLine($"Saved invoice {invoiceSummary.KsefNumber} to {xmlFilePath}");

            if (Pdf)
            {
                byte[] pdfContent = await XML2PDFCommand.XML2PDF(invoiceXml, Quiet, cancellationToken).ConfigureAwait(false);
                string outputPdfPath = Path.ChangeExtension(xmlFilePath, ".pdf");
                await File.WriteAllBytesAsync(outputPdfPath, pdfContent, cancellationToken).ConfigureAwait(false);
                Console.WriteLine($"Saved PDF for {xmlFilePath} to {outputPdfPath}");
            }
        }

        return 0;
    }
}
