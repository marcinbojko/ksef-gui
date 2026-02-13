using Microsoft.Extensions.Logging;

using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Serilog.Formatting.Json;

public static class Log
{
    private static ILoggerFactory? _loggerFactory;

    public static ILogger<object> Logger { get; private set; } = Microsoft.Extensions.Logging.Abstractions.NullLogger<object>.Instance;

    public static void ConfigureLogging(bool verbose = false, bool quiet = false)
    {
        LogEventLevel consoleLevel = quiet
            ? LogEventLevel.Warning
            : verbose
                ? LogEventLevel.Debug
                : LogEventLevel.Information;

        string logDir = System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            ".cache", "ksefcli");
        System.IO.Directory.CreateDirectory(logDir);
        string logPath = System.IO.Path.Combine(logDir, "ksefcli-.log");

        Serilog.Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(verbose ? LogEventLevel.Debug : LogEventLevel.Information)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .WriteTo.Console(
                formatter: new CompactJsonFormatter(),
                restrictedToMinimumLevel: consoleLevel)
            .WriteTo.File(
                new JsonFormatter(renderMessage: true),
                logPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                shared: true)
            .CreateLogger();

        _loggerFactory?.Dispose();
        _loggerFactory = LoggerFactory.Create(builder =>
            builder.AddSerilog(Serilog.Log.Logger, dispose: true));

        Logger = _loggerFactory.CreateLogger<object>();
    }

    public static void Shutdown()
    {
        _loggerFactory?.Dispose();
        _loggerFactory = null;
        Serilog.Log.CloseAndFlush();
    }

    public static void LogTrace(string message) => Logger.LogTrace("{Message}", message);
    public static void LogDebug(string message) => Logger.LogDebug("{Message}", message);
    public static void LogInformation(string message) => Logger.LogInformation("{Message}", message);
    public static void LogWarning(string message) => Logger.LogWarning("{Message}", message);
    public static void LogError(string message) => Logger.LogError("{Message}", message);
    public static void LogCritical(string message) => Logger.LogCritical("{Message}", message);
}
