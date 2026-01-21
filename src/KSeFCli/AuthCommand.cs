using CommandLine;

namespace KSeFCli;

[Verb("Auth", HelpText = "Authenticate using configured method")]
public class AuthCommand : GlobalCommand
{
    public override async Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        await Auth(cancellationToken).ConfigureAwait(false);
        return 0;
    }
}
