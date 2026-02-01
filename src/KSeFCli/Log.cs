using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

public static class Log
{
    private static ILoggerFactory? _loggerFactory;

    public static ILogger Logger { get; private set; } = default!;

    public static void ConfigureLogging(bool verbose = false, bool quiet = false)
    {
        _loggerFactory = LoggerFactory.Create(builder =>
        {
            LogLevel ksefCliLevel = LogLevel.Information;
            LogLevel microsoftLevel = LogLevel.Warning;
            LogLevel systemLevel = LogLevel.Warning;

            if (verbose)
            {
                ksefCliLevel = LogLevel.Debug;
                microsoftLevel = LogLevel.Debug;
                systemLevel = LogLevel.Debug;
            }

            if (quiet)
            {
                ksefCliLevel = LogLevel.Warning;
            }

            builder.AddFilter("KSeFCli", ksefCliLevel)
                   .AddFilter("Microsoft", microsoftLevel)
                   .AddFilter("System", systemLevel)
                   .AddConsole(options =>
                   {
                       options.LogToStandardErrorThreshold = LogLevel.Trace;
                   })
                   .AddSimpleConsole(options =>
                   {
                       options.SingleLine = true;
                       options.TimestampFormat = "HH:mm:ss ";
                   });
        });

        Logger = _loggerFactory.CreateLogger("KSeFCli");
    }

    public static void LogTrace(string message) => Logger.LogTrace(message);
    public static void LogDebug(string message) => Logger.LogDebug(message);
    public static void LogInformation(string message) => Logger.LogInformation(message);
    public static void LogWarning(string message) => Logger.LogWarning(message);
    public static void LogError(string message) => Logger.LogError(message);
    public static void LogCritical(string message) => Logger.LogCritical(message);
}
