using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Threading.Tasks;

namespace KSeFCli.Commands.Faktura
{
    public sealed class ListCommand : AsyncCommand<ListCommand.Settings>
    {
        public sealed class Settings : CommandSettings
        {
            [CommandOption("--przed <DATE>")]
            [Description("Filter: before date (YYYY-MM-DD)")]
            public string? Przed { get; set; }

            [CommandOption("--po <DATE>")]
            [Description("Filter: after date (YYYY-MM-DD)")]
            public string? Po { get; set; }

            [CommandOption("--nazwa <NAME>")]
            [Description("Filter: invoice name")]
            public string? Nazwa { get; set; }
        }

        public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
        {
            AnsiConsole.MarkupLine($"[green]Listing invoices: Przed='{settings.Przed ?? "N/A"}', Po='{settings.Po ?? "N/A"}', Nazwa='{settings.Nazwa ?? "N/A"}'[/]");
            // TODO: call InvoiceService to query invoices
            return 0; // Success
        }
    }
}