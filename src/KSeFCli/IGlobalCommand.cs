using CommandLine;

namespace KSeFCli;

public abstract class IGlobalCommand
{
    public static readonly string CacheDir = System.IO.Path.Join(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".cache", "ksefcli");
    public static readonly string ConfigDir = System.IO.Path.Join(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".config", "ksefcli");

    [Option('v', "verbose", HelpText = "Enable verbose logging")]
    public bool Verbose { get; set; }

    [Option('q', "quiet", HelpText = "Enable quiet mode (warnings and errors only)")]
    public bool Quiet { get; set; }

    public void ConfigureLogging() => Log.ConfigureLogging(Verbose, Quiet);

    public abstract Task<int> ExecuteAsync(CancellationToken cancellationToken);
}
