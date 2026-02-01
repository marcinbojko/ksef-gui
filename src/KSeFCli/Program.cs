using CommandLine;

namespace KSeFCli;

internal class Program
{
    public static async Task<int> Main(string[] args)
    {
        Parser parser = new Parser(with => with.HelpWriter = Console.Error);

        ParserResult<object> result = parser.ParseArguments<GetFakturaCommand, SzukajFakturCommand, TokenAuthCommand, TokenRefreshCommand, CertAuthCommand, AuthCommand, PrzeslijFakturyCommand, PobierzFakturyCommand, LinkDoFakturyCommand, QRDoFakturyCommand, XML2PDFCommand>(args);

        CancellationTokenSource cts = new CancellationTokenSource();
        Console.CancelKeyPress += (s, e) =>
        {
            Console.WriteLine("Canceling...");
            cts.Cancel();
            e.Cancel = true;
        };

        return await result.MapResult(
            (IWithConfigCommand cmd) =>
            {
                try
                {
                    return cmd.ExecuteAsync(cts.Token);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(ex);
                    return Task.FromResult(3);
                }
            },
            errs =>
            {
                if (errs.Any(e => e is HelpRequestedError or HelpVerbRequestedError))
                    return Task.FromResult(0);

                return Task.FromResult(1);
            }
        ).ConfigureAwait(false);
    }
}
