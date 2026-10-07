using System;
using System.Globalization;
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

    // RFC 3339 / ISO 8601 profile: date-time with 'T' separator, millisecond
    // precision and a numeric UTC offset. "zzz" renders as +02:00; RFC 3339
    // also allows "Z", which .NET emits as +00:00 - both are valid offsets.
    private const string Rfc3339Format = "yyyy-MM-dd\\THH:mm:ss.fffzzz";

    // Basic ISO 8601 form for file names, since ':' is not allowed in a path.
    private const string FileNameTimestampFormat = "yyyyMMdd\\THHmmss\\Z";

    // DEBUG entries (raw API traffic, stack traces) are never written to file.
    private const LogLevel MinimumLevel = LogLevel.INFO;

    /// <summary>
    /// The log records sign-in events, applied role and group settings, and
    /// failures. Entries from other areas are discarded even when a caller
    /// logs them at INFO or SUCCESS.
    /// </summary>
    private static bool IsInScope(LogLevel level, LogCategory category)
    {
        if (level is LogLevel.ERROR or LogLevel.WARN) return true;

        return category is LogCategory.AUTH or LogCategory.PERMISSION or LogCategory.SETTINGS;
    }

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

        var timestamp = DateTimeOffset.UtcNow.ToString(FileNameTimestampFormat, CultureInfo.InvariantCulture);
        _logFilePath = Path.Combine(logDir, $"PIMSettingsManager_{timestamp}.log");

        WriteSessionHeader();
    }

    private void WriteSessionHeader()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";
        WriteRaw("=============== PIMSettings Manager - Session Log ===============");
        WriteRaw($"Started:   {Timestamp()}");
        WriteRaw($"Version:   {version}");
        WriteRaw("------------------------------------------------------------------");
    }

    /// <summary>Current local time as an RFC 3339 / ISO 8601 date-time with UTC offset.</summary>
    private static string Timestamp()
        => DateTimeOffset.Now.ToString(Rfc3339Format, CultureInfo.InvariantCulture);

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
            ? $"---- {label} ----"
            : "".PadRight(60, '-');
        WriteRaw(line);
    }

    public void Log(LogLevel level, LogCategory category, string message,
        [CallerMemberName] string caller = "")
    {
        if (level < MinimumLevel) return;
        if (!IsInScope(level, category)) return;

        WriteRaw($"[{Timestamp()}] [{level,-7}] [{category,-10}] {message}");
    }

    /// <summary>Records the request for diagnostics only.</summary>
    public void LogApiCall(string method, string url, string? requestBody = null)
    {
        Log(LogLevel.DEBUG, LogCategory.API, $"{method} {GetUrlPath(url)}");
    }

    /// <summary>
    /// Reports failed Graph requests with the endpoint path only, never the
    /// query string.
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

    public void LogError(Exception ex, string context = "")
    {
        var prefix = string.IsNullOrWhiteSpace(context) ? "Error" : context.TrimEnd('.', ' ');
        Log(LogLevel.ERROR, LogCategory.SYSTEM, $"{prefix}: {ex.Message}");
        Log(LogLevel.DEBUG, LogCategory.SYSTEM, $"{ex.GetType().Name} StackTrace: {ex.StackTrace}");
    }

}
