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
    [Option('k', "ksef-number", Required = true, HelpText = "KSeF invoice number")]
    public string KsefNumber { get; set; }

    public override async Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        var config = Config();
        using IServiceScope scope = GetScope();
        IKSeFClient ksefClient = scope.ServiceProvider.GetRequiredService<IKSeFClient>();
        IVerificationLinkService linkSvc = scope.ServiceProvider.GetRequiredService<IVerificationLinkService>();

        string invoiceXml = await ksefClient.GetInvoiceAsync(KsefNumber, config.Token, cancellationToken).ConfigureAwait(false);

        XDocument xmlDoc = XDocument.Parse(invoiceXml);
        XNamespace ns = xmlDoc.Root.GetDefaultNamespace();

        string sellerNip = xmlDoc.Root.Element(ns + "Podmiot1").Element(ns + "DaneIdentyfikacyjne").Element(ns + "NIP").Value;
        DateTime issueDate = DateTime.Parse(xmlDoc.Root.Element(ns + "Fa").Element(ns + "P_1").Value);

        byte[] invoiceBytes = Encoding.UTF8.GetBytes(invoiceXml);
        byte[] hashBytes = SHA256.HashData(invoiceBytes);
        string invoiceHash = Base64UrlEncoder.Encode(hashBytes);

        string url = linkSvc.BuildInvoiceVerificationUrl(sellerNip, issueDate, invoiceHash);

        Console.WriteLine(url);

        return 0;
    }
}
