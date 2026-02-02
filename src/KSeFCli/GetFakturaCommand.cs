using System.Xml.Linq;

using CommandLine;

using KSeF.Client.Core.Interfaces.Clients;

using Microsoft.Extensions.DependencyInjection;

namespace KSeFCli;

[Verb("GetFaktura", HelpText = "Get a single invoice by KSeF number")]
public class GetFakturaCommand : IWithConfigCommand
{
    [Value(0, Required = true, HelpText = "KSeF invoice number")]
    public string KsefNumber { get; set; }

    public override async Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        ProfileConfig config = Config();
        using IServiceScope scope = GetScope();
        IKSeFClient ksefClient = scope.ServiceProvider.GetRequiredService<IKSeFClient>();
        string accessToken = await GetAccessToken(cancellationToken).ConfigureAwait(false);
        string invoiceXml = await ksefClient.GetInvoiceAsync(KsefNumber, accessToken, cancellationToken).ConfigureAwait(false);

        XDocument doc = XDocument.Parse(invoiceXml);
        Console.WriteLine(doc.ToString());

        return 0;
    }
}
