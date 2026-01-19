using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommandLine;
using KSeF.Client.Api.Services;
using KSeF.Client.Clients;
using KSeF.Client.Core.Interfaces.Clients;
using KSeF.Client.Core.Interfaces.Services;
using KSeF.Client.Core.Models;
using KSeF.Client.Core.Models.Invoices;
using KSeF.Client.Core.Models.Sessions;
using KSeF.Client.Extensions;
using KSeF.Client.Http;
using Microsoft.Extensions.DependencyInjection;

namespace KSeFCli {

    public class DummyCertificateFetcher : ICertificateFetcher {
        public Task<ICollection<KSeF.Client.Core.Models.Certificates.PemCertificateInfo>> GetCertificatesAsync(CancellationToken cancellationToken) {
            return Task.FromResult<ICollection<KSeF.Client.Core.Models.Certificates.PemCertificateInfo>>(new List<KSeF.Client.Core.Models.Certificates.PemCertificateInfo>());
        }
    }

    public static class AsyncRunner {
        public static int Run(Func<Task<int>> func) {
            return Task.Run(func).Result;
        }
    }

    internal class Program {

        private static IKSeFClient CreateKSeFClient(string baseUrl) {
            HttpClient httpClient = new HttpClient { BaseAddress = new Uri(baseUrl) };
            RestClient restClient = new RestClient(httpClient);
            return new KSeFClient(restClient);
        }

        private class GlobalOptions {
            [Option("token", Default = null, HelpText = "KSeF API token")]
            public string Token { get; set; } =
                Environment.GetEnvironmentVariable("KSEF_TOKEN") ??
                throw new InvalidOperationException("KSEF_TOKEN not set");

            [Option("base-url", Default = null, HelpText = "KSeF base URL")]
            public string BaseUrl { get; set; } =
                Environment.GetEnvironmentVariable("KSEF_URL") ??
                throw new InvalidOperationException("KSEF_URL not set");
        }

        [Verb("pobierz-fakture", HelpText = "Get a single invoice by KSeF number")]
        private class GetInvoiceOptions : GlobalOptions {
            [Option('k', "ksef-number", Required = true,
                    HelpText = "KSeF invoice number")]
            public string KsefNumber { get; set; } = null!;
        }

        [Verb("szukaj-faktury", HelpText = "Query invoice metadata")]
        private class QueryMetadataOptions : GlobalOptions {
            [Option('s', "subject-type", Required = true,
                    HelpText = "Invoice subject type (e.g., Subject1, Subject2, Subject3)")]
            public string SubjectType { get; set; } = null!;

            [Option("from", Required = true, HelpText = "Start date for the query (yyyy-MM-dd)")]
            public DateTime From { get; set; }

            [Option("to", Required = true, HelpText = "End date for the query (yyyy-MM-dd)")]
            public DateTime To { get; set; }

            [Option("date-type", Default = "Issue",
                    HelpText = "Date type for the query (Issue, Invoicing, Acquisition)")]
            public string DateType { get; set; } = "Issue";

            [Option("page-offset", Default = 0, HelpText = "Page offset for pagination")]
            public int PageOffset { get; set; }

            [Option("page-size", Default = 10, HelpText = "Page size for pagination")]
            public int PageSize { get; set; }
        }

        [Verb("export-invoices", HelpText = "Initialize an asynchronous invoice export")]
        private class ExportInvoicesOptions : GlobalOptions {
            [Option("from", Required = true, HelpText = "Start date for the query (yyyy-MM-dd)")]
            public DateTime From { get; set; }

            [Option("to", Required = true, HelpText = "End date for the query (yyyy-MM-dd)")]
            public DateTime To { get; set; }

            [Option("date-type", Default = "Issue",
                    HelpText = "Date type for the query (Issue, Invoicing, Acquisition)")]
            public string DateType { get; set; } = "Issue";

            [Option('s', "subject-type", Required = true,
                    HelpText = "Invoice subject type (e.g., Subject1, Subject2, Subject3)")]
            public string SubjectType { get; set; } = null!;

            [Option("certificate-path", Required = true,
                    HelpText = "Path to the certificate file (.pfx)")]
            public string CertificatePath { get; set; } = null!;

            [Option("certificate-password", Required = false,
                    HelpText = "Password for the certificate file")]
            public string? CertificatePassword { get; set; }
        }

        [Verb("get-export-status", HelpText = "Checks the status of an asynchronous invoice export")]
        private class GetExportStatusOptions : GlobalOptions {
            [Option('r', "reference-number", Required = true,
                    HelpText = "Reference number of the asynchronous export operation")]
            public string ReferenceNumber { get; set; } = null!;
        }


        private static int Main(string[] args) {
            return Parser.Default
                .ParseArguments<GetInvoiceOptions, QueryMetadataOptions, ExportInvoicesOptions, GetExportStatusOptions>(args)
                .MapResult((GetInvoiceOptions opts) => RunGetInvoice(opts),
                           (QueryMetadataOptions opts) => RunQueryMetadata(opts),
                           (ExportInvoicesOptions opts) => RunExportInvoices(opts),
                           (GetExportStatusOptions opts) => RunGetExportStatus(opts),
                           // TODO: Implement a proper exit code handling mechanism
                           errs => 1);
        }

        private static int RunGetInvoice(GetInvoiceOptions opts) {
            return AsyncRunner.Run(async () => {
                IKSeFClient ksefClient = CreateKSeFClient(opts.BaseUrl);
                try {
                    string invoice = await ksefClient.GetInvoiceAsync(opts.KsefNumber, opts.Token, CancellationToken.None).ConfigureAwait(false);
                    Console.WriteLine(JsonSerializer.Serialize(new { Status = "Success", Invoice = invoice }));
                }
                catch (Exception ex) {
                    Console.Error.WriteLine(JsonSerializer.Serialize(new { Status = "Error", Message = ex.Message }));
                    return 1;
                }
                return 0;
            });
        }

        private static int RunQueryMetadata(QueryMetadataOptions opts) {
            return AsyncRunner.Run(async () => {
                IKSeFClient ksefClient = CreateKSeFClient(opts.BaseUrl);
                try {
                    if (!Enum.TryParse(opts.SubjectType, true, out InvoiceSubjectType subjectType)) {
                        Console.Error.WriteLine(JsonSerializer.Serialize(new { Status = "Error", Message = $"Invalid SubjectType: {opts.SubjectType}" }));
                        return 1;
                    }

                    if (!Enum.TryParse(opts.DateType, true, out DateType dateType)) {
                        Console.Error.WriteLine(JsonSerializer.Serialize(new { Status = "Error", Message = $"Invalid DateType: {opts.DateType}" }));
                        return 1;
                    }

                    InvoiceQueryFilters invoiceQueryFilters = new InvoiceQueryFilters {
                        SubjectType = subjectType,
                        DateRange = new DateRange {
                            From = opts.From,
                            To = opts.To,
                            DateType = dateType
                        }
                    };

                    PagedInvoiceResponse pagedInvoicesResponse = await ksefClient.QueryInvoiceMetadataAsync(
                        invoiceQueryFilters,
                        opts.Token,
                        pageOffset: opts.PageOffset,
                        pageSize: opts.PageSize,
                        cancellationToken: CancellationToken.None).ConfigureAwait(false);

                    Console.WriteLine(JsonSerializer.Serialize(new { Status = "Success", Metadata = pagedInvoicesResponse }));
                }
                catch (Exception ex) {
                    Console.Error.WriteLine(JsonSerializer.Serialize(new { Status = "Error", Message = ex.Message }));
                    return 1;
                }
                return 0;
            });
        }
        private static int RunExportInvoices(ExportInvoicesOptions opts) {
            return AsyncRunner.Run(async () => {
                IKSeFClient ksefClient = CreateKSeFClient(opts.BaseUrl);
                try {
                    if (!Enum.TryParse(opts.SubjectType, true, out InvoiceSubjectType subjectType)) {
                        Console.Error.WriteLine(JsonSerializer.Serialize(new { Status = "Error", Message = $"Invalid SubjectType: {opts.SubjectType}" }));
                        return 1;
                    }

                    if (!Enum.TryParse(opts.DateType, true, out DateType dateType)) {
                        Console.Error.WriteLine(JsonSerializer.Serialize(new { Status = "Error", Message = $"Invalid DateType: {opts.DateType}" }));
                        return 1;
                    }

                    X509Certificate2 certificate;
                    try {
                        byte[] certBytes = File.ReadAllBytes(opts.CertificatePath);
                        certBytes = File.ReadAllBytes(opts.CertificatePath);
                        certificate = X509CertificateLoader.LoadPkcs12FromFile(opts.CertificatePath, opts.CertificatePassword);
                    }
                    catch (Exception ex) {
                        Console.Error.WriteLine(JsonSerializer.Serialize(new { Status = "Error", Message = $"Failed to load certificate: {ex.Message}" }));
                        return 1;
                    }

                    ICryptographyService cryptographyService = new CryptographyService(new DummyCertificateFetcher());
                    ((CryptographyService)cryptographyService).SetExternalMaterials(certificate, certificate);
                    EncryptionData encryptionData = cryptographyService.GetEncryptionData();

                    InvoiceQueryFilters queryFilters = new InvoiceQueryFilters {
                        DateRange = new DateRange {
                            From = opts.From,
                            To = opts.To,
                            DateType = dateType
                        },
                        SubjectType = subjectType
                    };

                    InvoiceExportRequest invoiceExportRequest = new InvoiceExportRequest {
                        Encryption = encryptionData.EncryptionInfo,
                        Filters = queryFilters
                    };

                    OperationResponse exportInvoicesResponse = await ksefClient.ExportInvoicesAsync(
                        invoiceExportRequest,
                        opts.Token,
                        CancellationToken.None).ConfigureAwait(false);

                    Console.WriteLine(JsonSerializer.Serialize(new { Status = "Success", ReferenceNumber = exportInvoicesResponse.ReferenceNumber }));
                }
                catch (Exception ex) {
                    Console.Error.WriteLine(JsonSerializer.Serialize(new { Status = "Error", Message = ex.Message }));
                    return 1;
                }
                return 0;
            });
        }
        private static int RunGetExportStatus(GetExportStatusOptions opts) {
            return AsyncRunner.Run(async () => {
                IKSeFClient ksefClient = CreateKSeFClient(opts.BaseUrl);
                try {
                    InvoiceExportStatusResponse exportStatus = await ksefClient.GetInvoiceExportStatusAsync(
                        opts.ReferenceNumber,
                        opts.Token).ConfigureAwait(false);

                    Console.WriteLine(JsonSerializer.Serialize(new { Status = "Success", ExportStatus = exportStatus }));
                }
                catch (Exception ex) {
                    Console.Error.WriteLine(JsonSerializer.Serialize(new { Status = "Error", Message = ex.Message }));
                    return 1;
                }
                return 0;
            });
        }
    }
}
