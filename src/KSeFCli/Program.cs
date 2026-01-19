using System;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KSeF.Client.Api.Services;
using KSeF.Client.Clients;
using KSeF.Client.Core.Interfaces.Clients;
using KSeF.Client.Core.Interfaces.Services;
using KSeF.Client.Core.Models;
using KSeF.Client.Core.Models.Invoices;
using KSeF.Client.Core.Models.Sessions;
using KSeF.Client.Http;
using Spectre.Console.Cli;

namespace KSeFCli
{
    public class DummyCertificateFetcher : ICertificateFetcher
    {
        public Task<ICollection<KSeF.Client.Core.Models.Certificates.PemCertificateInfo>> GetCertificatesAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<ICollection<KSeF.Client.Core.Models.Certificates.PemCertificateInfo>>(new List<KSeF.Client.Core.Models.Certificates.PemCertificateInfo>());
        }
    }

    public class GlobalSettings : CommandSettings
    {
        [CommandOption("--token")]
        [Description("KSeF API token")]
        public string Token { get; set; } = Environment.GetEnvironmentVariable("KSEF_TOKEN") ?? string.Empty;

        [CommandOption("--base-url")]
        [Description("KSeF base URL")]
        [DefaultValue("https://api-test.ksef.mf.gov.pl/v2")]
        public string BaseUrl { get; set; } = string.Empty;
    }

    public class GetInvoiceSettings : GlobalSettings
    {
        [CommandOption("-k|--ksef-number")]
        [Description("KSeF invoice number")]
        public string KsefNumber { get; set; } = null!;
    }

    public class QueryMetadataSettings : GlobalSettings
    {
        [CommandOption("-s|--subject-type")]
        [Description("Invoice subject type (e.g., Subject1, Subject2, Subject3)")]
        public string SubjectType { get; set; } = null!;

        [CommandOption("--from")]
        [Description("Start date for the query (yyyy-MM-dd)")]
        public DateTime From { get; set; }

        [CommandOption("--to")]
        [Description("End date for the query (yyyy-MM-dd)")]
        public DateTime To { get; set; }

        [CommandOption("--date-type")]
        [Description("Date type for the query (Issue, Invoicing, Acquisition)")]
        [DefaultValue("Issue")]
        public string DateType { get; set; } = "Issue";

        [CommandOption("--page-offset")]
        [Description("Page offset for pagination")]
        [DefaultValue(0)]
        public int PageOffset { get; set; }

        [CommandOption("--page-size")]
        [Description("Page size for pagination")]
        [DefaultValue(10)]
        public int PageSize { get; set; }
    }

    public class ExportInvoicesSettings : GlobalSettings
    {
        [CommandOption("--from")]
        [Description("Start date for the query (yyyy-MM-dd)")]
        public DateTime From { get; set; }

        [CommandOption("--to")]
        [Description("End date for the query (yyyy-MM-dd)")]
        public DateTime To { get; set; }

        [CommandOption("--date-type")]
        [Description("Date type for the query (Issue, Invoicing, Acquisition)")]
        [DefaultValue("Issue")]
        public string DateType { get; set; } = "Issue";

        [CommandOption("-s|--subject-type")]
        [Description("Invoice subject type (e.g., Subject1, Subject2, Subject3)")]
        public string SubjectType { get; set; } = null!;

        [CommandOption("--certificate-path")]
        [Description("Path to the certificate file (.pfx)")]
        public string CertificatePath { get; set; } = null!;

        [CommandOption("--certificate-password")]
        [Description("Password for the certificate file")]
        public string? CertificatePassword { get; set; }
    }

    public class GetExportStatusSettings : GlobalSettings
    {
        [CommandOption("-r|--reference-number")]
        [Description("Reference number of the asynchronous export operation")]
        public string ReferenceNumber { get; set; } = null!;
    }

    public static class KSeFClientFactory
    {
        public static IKSeFClient CreateKSeFClient(string baseUrl)
        {
            var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl) };
            var restClient = new RestClient(httpClient);
            return new KSeFClient(restClient);
        }
    }

    public class GetInvoiceCommand : AsyncCommand<GetInvoiceSettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, GetInvoiceSettings settings)
        {
            var ksefClient = KSeFClientFactory.CreateKSeFClient(settings.BaseUrl);
            var invoice = await ksefClient.GetInvoiceAsync(settings.KsefNumber, settings.Token, CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(new { Status = "Success", Invoice = invoice }));
            return 0;
        }
    }

    public class QueryMetadataCommand : AsyncCommand<QueryMetadataSettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, QueryMetadataSettings settings)
        {
            var ksefClient = KSeFClientFactory.CreateKSeFClient(settings.BaseUrl);

            if (!Enum.TryParse(settings.SubjectType, true, out InvoiceSubjectType subjectType))
            {
                Console.Error.WriteLine(JsonSerializer.Serialize(new { Status = "Error", Message = $"Invalid SubjectType: {settings.SubjectType}" }));
                return 1;
            }

            if (!Enum.TryParse(settings.DateType, true, out DateType dateType))
            {
                Console.Error.WriteLine(JsonSerializer.Serialize(new { Status = "Error", Message = $"Invalid DateType: {settings.DateType}" }));
                return 1;
            }

            var invoiceQueryFilters = new InvoiceQueryFilters
            {
                SubjectType = subjectType,
                DateRange = new DateRange
                {
                    From = settings.From,
                    To = settings.To,
                    DateType = dateType
                }
            };

            var pagedInvoicesResponse = await ksefClient.QueryInvoiceMetadataAsync(
                invoiceQueryFilters,
                settings.Token,
                pageOffset: settings.PageOffset,
                pageSize: settings.PageSize,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);

            Console.WriteLine(JsonSerializer.Serialize(new { Status = "Success", Metadata = pagedInvoicesResponse }));
            return 0;
        }
    }

    public class ExportInvoicesCommand : AsyncCommand<ExportInvoicesSettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, ExportInvoicesSettings settings)
        {
            var ksefClient = KSeFClientFactory.CreateKSeFClient(settings.BaseUrl);

            if (!Enum.TryParse(settings.SubjectType, true, out InvoiceSubjectType subjectType))
            {
                Console.Error.WriteLine(JsonSerializer.Serialize(new { Status = "Error", Message = $"Invalid SubjectType: {settings.SubjectType}" }));
                return 1;
            }

            if (!Enum.TryParse(settings.DateType, true, out DateType dateType))
            {
                Console.Error.WriteLine(JsonSerializer.Serialize(new { Status = "Error", Message = $"Invalid DateType: {settings.DateType}" }));
                return 1;
            }

            X509Certificate2 certificate;
            try
            {
                certificate = X509CertificateLoader.LoadPkcs12FromFile(settings.CertificatePath, settings.CertificatePassword);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(JsonSerializer.Serialize(new { Status = "Error", Message = $"Failed to load certificate: {ex.Message}" }));
                return 1;
            }

            var cryptographyService = new CryptographyService(new DummyCertificateFetcher());
            ((CryptographyService)cryptographyService).SetExternalMaterials(certificate, certificate);
            var encryptionData = cryptographyService.GetEncryptionData();

            var queryFilters = new InvoiceQueryFilters
            {
                DateRange = new DateRange
                {
                    From = settings.From,
                    To = settings.To,
                    DateType = dateType
                },
                SubjectType = subjectType
            };

            var invoiceExportRequest = new InvoiceExportRequest
            {
                Encryption = encryptionData.EncryptionInfo,
                Filters = queryFilters
            };

            var exportInvoicesResponse = await ksefClient.ExportInvoicesAsync(
                invoiceExportRequest,
                settings.Token,
                CancellationToken.None).ConfigureAwait(false);

            Console.WriteLine(JsonSerializer.Serialize(new { Status = "Success", ReferenceNumber = exportInvoicesResponse.ReferenceNumber }));
            return 0;
        }
    }

    public class GetExportStatusCommand : AsyncCommand<GetExportStatusSettings>
    {
        public override async Task<int> ExecuteAsync(CommandContext context, GetExportStatusSettings settings)
        {
            var ksefClient = KSeFClientFactory.CreateKSeFClient(settings.BaseUrl);
            var exportStatus = await ksefClient.GetInvoiceExportStatusAsync(
                settings.ReferenceNumber,
                settings.Token).ConfigureAwait(false);

            Console.WriteLine(JsonSerializer.Serialize(new { Status = "Success", ExportStatus = exportStatus }));
            return 0;
        }
    }

    internal class Program
    {
        public static int Main(string[] args)
        {
            var app = new CommandApp();
            app.Configure(config =>
            {
                config.PropagateExceptions();
                
                config.AddCommand<GetInvoiceCommand>("pobierz-fakture")
                    .WithDescription("Get a single invoice by KSeF number");
                config.AddCommand<QueryMetadataCommand>("szukaj-faktury")
                    .WithDescription("Query invoice metadata");
                config.AddCommand<ExportInvoicesCommand>("export-invoices")
                    .WithDescription("Initialize an asynchronous invoice export");
                config.AddCommand<GetExportStatusCommand>("get-export-status")
                    .WithDescription("Checks the status of an asynchronous export operation");
            });

            return app.Run(args);
        }
    }
}
