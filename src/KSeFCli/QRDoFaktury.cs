using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using CommandLine;
using KSeF.Client.Api.Services;
using KSeF.Client.Core.Interfaces.Clients;
using KSeF.Client.Core.Interfaces.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace KSeFCli;

[Verb("QRDoFaktury", HelpText = "Generate a QR code for an invoice and save it to a file")]
public class QRDoFakturyCommand : GlobalCommand
{
    [Option('k', "ksef-number", Required = true, HelpText = "KSeF invoice number")]
    public string KsefNumber { get; set; }

    [Option('o', "output", Required = true, HelpText = "Output file path for the QR code (e.g., invoice.jpg)")]
    public string OutputPath { get; set; }

    [Option('p', "pixels", Default = 5, HelpText = "Pixels per module for the QR code")]
    public int PixelsPerModule { get; set; }

    public override async Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        var config = Config();
        using IServiceScope scope = GetScope();
        IKSeFClient ksefClient = scope.ServiceProvider.GetRequiredService<IKSeFClient>();
        IVerificationLinkService linkSvc = scope.ServiceProvider.GetRequiredService<IVerificationLinkService>();

        string invoiceXml = await ksefClient.GetInvoiceAsync(KsefNumber, config.Token, cancellationToken).ConfigureAwait(false);

        XDocument xmlDoc = XDocument.Parse(invoiceXml);
        if (xmlDoc.Root is null)
            throw new InvalidDataException("Invoice XML is missing the root element.");

        XNamespace ns = xmlDoc.Root.GetDefaultNamespace();

        string sellerNip = xmlDoc.Root.Element(ns + "Podmiot1")?.Element(ns + "DaneIdentyfikacyjne")?.Element(ns + "NIP")?.Value ?? throw new InvalidDataException("Could not find seller NIP in invoice XML.");
        string issueDateValue = xmlDoc.Root.Element(ns + "Fa")?.Element(ns + "P_1")?.Value ?? throw new InvalidDataException("Could not find issue date in invoice XML.");
        DateTime issueDate = DateTime.Parse(issueDateValue);

        byte[] invoiceBytes = Encoding.UTF8.GetBytes(invoiceXml);
        byte[] hashBytes = SHA256.HashData(invoiceBytes);
        string invoiceHash = Base64UrlEncoder.Encode(hashBytes);

        string url = linkSvc.BuildInvoiceVerificationUrl(sellerNip, issueDate, invoiceHash);

        byte[] qrCodeBytes = QrCodeService.GenerateQrCode(url, PixelsPerModule);

        await File.WriteAllBytesAsync(OutputPath, qrCodeBytes, cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"QR code saved to {OutputPath}");

        return 0;
    }
}
