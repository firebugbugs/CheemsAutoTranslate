using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace CursorTranslator.Services;

public static class AppLog
{
    private const string FilePrefix = "cheems-";
    private const int RetainedCalendarDays = 7;
    private static readonly object Sync = new();
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CursorTranslator",
        "Logs");
    private static readonly Regex QueryParameterPattern = new(
        @"([?&][^=&#\s]+)=([^&#\s]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex BearerTokenPattern = new(
        @"(?i)(\bBearer\s+)[A-Za-z0-9._~+/-]+=*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SecretAssignmentPattern = new(
        @"(?i)((?:\bapi[\s_-]?key\b|\bkey\b|\baccess[-_ ]?token\b|\btoken\b|\bsecret\b|\bpassword\b|\bauthorization\b)\s*[=:]\s*)(""[^""]*""|'[^']*'|[^\s&,;]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static bool _initialized;
    private static DateOnly? _lastPrunedDate;

    public static string LogDirectoryPath => LogDirectory;

    public static void Initialize()
    {
        lock (Sync)
            EnsureInitialized();
    }

    public static void Info(string category, string message)
        => Write("INFO", category, message, null);

    public static void Warning(string category, string message, Exception? exception = null)
        => Write("WARN", category, message, exception);

    public static void Error(string category, string message, Exception? exception = null)
        => Write("ERROR", category, message, exception);

    private static void Write(string level, string category, string message, Exception? exception)
    {
        lock (Sync)
        {
            try
            {
                if (!EnsureInitialized()) return;

                var now = DateTimeOffset.Now;
                var today = DateOnly.FromDateTime(now.LocalDateTime);
                if (_lastPrunedDate != today)
                    PruneExpiredLogs(today);

                var path = Path.Combine(LogDirectory, $"{FilePrefix}{today:yyyy-MM-dd}.log");
                var content = new StringBuilder()
                    .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
                    .Append(" [").Append(level).Append("] [")
                    .Append(SanitizeSingleLine(category)).Append("] ")
                    .Append(Sanitize(message));

                if (exception is not null)
                    content.AppendLine().Append(FormatException(exception));

                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.WriteLine(content);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            catch (Exception loggingException)
            {
                // Logging must never crash the application or recursively try to log itself.
                Debug.WriteLine($"Unable to write application log: {loggingException}");
            }
        }
    }

    private static bool EnsureInitialized()
    {
        if (_initialized) return true;

        try
        {
            Directory.CreateDirectory(LogDirectory);
            PruneExpiredLogs(DateOnly.FromDateTime(DateTime.Today));
            _initialized = true;
            return true;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Unable to initialize application logging: {exception}");
            return false;
        }
    }

    private static void PruneExpiredLogs(DateOnly today)
    {
        var oldestRetainedDate = today.AddDays(-(RetainedCalendarDays - 1));
        foreach (var path in Directory.EnumerateFiles(LogDirectory, $"{FilePrefix}*.log"))
        {
            var filename = Path.GetFileNameWithoutExtension(path);
            var dateText = filename.AsSpan(FilePrefix.Length);
            if (DateOnly.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var fileDate)
                && fileDate < oldestRetainedDate)
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception exception)
                {
                    Debug.WriteLine($"Unable to prune expired log '{path}': {exception.Message}");
                }
            }
        }

        _lastPrunedDate = today;
    }

    private static string Sanitize(string value)
    {
        var sanitized = QueryParameterPattern.Replace(value, "$1=[REDACTED]");
        sanitized = BearerTokenPattern.Replace(sanitized, "$1[REDACTED]");
        sanitized = SecretAssignmentPattern.Replace(sanitized, "$1[REDACTED]");
        return sanitized.Replace("\r\n", "\n").Replace('\r', '\n');
    }

    private static string FormatException(Exception exception)
    {
        var result = new StringBuilder();
        AppendException(result, exception);
        return Sanitize(result.ToString());
    }

    private static void AppendException(StringBuilder target, Exception exception)
    {
        target.Append(exception.GetType().FullName);
        if (exception is HttpRequestException httpException)
        {
            if (httpException.StatusCode is { } statusCode)
                target.Append(" (HTTP ").Append((int)statusCode).Append(' ').Append(statusCode).Append(')');
            target.AppendLine(" (response message omitted to avoid logging request or response text)");
        }
        else if (exception is AggregateException)
        {
            target.AppendLine();
        }
        else
        {
            target.Append(": ").AppendLine(exception.Message);
        }

        if (!string.IsNullOrWhiteSpace(exception.StackTrace))
            target.AppendLine(exception.StackTrace);

        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
            {
                target.AppendLine("Inner exception:");
                AppendException(target, inner);
            }
        }
        else if (exception.InnerException is { } innerException)
        {
            target.AppendLine("Inner exception:");
            AppendException(target, innerException);
        }
    }

    private static string SanitizeSingleLine(string value)
        => Sanitize(value).Replace('\n', ' ');
}
