using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using CommandLine;
using KSeF.Client.Core.Interfaces.Clients;
using KSeF.Client.Core.Interfaces.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace KSeFCli;

[Verb("LinkDoFaktury", HelpText = "Generate a link to an invoice")]
public class LinkDoFakturyCommand : GlobalCommand
{
    [Value(0, Required = true, HelpText = "KSeF invoice number")]
    public string KsefNumber { get; set; }

    public override async Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        var config = Config();
        using IServiceScope scope = GetScope();
        IKSeFClient ksefClient = scope.ServiceProvider.GetRequiredService<IKSeFClient>();
        IVerificationLinkService linkSvc = scope.ServiceProvider.GetRequiredService<IVerificationLinkService>();

        string accessToken = await GetAccessToken(cancellationToken).ConfigureAwait(false);
        string invoiceXml = await ksefClient.GetInvoiceAsync(KsefNumber, accessToken, cancellationToken).ConfigureAwait(false);

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

        Console.WriteLine(url);

        return 0;
    }
}
