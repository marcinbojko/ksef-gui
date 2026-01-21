using System.Text.Json;

using CommandLine;

using KSeF.Client.Core.Interfaces.Clients;
using KSeF.Client.Core.Models.Invoices;

using Microsoft.Extensions.DependencyInjection;

namespace KSeFCli;

[Verb("SzukajFaktur", HelpText = "Query invoice metadata")]
public class SzukajFakturCommand : GlobalCommand
{
    [Option('s', "subject-type", Default = "Subject1", HelpText = "Subject type (Subject1, Subject2, etc.)")]
    public string SubjectType { get; set; }

    [Option("from", Required = true, HelpText = "Start date in ISO-8601 format")]
    public DateTime From { get; set; }

    [Option("to", Required = true, HelpText = "End date in ISO-8601 format")]
    public DateTime To { get; set; }

    [Option("date-type", Default = "Issue", HelpText = "Date type (Issue, Invoicing, PermanentStorage)")]
    public string DateType { get; set; }

    [Option("page-offset", Default = 0, HelpText = "Page offset for pagination")]
    public int PageOffset { get; set; }

    [Option("page-size", Default = 10, HelpText = "Page size for pagination")]
    public int PageSize { get; set; }

    public override async Task<int> ExecuteAsync(CancellationToken cancellationToken)

    {
        using IServiceScope scope = GetScope();
        IKSeFClient ksefClient = scope.ServiceProvider.GetRequiredService<IKSeFClient>();
        SzukajFakturCommand settings = this;

        if (!Enum.TryParse(settings.SubjectType, true, out InvoiceSubjectType subjectType))
        {
            Console.Error.WriteLine($"Invalid SubjectType: {settings.SubjectType}");
            return 1;
        }
        if (!Enum.TryParse(settings.DateType, true, out DateType dateType))
        {
            Console.Error.WriteLine($"Invalid DateType: {settings.DateType}");
            return 1;
        }
        InvoiceQueryFilters invoiceQueryFilters = new InvoiceQueryFilters
        {
            SubjectType = subjectType,
            DateRange = new DateRange
            {
                From = settings.From,
                To = settings.To,
                DateType = dateType
            }
        };

        string accessToken = await GetAccessToken(cancellationToken).ConfigureAwait(false);
        PagedInvoiceResponse pagedInvoicesResponse = await ksefClient.QueryInvoiceMetadataAsync(
            invoiceQueryFilters,
            accessToken,
            pageOffset: settings.PageOffset,
            pageSize: settings.PageSize,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        Console.WriteLine(JsonSerializer.Serialize(pagedInvoicesResponse));
        return 0;
    }
}
