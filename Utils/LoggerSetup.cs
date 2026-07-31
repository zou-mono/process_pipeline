using NLog;
using NLog.Config;
using NLog.Targets;
using System;
using System.IO;

public static class LoggerSetup
{
    private static bool _isConfigured = false;

    public static void Configure()
    {
        // 确保只配置一次（防止多次调用导致覆盖）
        if (_isConfigured) return;

        var config = new LoggingConfiguration();
        string plugin_name = "process_pipeline";

        // 1. 生成带时间戳的日志文件路径
        string logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CAD_plugins",
            plugin_name,
            "Logs"
        );
        Directory.CreateDirectory(logDirectory);

        // 文件名格式：YourPlugin_20250723_143025.log
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string logFilePath = Path.Combine(logDirectory, $"{plugin_name}_{timestamp}.log");

        // 2. 配置 FileTarget（使用动态路径）
        var fileTarget = new FileTarget("file")
        {
            FileName = logFilePath,
            KeepFileOpen = true,
            CreateDirs = true, // 如果目录不存在则自动创建
            // 可选：设置编码，确保中文不乱码
            Encoding = System.Text.Encoding.UTF8
        };
        config.AddTarget(fileTarget);
        config.AddRule(LogLevel.Debug, LogLevel.Fatal, fileTarget);

        // 3. 可选：同时配置控制台输出，方便调试
        var consoleTarget = new ConsoleTarget("console")
        {
            AutoFlush = true
        };
        config.AddTarget(consoleTarget);
        config.AddRule(LogLevel.Debug, LogLevel.Fatal, consoleTarget);

        LogManager.Configuration = config;
        _isConfigured = true;

        // 4. 记录启动日志（证明日志系统已就绪）
        var logger = LogManager.GetCurrentClassLogger();
        logger.Info($"日志文件已创建: {logFilePath}");
    }
}