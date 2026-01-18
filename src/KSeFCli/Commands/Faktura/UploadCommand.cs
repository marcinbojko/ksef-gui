using Spectre.Console;
using Spectre.Console.Cli;
using System.ComponentModel;
using System.Threading.Tasks;

namespace KSeFCli.Commands.Faktura
{
    public sealed class UploadCommand : AsyncCommand<UploadCommand.Settings>
    {
        public sealed class Settings : CommandSettings
        {
            [CommandArgument(0, "<FILES>")]
            [Description("XML invoice files to upload.")]
            public string[] Files { get; set; } = null!; // Using null-forgiving operator

            public override ValidationResult Validate()
            {
                if (Files == null || Files.Length == 0)
                {
                    return ValidationResult.Error("At least one file must be specified.");
                }
                // TODO: Add file existence and format validation here
                return ValidationResult.Success();
            }
        }

        public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
        {
            AnsiConsole.MarkupLine($"[green]Sending invoices: {string.Join(", ", settings.Files)}[/]");
            // TODO: call InvoiceService to upload invoices
            return 0; // Success
        }
    }
}