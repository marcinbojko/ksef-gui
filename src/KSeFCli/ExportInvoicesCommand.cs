using System.ComponentModel;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using KSeF.Client.Api.Services;
using KSeF.Client.Core.Interfaces.Clients;
using KSeF.Client.Core.Interfaces.Services;
using KSeF.Client.Core.Models;
using KSeF.Client.Core.Models.Invoices;
using KSeF.Client.Core.Models.Sessions;
using Spectre.Console.Cli;

namespace KSeFCli;

public class DummyCertificateFetcher : ICertificateFetcher {
    public Task<ICollection<KSeF.Client.Core.Models.Certificates.PemCertificateInfo>> GetCertificatesAsync(CancellationToken cancellationToken) {
        return Task.FromResult<ICollection<KSeF.Client.Core.Models.Certificates.PemCertificateInfo>>(new List<KSeF.Client.Core.Models.Certificates.PemCertificateInfo>());
    }
}


[Description("Initialize an asynchronous invoice export")]
public class ExportInvoicesCommand : AsyncCommand<ExportInvoicesCommand.ExportInvoicesSettings> {

    public class ExportInvoicesSettings : GlobalSettings {
        [CommandOption("--from")]
        [Description("Data początkowa zakresu w formacie ISO-8601 np. 2026-01-03T13:45:00+00:00.")]
        public DateTime From { get; set; }

        [CommandOption("--to")]
        [Description("Data końcowa zakresu w formacie ISO-8601 np. 2026-01-03T13:45:00+00:00.")]
        public DateTime To { get; set; }

        [CommandOption("--date-type")]
        [Description("Typ daty, według której ma być zastosowany zakres.\n" +
                     "Dostępne wartości:\n" +
                     "  \"Issue\" - Data wystawienia faktury.\n" +
                     "  \"Invoicing\" - Data przyjęcia faktury w systemie KSeF (do dalszego przetwarzania).\n" +
                     "  \"PermanentStorage\" - Data trwałego zapisu faktury w repozytorium systemu KSeF.")]
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

    public override async Task<int> ExecuteAsync(CommandContext context, ExportInvoicesSettings settings) {
        IKSeFClient ksefClient = KSeFClientFactory.CreateKSeFClient(settings);

        if (!Enum.TryParse(settings.SubjectType, true, out InvoiceSubjectType subjectType)) {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { Status = "Error", Message = $"Invalid SubjectType: {settings.SubjectType}" }));
            return 1;
        }

        if (!Enum.TryParse(settings.DateType, true, out DateType dateType)) {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { Status = "Error", Message = $"Invalid DateType: {settings.DateType}" }));
            return 1;
        }

        X509Certificate2 certificate;
        try {
            certificate = X509CertificateLoader.LoadPkcs12FromFile(settings.CertificatePath, settings.CertificatePassword);
        } catch (Exception ex) {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { Status = "Error", Message = $"Failed to load certificate: {ex.Message}" }));
            return 1;
        }

        CryptographyService cryptographyService = new CryptographyService(new DummyCertificateFetcher());
        ((CryptographyService)cryptographyService).SetExternalMaterials(certificate, certificate);
        EncryptionData encryptionData = cryptographyService.GetEncryptionData();

        InvoiceQueryFilters queryFilters = new InvoiceQueryFilters {
            DateRange = new DateRange {
                From = settings.From,
                To = settings.To,
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
            settings.Token,
            CancellationToken.None).ConfigureAwait(false);

        Console.WriteLine(JsonSerializer.Serialize(new { Status = "Success", ReferenceNumber = exportInvoicesResponse.ReferenceNumber }));
        return 0;
    }
}
