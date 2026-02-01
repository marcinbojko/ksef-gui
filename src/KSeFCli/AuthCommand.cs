using CommandLine;

namespace KSeFCli;

[Verb("Auth", HelpText = "Authenticate using configured method")]
public class AuthCommand : IWithConfigCommand
{
    public override async Task<int> ExecuteAsync(CancellationToken cancellationToken)
    {
        await Auth(cancellationToken).ConfigureAwait(false);
        return 0;
    }
}
