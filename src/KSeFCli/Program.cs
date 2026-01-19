using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using CommandLine;
using KSeF.Client.Clients;
using KSeF.Client.Core.DTOs.Invoice;
using KSeF.Client.Core.Interfaces.Clients;
using KSeF.Client.Http;
using Microsoft.Extensions.DependencyInjection;
using KSeF.Client.Core.Enums;
using System.Security.Cryptography.X509Certificates;
using KSeF.Client.Core.DTOs.Cryptography;
using KSeF.Client.Extensions;
using KSeF.Client.Api.Services;
using KSeF.Client.Core.Interfaces.Services;
using System.IO;

using System.Threading;

namespace KSeFCli
{

    public static class AsyncRunner
    {
        public static int Run(Func<Task<int>> func) { return Task.Run(func).Result; }
    }

    class Program
    {

        private static IKSeFClient CreateKSeFClient(string baseUrl)
        {
            var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl) };
            var restClient = new RestClient(httpClient);
            return new KSeFClient(restClient);
        }

        class GlobalOptions
        {
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
        class GetInvoiceOptions : GlobalOptions
        {
            [Option('k', "ksef-number", Required = true,
                    HelpText = "KSeF invoice number")]
            public string KsefNumber { get; set; } = null!;
        }

        [Verb("szukaj-faktury", HelpText = "Query invoice metadata")]
        class QueryMetadataOptions : GlobalOptions
        {
            [Option('s', "subject-type", Required = true,
                    HelpText =
                        "Invoice subject type (e.g., Subject1, Subject2, Subject3)")]
            public string SubjectType { get; set; } = null!;

            [Option("from", Required = true,
                    HelpText = "Start date for the query (yyyy-MM-dd)")]
            public DateTime From
            {
                get; set;
            }

            [Option("to", Required = true,
                    HelpText = "End date for the query (yyyy-MM-dd)")]
            public DateTime To
            {
                get; set;
            }

            [Option("date-type", Default = "Issue",
                    HelpText =
                        "Date type for the query (Issue, Invoicing, Acquisition)")]
            public string DateType
            {
                get; set;
            } = "Issue";

            [Option("page-offset", Default = 0,
                    HelpText = "Page offset for pagination")]
            public int PageOffset
            {
                get; set;
            }

            [Option("page-size", Default = 10, HelpText = "Page size for pagination")]
            public int PageSize
            {
                get; set;
            }
        }

        [Verb("export-invoices",
              HelpText = "Initialize an asynchronous invoice export")]
        class ExportInvoicesOptions : GlobalOptions
        {
            [Option("from", Required = true,
                    HelpText = "Start date for the query (yyyy-MM-dd)")]
            public DateTime From { get; set; }

            [Option("to", Required = true,
                    HelpText = "End date for the query (yyyy-MM-dd)")]
            public DateTime To
            {
                get; set;
            }

            [Option("date-type", Default = "Issue",
                    HelpText =
                        "Date type for the query (Issue, Invoicing, Acquisition)")]
            public string DateType
            {
                get; set;
            } = "Issue";

            [Option('s', "subject-type", Required = true,
                    HelpText =
                        "Invoice subject type (e.g., Subject1, Subject2, Subject3)")]
            public string SubjectType
            {
                get; set;
            } = null!;

            [Option("certificate-path", Required = true,
                    HelpText = "Path to the certificate file (.pfx)")]
            public string CertificatePath
            {
                get; set;
            } = null!;

            [Option("certificate-password", Required = false,
                    HelpText = "Password for the certificate file")]
            public string? CertificatePassword
            {
                get; set;
            }
        }

        [Verb("get-export-status",
              HelpText = "Checks the status of an asynchronous invoice export")]
        class GetExportStatusOptions : GlobalOptions
        {
            [Option('r', "reference-number", Required = true,
                    HelpText = "Reference number of the asynchronous export operation")]
            public string ReferenceNumber { get; set; } = null!;
        }

        static int Main(string[] args)
        {
            return Parser.Default
                .ParseArguments<GetInvoiceOptions, QueryMetadataOptions,
                                ExportInvoicesOptions, GetExportStatusOptions>(args)
                .MapResult((GetInvoiceOptions opts) => RunGetInvoice(opts),
                           (QueryMetadataOptions opts) => RunQueryMetadata(opts),
                           (ExportInvoicesOptions opts) => RunExportInvoices(opts),
                           (GetExportStatusOptions opts) => RunGetExportStatus(opts),
                           errs => ExitCodes.GenericError);
        }

        static int RunGetInvoice(GetInvoiceOptions opts)
        {
            return AsyncRunner.Run(async () =>
            {
                IKSeFClient ksefClient = CreateKSeFClient(opts.BaseUrl);
                try
                {
                    string invoice = await ksefClient.GetInvoiceAsync(
                        opts.KsefNumber, opts.Token, CancellationToken.None);
                    Console.WriteLine(JsonSerializer.Serialize(
                        new { Status = "Success", Invoice = invoice }));
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(JsonSerializer.Serialize(
                        new { Status = "Error", Message = ex.Message }));
                    return 1;
                }
                return 0;
            });
        }

        static int RunQueryMetadata(QueryMetadataOptions opts)
        {
            return AsyncRunner.Run(async () =>
            {
                IKSeFClient ksefClient = CreateKSeFClient(opts.BaseUrl);
                try
                {
                    if (!Enum.TryParse(opts.SubjectType, true,
                                       out InvoiceSubjectType subjectType))
                    {
                        Console.Error.WriteLine(JsonSerializer.Serialize(
                            new
                            {
                                Status = "Error",
                                Message = $"Invalid SubjectType: {opts.SubjectType}"
                            }));
                        return 1;
                    }

                    if (!Enum.TryParse(opts.DateType, true, out DateType dateType))
                    {
                        Console.Error.WriteLine(JsonSerializer.Serialize(
                            new
                            {
                                Status = "Error",
                                Message = $"Invalid DateType: {opts.DateType}"
                            }));
                        return 1;
                    }

                    InvoiceQueryFilters invoiceQueryFilters = new InvoiceQueryFilters
                    {
                        SubjectType = subjectType,
                        DateRange = new DateRange
                        {
                            From = opts.From,
                            To = opts.To,
                            DateType = dateType
                        }
                    };

                    PagedInvoiceResponse pagedInvoicesResponse =
                        await ksefClient.QueryInvoiceMetadataAsync(
                            invoiceQueryFilters, opts.Token, pageOffset: opts.PageOffset,
                            pageSize: opts.PageSize,
                            cancellationToken: CancellationToken.None);

                    Console.WriteLine(JsonSerializer.Serialize(
                        new { Status = "Success", Metadata = pagedInvoicesResponse }));
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(JsonSerializer.Serialize(
                        new { Status = "Error", Message = ex.Message }));
                    return 1;
                }
                return 0;
            });
        }
        static int RunExportInvoices(ExportInvoicesOptions opts)
        {
            return AsyncRunner.Run(async () =>
            {
                IKSeFClient ksefClient = CreateKSeFClient(opts.BaseUrl);
                try
                {
                    if (!Enum.TryParse(opts.SubjectType, true,
                                       out InvoiceSubjectType subjectType))
                    {
                        Console.Error.WriteLine(JsonSerializer.Serialize(
                            new
                            {
                                Status = "Error",
                                Message = $"Invalid SubjectType: {opts.SubjectType}"
                            }));
                        return 1;
                    }

                    if (!Enum.TryParse(opts.DateType, true, out DateType dateType))
                    {
                        Console.Error.WriteLine(JsonSerializer.Serialize(
                            new
                            {
                                Status = "Error",
                                Message = $"Invalid DateType: {opts.DateType}"
                            }));
                        return 1;
                    }

                    X509Certificate2 certificate;
                    try
                    {
                        certificate = X509CertificateLoaderExtensions.LoadCertificate(
                            opts.CertificatePath, opts.CertificatePassword);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine(JsonSerializer.Serialize(
                            new
                            {
                                Status = "Error",
                                Message = $"Failed to load certificate: {ex.Message}"
                            }));
                        return 1;
                    }

                    ICryptographyService cryptographyService =
                        new CryptographyService((ICertificateFetcher)certificate);
                    EncryptionData encryptionData = cryptographyService.GetEncryptionData();

                    InvoiceQueryFilters queryFilters = new InvoiceQueryFilters
                    {
                        DateRange = new DateRange
                        {
                            From = opts.From,
                            To = opts.To,
                            DateType = dateType
                        },
                        SubjectType = subjectType
                    };

                    InvoiceExportRequest invoiceExportRequest = new InvoiceExportRequest
                    {
                        Encryption = encryptionData.EncryptionInfo,
                        Filters = queryFilters
                    };

                    OperationResponse exportInvoicesResponse =
                        await ksefClient.ExportInvoicesAsync(
                            invoiceExportRequest, opts.Token, CancellationToken.None);

                    Console.WriteLine(JsonSerializer.Serialize(
                        new
                        {
                            Status = "Success",
                            ReferenceNumber = exportInvoicesResponse.ReferenceNumber
                        }));
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(JsonSerializer.Serialize(
                        new { Status = "Error", Message = ex.Message }));
                    return 1;
                }
                return 0;
            });
        }
    }
