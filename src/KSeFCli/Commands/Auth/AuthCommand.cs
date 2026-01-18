using Spectre.Console.Cli;
using System.ComponentModel;

namespace KSeFCli.Commands.Auth
{
    // AuthCommand is a group command. Its subcommands are registered in Program.cs.
    // This class primarily serves to hold the description.
    [Description("Manage KSeF authorization and tokens.")]
    public sealed class AuthCommand : Command
    {
        public override int Execute(CommandContext context)
        {
            // This execute will only run if no subcommand is provided.
            // Show help for the auth branch.
            context.ShowHelp(context.Name);
            return 1; // Indicate error or show help
        }
    }
}