using CommandLine;

using Microsoft.Extensions.DependencyInjection;

namespace KSeFCli;

[Verb("Auth", HelpText = "Authenticate using configured method")]
public class AuthCommand : IWithConfigCommand
{
    public override async Task<int> ExecuteInScopeAsync(IServiceScope scope, CancellationToken cancellationToken)
    {
        await Auth(scope, cancellationToken).ConfigureAwait(false);
        return 0;
    }
}
