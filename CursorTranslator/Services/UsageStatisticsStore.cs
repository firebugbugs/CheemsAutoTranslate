using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using CursorTranslator.Models;

namespace CursorTranslator.Services;

public sealed class UsageStatisticsStore
{
    public const int RetentionDays = 365;
    private const int CurrentSchemaVersion = 1;
    private const string CompletedTranslationEvent = "translation.completed";
    private const string TranslationUnitsMetric = "translation_units";
    private const string SourceCharactersMetric = "source_characters";
    private readonly string _databasePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CursorTranslator",
        "usage-statistics.db");
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private volatile bool _initialized;

    public UsageStatisticsStore()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 5
        }.ToString();
    }

    public string DatabasePath => _databasePath;

    public async Task InitializeAsync()
    {
        if (_initialized) return;

        await _initializationLock.WaitAsync();
        try
        {
            if (_initialized) return;
            await Task.Run(InitializeDatabase);
            _initialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    public async Task RecordCompletedTranslationAsync(string submittedText, TranslationProviderKind provider)
    {
        await InitializeAsync();
        var units = TranslationUnitCounter.Count(submittedText);
        var sourceCharacters = submittedText.EnumerateRunes().LongCount();
        var now = DateTimeOffset.Now;
        var localDate = DateOnly.FromDateTime(now.LocalDateTime);

        await Task.Run(() =>
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using (var insertEvent = connection.CreateCommand())
            {
                insertEvent.Transaction = transaction;
                insertEvent.CommandText = """
                    INSERT INTO usage_events (event_type, occurred_at_utc, local_date, provider, metadata_json)
                    VALUES ($event_type, $occurred_at_utc, $local_date, $provider, NULL);
                    SELECT last_insert_rowid();
                    """;
                insertEvent.Parameters.AddWithValue("$event_type", CompletedTranslationEvent);
                insertEvent.Parameters.AddWithValue("$occurred_at_utc", now.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
                insertEvent.Parameters.AddWithValue("$local_date", localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                insertEvent.Parameters.AddWithValue("$provider", provider.ToString());
                insertEvent.ExecuteNonQuery();
                using var identity = connection.CreateCommand();
                identity.Transaction = transaction;
                identity.CommandText = "SELECT last_insert_rowid();";
                var eventId = Convert.ToInt64(identity.ExecuteScalar(), CultureInfo.InvariantCulture);
                InsertMetric(connection, transaction, eventId, TranslationUnitsMetric, units);
                InsertMetric(connection, transaction, eventId, SourceCharactersMetric, sourceCharacters);
            }

            PruneExpiredEvents(connection, transaction, DateOnly.FromDateTime(DateTime.Today));
            transaction.Commit();
        });
    }

    public async Task<IReadOnlyList<DailyTranslationStatistic>> GetDailyStatisticsAsync(
        DateOnly startDate,
        DateOnly endDate)
    {
        if (startDate > endDate)
            throw new ArgumentException("开始日期不能晚于结束日期。", nameof(startDate));

        await InitializeAsync();
        return await Task.Run<IReadOnlyList<DailyTranslationStatistic>>(() =>
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT e.local_date,
                       COUNT(DISTINCT e.event_id) AS request_count,
                       COALESCE(SUM(CASE WHEN m.metric_name = $translation_units THEN m.metric_value ELSE 0 END), 0) AS translation_units
                FROM usage_events AS e
                LEFT JOIN usage_event_metrics AS m ON m.event_id = e.event_id
                WHERE e.event_type = $event_type
                  AND e.local_date >= $start_date
                  AND e.local_date <= $end_date
                GROUP BY e.local_date
                ORDER BY e.local_date;
                """;
            command.Parameters.AddWithValue("$translation_units", TranslationUnitsMetric);
            command.Parameters.AddWithValue("$event_type", CompletedTranslationEvent);
            command.Parameters.AddWithValue("$start_date", startDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$end_date", endDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

            var daily = new List<DailyTranslationStatistic>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var date = DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture);
                daily.Add(new DailyTranslationStatistic(date, reader.GetInt64(1), reader.GetInt64(2)));
            }
            return daily;
        });
    }

    private void InitializeDatabase()
    {
        using var connection = OpenConnection();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                PRAGMA journal_mode = WAL;
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version INTEGER PRIMARY KEY,
                    applied_at_utc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS usage_events (
                    event_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    event_type TEXT NOT NULL,
                    occurred_at_utc TEXT NOT NULL,
                    local_date TEXT NOT NULL,
                    provider TEXT,
                    metadata_json TEXT
                );
                CREATE TABLE IF NOT EXISTS usage_event_metrics (
                    event_id INTEGER NOT NULL REFERENCES usage_events(event_id) ON DELETE CASCADE,
                    metric_name TEXT NOT NULL,
                    metric_value INTEGER NOT NULL CHECK (metric_value >= 0),
                    PRIMARY KEY (event_id, metric_name)
                );
                CREATE INDEX IF NOT EXISTS ix_usage_events_type_date
                    ON usage_events (event_type, local_date);
                CREATE INDEX IF NOT EXISTS ix_usage_events_occurred_at
                    ON usage_events (occurred_at_utc);
                CREATE INDEX IF NOT EXISTS ix_usage_event_metrics_name_event
                    ON usage_event_metrics (metric_name, event_id);
                INSERT OR IGNORE INTO schema_migrations (version, applied_at_utc)
                    VALUES ($version, $applied_at_utc);
                """;
            command.Parameters.AddWithValue("$version", CurrentSchemaVersion);
            command.Parameters.AddWithValue("$applied_at_utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }

        using var pruneTransaction = connection.BeginTransaction();
        PruneExpiredEvents(connection, pruneTransaction, DateOnly.FromDateTime(DateTime.Today));
        pruneTransaction.Commit();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static void InsertMetric(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long eventId,
        string name,
        long value)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO usage_event_metrics (event_id, metric_name, metric_value)
            VALUES ($event_id, $metric_name, $metric_value);
            """;
        command.Parameters.AddWithValue("$event_id", eventId);
        command.Parameters.AddWithValue("$metric_name", name);
        command.Parameters.AddWithValue("$metric_value", value);
        command.ExecuteNonQuery();
    }

    private static void PruneExpiredEvents(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateOnly today)
    {
        var firstRetainedDate = today.AddDays(-(RetentionDays - 1));
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM usage_events WHERE local_date < $first_retained_date;";
        command.Parameters.AddWithValue("$first_retained_date", firstRetainedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }
}
