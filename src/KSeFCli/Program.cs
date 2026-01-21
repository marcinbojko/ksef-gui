using CommandLine;

namespace KSeFCli;

internal class Program
{
    public static async Task<int> Main(string[] args)
    {
        Parser parser = new Parser(with => with.HelpWriter = Console.Error);

        ParserResult<object> result = parser.ParseArguments<GetFakturaCommand, SzukajFakturCommand, ExportInvoicesCommand, GetExportStatusCommand, TokenAuthCommand, TokenRefreshCommand, CertAuthCommand>(args);

        CancellationTokenSource cts = new CancellationTokenSource();
        Console.CancelKeyPress += (s, e) =>
        {
            Console.WriteLine("Canceling...");
            cts.Cancel();
            e.Cancel = true;
        };

        return await result.MapResult(
            async (GlobalCommand cmd) => await cmd.ExecuteAsync(cts.Token).ConfigureAwait(false),
            errs => Task.FromResult(1)).ConfigureAwait(false);
    }
}
