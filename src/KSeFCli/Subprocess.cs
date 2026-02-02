using System.Diagnostics;

namespace KSeFCli;

internal record Subprocess(
    IEnumerable<string> CommandAndArgs,
    string? WorkingDir = null,
    IDictionary<string, string?>? Environment = null,
    bool Quiet = false
)
{
    public static bool CheckCommandExists(string command)
    {
        string[] paths = System.Environment.GetEnvironmentVariable("PATH")!.Split(Path.PathSeparator);
        string commandName = command;
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
        {
            if (!commandName.EndsWith(".exe"))
            {
                commandName += ".exe";
            }
        }

        foreach (string path in paths)
        {
            string fullPath = Path.Combine(path, commandName);
            if (File.Exists(fullPath))
            {
                return true;
            }
        }

        return false;
    }

    private Process AddArgsAndEnvironmentToProcessStartInfoAndStart(ProcessStartInfo processStartInfo)
    {
        foreach (System.Collections.DictionaryEntry kvp in System.Environment.GetEnvironmentVariables())
        {
            processStartInfo.Environment[kvp.Key.ToString()!] = kvp.Value?.ToString();
        }

        if (Environment != null)
        {
            foreach (KeyValuePair<string, string?> kvp in Environment)
            {
                Log.LogDebug($"Setting environment variable: {kvp.Key}={kvp.Value}");
                processStartInfo.Environment[kvp.Key] = kvp.Value;
            }
        }
        foreach (string? arg in CommandAndArgs.Skip(1))
        {
            processStartInfo.ArgumentList.Add(arg);
        }

        Process process = Process.Start(processStartInfo)!;
        if (process == null)
        {
            throw new InvalidOperationException("Failed to start process");
        }

        return process;
    }

    private async Task WaitAndCheck(Process process, CancellationToken cancellationToken)
    {
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Command `{string.Join(" ", CommandAndArgs)}` failed with exit code {process.ExitCode}");
        }
    }


    public async Task CheckCallAsync(CancellationToken cancellationToken = default)
    {
        if (CommandAndArgs == null || !CommandAndArgs.Any())
        {
            throw new InvalidOperationException("No command specified.");
        }

        IEnumerable<string> args = CommandAndArgs.Skip(1);
        if (!Quiet)
        {
            Log.LogInformation($"Executing: {string.Join(" ", CommandAndArgs)}");
        }

        ProcessStartInfo processStartInfo = new()
        {
            FileName = CommandAndArgs.First(),
            WorkingDirectory = WorkingDir,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using Process process = AddArgsAndEnvironmentToProcessStartInfoAndStart(processStartInfo);
        await WaitAndCheck(process, cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]> CheckOutputAsync(CancellationToken cancellationToken = default)
    {
        if (!Quiet)
        {
            Log.LogInformation($"Executing (capturing output): {string.Join(" ", CommandAndArgs.Select(a => $"\"{a}\""))}");
        }

        ProcessStartInfo processStartInfo = new ProcessStartInfo
        {
            FileName = CommandAndArgs.First(),
            WorkingDirectory = WorkingDir,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using Process process = AddArgsAndEnvironmentToProcessStartInfoAndStart(processStartInfo);
        using MemoryStream ms = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        await WaitAndCheck(process, cancellationToken).ConfigureAwait(false);
        return ms.ToArray();
    }
}
