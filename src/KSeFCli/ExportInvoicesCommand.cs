using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using CommandLine;
using KSeF.Client.Core.Interfaces.Clients;
using KSeF.Client.Core.Interfaces.Services;
using KSeF.Client.Core.Models;
using KSeF.Client.Core.Models.Invoices;
using KSeF.Client.Core.Models.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace KSeFCli;

[Verb("ExportInvoices", HelpText = "Initialize an asynchronous invoice export")]
public class ExportInvoicesCommand : IWithConfigCommand
{
    [Option("from", Required = true, HelpText = "Start date in ISO-8601 format")]
    public DateTime From { get; set; }

    [Option("to", Required = true, HelpText = "End date in ISO-8601 format")]
    public DateTime To { get; set; }

    [Option("date-type", Default = "Issue", HelpText = "Date type (Issue, Invoicing, PermanentStorage)")]
    public string DateType { get; set; }

    [Option('s', "subject-type", Required = true, HelpText = "Invoice subject type")]
    public string SubjectType { get; set; }

    public override async Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        var config = Config();
        using IServiceScope scope = GetScope();
        IKSeFClient ksefClient = scope.ServiceProvider.GetRequiredService<IKSeFClient>();
        ICryptographyService cryptographyService = scope.ServiceProvider.GetRequiredService<ICryptographyService>();


        if (!Enum.TryParse(SubjectType, true, out InvoiceSubjectType subjectType))
        {
            Console.Error.WriteLine($"Invalid SubjectType: {SubjectType}");
            return 1;
        }

        if (!Enum.TryParse(DateType, true, out DateType dateType))
        {
            Console.Error.WriteLine($"Invalid DateType: {DateType}");
            return 1;
        }

        EncryptionData encryptionData = cryptographyService.GetEncryptionData();

        InvoiceQueryFilters queryFilters = new InvoiceQueryFilters
        {
            DateRange = new DateRange
            {
                From = From,
                To = To,
                DateType = dateType
            },
            SubjectType = subjectType
        };

        InvoiceExportRequest invoiceExportRequest = new InvoiceExportRequest
        {
            Encryption = encryptionData.EncryptionInfo,
            Filters = queryFilters
        };

        OperationResponse exportInvoicesResponse = await ksefClient.ExportInvoicesAsync(
            invoiceExportRequest,
            config.Token,
            cancellationToken).ConfigureAwait(false);

        Console.WriteLine(JsonSerializer.Serialize(new { ReferenceNumber = exportInvoicesResponse.ReferenceNumber }));
        return 0;
    }
}
