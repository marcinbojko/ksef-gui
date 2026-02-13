using System.Reflection;

using CommandLine;
using CommandLine.Text;

namespace KSeFCli;

internal class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Print build info
        Assembly asm = System.Reflection.Assembly.GetExecutingAssembly();
        string version = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(asm)
            .FirstOrDefault(a => a.Key == "Version")?.Value ?? "unknown";
        string buildDate = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(asm)
            .FirstOrDefault(a => a.Key == "BuildDate")?.Value ?? "unknown";
        Console.WriteLine($"ksefcli {version} ({buildDate})");

        // Default to GUI mode if no arguments provided (for double-click on Windows)
        if (args.Length == 0)
        {
            args = ["Gui"];
        }

        // https://github.com/commandlineparser/commandline/wiki/How-To
        StringWriter helpWriter = new StringWriter();
        Parser parser = new Parser(with =>
        {
            with.HelpWriter = helpWriter;
            with.EnableDashDash = true;
        });

        ParserResult<object> result = parser.ParseArguments<GetFakturaCommand, SzukajFakturCommand, TokenAuthCommand, TokenRefreshCommand, CertAuthCommand, AuthCommand, PrzeslijFakturyCommand, PobierzFakturyCommand, LinkDoFakturyCommand, QRDoFakturyCommand, XML2PDFCommand, SelfUpdateCommand, PrintConfigCommand, GuiCommand>(args);

        CancellationTokenSource cts = new CancellationTokenSource();
        Console.CancelKeyPress += (s, e) =>
        {
            Console.WriteLine("Canceling...");
            cts.Cancel();
            e.Cancel = true;
        };

        try
        {
            return await result.MapResult(
                async (IGlobalCommand cmd) =>
                {
                    try
                    {
                        cmd.ConfigureLogging();
                        return await cmd.ExecuteAsync(cts.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine(ex.ToString());
                        return 3;
                    }
                },
                errs =>
                {
                    HelpText helpText = HelpText.AutoBuild(result, h =>
                    {
                        h.Copyright = "Copyright (C) 2026 Kamil Cukrowski. Source code licensed under GPLv3.";
                        // new CopyrightInfo("Kamil Cukrowski", 2026);
                        h.AdditionalNewLineAfterOption = false;
                        return h;
                    });
                    Console.WriteLine(helpText);

                    if (errs.Any(e => e is HelpRequestedError or HelpVerbRequestedError))
                    {
                        return Task.FromResult(0);
                    }

                    return Task.FromResult(1);
                }
            ).ConfigureAwait(false);
        }
        finally
        {
            Log.Shutdown();
        }
    }
}
