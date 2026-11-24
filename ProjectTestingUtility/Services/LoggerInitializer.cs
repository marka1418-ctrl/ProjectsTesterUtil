using NLog;
using NLog.Config;
using NLog.Targets;

namespace ProjectTestingUtility.Services;

public static class LoggerInitializer
{
    public static void Configure()
    {
        var config = new LoggingConfiguration();
        var logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(logDirectory);

        var fileTarget = new FileTarget("file")
        {
            FileName = Path.Combine(logDirectory, "log.log"),
            Layout = @"[${date:dd.MM.yyyy HH\:mm\:ss}]" + "[${level:uppercase=true}][${callsite}] ${message:withexception=true}",
            ArchiveAboveSize = 5242880, // 5 MB
            MaxArchiveFiles = 1,
            ArchiveFileName = Path.Combine(logDirectory, "archives", "${shortdate}_log_{#}.log"),
            ArchiveNumbering = ArchiveNumberingMode.Sequence,
            KeepFileOpen = false,
            ConcurrentWrites = true
        };

        config.AddTarget(fileTarget);
        var rule = new LoggingRule("*", LogLevel.Trace, LogLevel.Fatal, fileTarget);
        config.LoggingRules.Add(rule);

        LogManager.Configuration = config;
    }
}