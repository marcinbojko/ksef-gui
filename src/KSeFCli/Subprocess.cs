using System.Diagnostics;

namespace KSeFCli;

internal record Subprocess(
    IEnumerable<string> CommandAndArgs,
    string? WorkingDir = null,
    IDictionary<string, string?>? Environment = null,
    bool Quiet = false
)
{
    /// <summary>
    /// Find the full path to a command in PATH, or null if not found
    /// </summary>
    public static string? FindCommandInPath(string command)
    {
        string[] paths = System.Environment.GetEnvironmentVariable("PATH")!.Split(Path.PathSeparator);

        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
        {
            // On Windows, check for .exe, .cmd, .bat, .ps1
            string[] extensions = command.Contains('.') ? [""] : [".exe", ".cmd", ".bat", ".ps1"];
            foreach (string ext in extensions)
            {
                string commandName = command + ext;
                foreach (string path in paths)
                {
                    string fullPath = Path.Combine(path, commandName);
                    if (File.Exists(fullPath))
                    {
                        return fullPath;
                    }
                }
            }
            return null;
        }
        else
        {
            // Unix-like: check as-is
            foreach (string path in paths)
            {
                string fullPath = Path.Combine(path, command);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
            return null;
        }
    }

    public static bool CheckCommandExists(string command)
    {
        return FindCommandInPath(command) != null;
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

    private async Task WaitAndCheck(Process process, CancellationToken cancellationToken, string? stderr = null)
    {
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            string errorMsg = $"Command `{string.Join(" ", CommandAndArgs)}` failed with exit code {process.ExitCode}";
            if (!string.IsNullOrEmpty(stderr))
            {
                errorMsg += $"\nStderr: {stderr}";
            }
            throw new InvalidOperationException(errorMsg);
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

        // Resolve command to full path if it's just a command name (not a path)
        string command = CommandAndArgs.First();
        if (!Path.IsPathRooted(command) && !command.Contains(Path.DirectorySeparatorChar))
        {
            string? fullPath = FindCommandInPath(command);
            if (fullPath != null)
            {
                command = fullPath;
            }
        }

        ProcessStartInfo processStartInfo = new()
        {
            FileName = command,
            WorkingDirectory = WorkingDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        using Process process = AddArgsAndEnvironmentToProcessStartInfoAndStart(processStartInfo);
        string stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await WaitAndCheck(process, cancellationToken, stderr).ConfigureAwait(false);
    }

    public async Task<byte[]> CheckOutputAsync(CancellationToken cancellationToken = default)
    {
        if (!Quiet)
        {
            Log.LogInformation($"Executing (capturing output): {string.Join(" ", CommandAndArgs.Select(a => $"\"{a}\""))}");
        }

        // Resolve command to full path if it's just a command name (not a path)
        string command = CommandAndArgs.First();
        if (!Path.IsPathRooted(command) && !command.Contains(Path.DirectorySeparatorChar))
        {
            string? fullPath = FindCommandInPath(command);
            if (fullPath != null)
            {
                command = fullPath;
            }
        }

        ProcessStartInfo processStartInfo = new ProcessStartInfo
        {
            FileName = command,
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
