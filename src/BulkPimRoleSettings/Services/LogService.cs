using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

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

    private const int LogRetentionDays = 30;

    // Entries below this level are not written to the file. DEBUG entries
    // (raw API traffic, stack traces) are developer noise; the user-facing
    // log contains INFO, WARN, ERROR, SUCCESS and configuration changes.
    private const LogLevel MinimumLevel = LogLevel.INFO;

    private readonly string _logFilePath;
    private readonly object _lock = new();

    private LogService()
    {
        // Per-user location: %LocalAppData% is only readable by the signed-in
        // Windows user, unlike %ProgramData% which is world-readable.
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PIMSettingsManager", "Logs");

        Directory.CreateDirectory(logDir);
        CleanupOldLogs(logDir);

        var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        _logFilePath = Path.Combine(logDir, $"PIMSettingsManager_{timestamp}.log");

        WriteSessionHeader();
    }

    private void WriteSessionHeader()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";
        WriteRaw("═══════════════ PIMSettings Manager — Session Log ═══════════════");
        WriteRaw($"Started:  {DateTime.Now:yyyy-MM-dd HH:mm:ss} (local time)");
        WriteRaw($"Version:  {version}");
        WriteRaw("──────────────────────────────────────────────────────────────────");
    }

    private static void CleanupOldLogs(string logDir)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-LogRetentionDays);
            foreach (var file in Directory.EnumerateFiles(logDir, "PIMSettingsManager_*.log"))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // Cleanup is best-effort; never block startup on it.
        }
    }

    private static string GetOsTimestampFormat()
    {
        var dtf = System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat;
        return $"{dtf.ShortDatePattern} {dtf.LongTimePattern}";
    }

    private static string SanitizeForFileName(string input)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            input = input.Replace(invalid, '-');
        }

        // Use underscore for spaces to keep filename friendly
        input = input.Replace(' ', '_');
        return input;
    }

    public string LogFilePath => _logFilePath;

    private void WriteRaw(string line)
    {
        lock (_lock)
        {
            try
            {
                File.AppendAllText(_logFilePath, line + Environment.NewLine);
            }
            catch
            {
                // Avoid recursive logging failures
            }
        }
    }

    public void LogSeparator(string? label = null)
    {
        var line = label != null
            ? $"──── {label} ────"
            : "".PadRight(60, '─');
        WriteRaw(line);
    }

    public void Log(LogLevel level, LogCategory category, string message,
        [CallerMemberName] string caller = "")
    {
        if (level < MinimumLevel) return;

        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        WriteRaw($"[{timestamp}] [{level,-7}] {message}");
    }

    /// <summary>
    /// API request details are developer noise — logged at DEBUG, which is
    /// filtered out of the user-facing log file.
    /// </summary>
    public void LogApiCall(string method, string url, string? requestBody = null)
    {
        Log(LogLevel.DEBUG, LogCategory.API, $"{method} {GetUrlPath(url)}");
    }

    /// <summary>
    /// Successful responses stay hidden (DEBUG); failures are logged in a
    /// readable form with the endpoint path only — no query strings.
    /// </summary>
    public void LogApiResponse(string url, int statusCode, string? responseBody = null)
    {
        if (statusCode < 400)
        {
            Log(LogLevel.DEBUG, LogCategory.API, $"Response {statusCode} from {GetUrlPath(url)}");
            return;
        }

        Log(LogLevel.ERROR, LogCategory.API,
            $"A request to Microsoft Graph failed (HTTP {statusCode}, endpoint: {GetUrlPath(url)}).");
    }

    /// <summary>Strips the query string so filters/search terms never reach the log.</summary>
    private static string GetUrlPath(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
    }

    /// <summary>
    /// Records what has been configured. Actual changes are logged prominently;
    /// values that stay the same are kept out of the user-facing log.
    /// </summary>
    public void LogSettingChange(string roleName, string settingName, string? oldValue, string? newValue)
    {
        var before = oldValue ?? "N/A";
        var after = newValue ?? "N/A";

        if (string.Equals(before, after, StringComparison.Ordinal))
        {
            Log(LogLevel.DEBUG, LogCategory.SETTINGS, $"{roleName}: {settingName} unchanged ({before})");
            return;
        }

        Log(LogLevel.INFO, LogCategory.SETTINGS,
            $"Configured '{roleName}' — {settingName}: '{before}' → '{after}'");
    }

    public void LogError(Exception ex, string context = "")
    {
        var prefix = string.IsNullOrWhiteSpace(context) ? "Error" : context.TrimEnd('.', ' ');
        Log(LogLevel.ERROR, LogCategory.SYSTEM, $"{prefix}: {ex.Message}");
        Log(LogLevel.DEBUG, LogCategory.SYSTEM, $"{ex.GetType().Name} StackTrace: {ex.StackTrace}");
    }
}
