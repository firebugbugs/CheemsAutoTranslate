using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
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

    public static string GetRecentLogsText(int maxCharacters = 512_000)
    {
        const string heading = "Cheems翻译 · 最近 7 天开发日志";
        const string truncatedNotice = "较早的日志过多，已省略；以下为最近记录。";
        if (maxCharacters < 1) throw new ArgumentOutOfRangeException(nameof(maxCharacters));

        lock (Sync)
        {
            if (!Directory.Exists(LogDirectory)) return $"{heading}{Environment.NewLine}没有可复制的日志。";

            var today = DateOnly.FromDateTime(DateTime.Today);
            var oldestDate = today.AddDays(-(RetainedCalendarDays - 1));
            var files = Directory.EnumerateFiles(LogDirectory, $"{FilePrefix}*.log")
                .Select(path =>
                {
                    var name = Path.GetFileNameWithoutExtension(path);
                    return DateOnly.TryParseExact(name.AsSpan(FilePrefix.Length), "yyyy-MM-dd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                        ? (Path: path, Date: date)
                        : default;
                })
                .Where(file => file.Path is not null && file.Date >= oldestDate && file.Date <= today)
                .OrderByDescending(file => file.Date)
                .ToArray();

            if (files.Length == 0) return $"{heading}{Environment.NewLine}最近 7 天没有日志。";

            var chunks = new List<string>();
            var remaining = Math.Max(0, maxCharacters - heading.Length - truncatedNotice.Length - 64);
            var truncated = false;
            foreach (var file in files)
            {
                if (remaining <= 0)
                {
                    truncated = true;
                    break;
                }

                var content = File.ReadAllText(file.Path!);
                if (string.IsNullOrEmpty(content)) continue;

                var dateHeading = $"===== {file.Date:yyyy-MM-dd} ====={Environment.NewLine}";
                var availableContent = remaining - dateHeading.Length;
                if (content.Length <= availableContent)
                {
                    chunks.Add(dateHeading + content);
                    remaining -= dateHeading.Length + content.Length;
                    continue;
                }

                truncated = true;
                if (availableContent > 0)
                {
                    var start = content.Length - availableContent;
                    if (start > 0)
                    {
                        var nextLine = content.IndexOf('\n', start);
                        if (nextLine >= 0) start = nextLine + 1;
                    }
                    var tail = content[start..].TrimStart('\r', '\n');
                    if (!string.IsNullOrEmpty(tail))
                        chunks.Add(dateHeading + tail);
                }
                break;
            }

            chunks.Reverse();
            var result = new StringBuilder(heading).AppendLine().AppendLine();
            if (truncated) result.AppendLine(truncatedNotice);
            foreach (var chunk in chunks) result.AppendLine(chunk);
            return result.ToString();
        }
    }

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
        target.Append(" (HRESULT 0x")
            .Append(unchecked((uint)exception.HResult).ToString("X8", CultureInfo.InvariantCulture))
            .Append(')');
        if (exception is HttpRequestException httpException)
        {
            target.Append(" (request error: ").Append(httpException.HttpRequestError).Append(')');
            if (httpException.StatusCode is { } statusCode)
                target.Append(" (HTTP ").Append((int)statusCode).Append(' ').Append(statusCode).Append(')');
            target.AppendLine(" (response message omitted to avoid logging request or response text)");
        }
        else if (exception is SocketException socketException)
        {
            target.Append(" (socket error: ").Append(socketException.SocketErrorCode)
                .Append(", native code: ").Append(socketException.NativeErrorCode).AppendLine(")");
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
