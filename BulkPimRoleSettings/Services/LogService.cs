using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace BulkPimRoleSettings.Services;

public enum LogLevel
{
    DEBUG,
    INFO,
    WARN,
    ERROR,
    SUCCESS
}

public enum LogCategory
{
    AUTH,
    API,
    PERMISSION,
    SETTINGS,
    UI,
    SYSTEM
}

public sealed class LogService
{
    private static readonly Lazy<LogService> _instance = new(() => new LogService());
    public static LogService Instance => _instance.Value;

    private readonly string _logFilePath;
    private readonly object _lock = new();

    private LogService()
    {
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "BulkPimRoleSettings");

        Directory.CreateDirectory(logDir);

        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH-mm-ss");
        _logFilePath = Path.Combine(logDir, $"BulkPimRoleSettings_{timestamp}.log");

        Log(LogLevel.INFO, LogCategory.SYSTEM, $"Log session started. Log file: {_logFilePath}");
    }

    public string LogFilePath => _logFilePath;

    public void LogSeparator(string? label = null)
    {
        lock (_lock)
        {
            try
            {
                var line = label != null
                    ? $"{"",20} ──── {label} ────"
                    : $"{"",20} {"".PadRight(60, '─')}";
                File.AppendAllText(_logFilePath, line + Environment.NewLine);
            }
            catch { }
        }
    }

    public void Log(LogLevel level, LogCategory category, string message,
        [CallerMemberName] string caller = "")
    {
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var entry = $"[{timestamp}] [{level}] [{category}] [{caller}] {message}";

        lock (_lock)
        {
            try
            {
                File.AppendAllText(_logFilePath, entry + Environment.NewLine);
            }
            catch
            {
                // Avoid recursive logging failures
            }
        }
    }

    private const int MaxBodyLogLength = 2000;

    public void LogApiCall(string method, string url, string? requestBody = null)
    {
        Log(LogLevel.INFO, LogCategory.API, $"{method} {url}");
        if (!string.IsNullOrEmpty(requestBody))
        {
            Log(LogLevel.DEBUG, LogCategory.API, $"Request Body:{Environment.NewLine}{FormatJson(requestBody)}");
        }
    }

    public void LogApiResponse(string url, int statusCode, string? responseBody = null)
    {
        var level = statusCode >= 400 ? LogLevel.ERROR : LogLevel.INFO;
        Log(level, LogCategory.API, $"Response {statusCode} from {url}");
        if (!string.IsNullOrEmpty(responseBody))
        {
            var formatted = FormatJson(responseBody);
            if (formatted.Length > MaxBodyLogLength)
            {
                formatted = formatted.Substring(0, MaxBodyLogLength) + $"{Environment.NewLine}  ... (truncated, {responseBody.Length:N0} chars total)";
            }
            Log(LogLevel.DEBUG, LogCategory.API, $"Response Body:{Environment.NewLine}{formatted}");
        }
    }

    public void LogSettingChange(string roleName, string settingName, string? oldValue, string? newValue)
    {
        Log(LogLevel.INFO, LogCategory.SETTINGS,
            $"Role: '{roleName}' | Setting: '{settingName}' | Before: '{oldValue ?? "N/A"}' | After: '{newValue ?? "N/A"}'");
    }

    public void LogError(Exception ex, string context = "")
    {
        Log(LogLevel.ERROR, LogCategory.SYSTEM,
            $"{context} Exception: {ex.GetType().Name}: {ex.Message}");
        Log(LogLevel.DEBUG, LogCategory.SYSTEM, $"StackTrace: {ex.StackTrace}");
    }

    private static string FormatJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return json;
        }
    }
}
