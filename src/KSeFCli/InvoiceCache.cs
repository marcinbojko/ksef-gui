using System.Text.Json;

using KSeF.Client.Core.Models.Invoices;

using Microsoft.Data.Sqlite;

namespace KSeFCli;

/// <summary>
/// Persists the last invoice search result per profile in a local SQLite database.
/// KSeF invoices are immutable, so cached data is always valid — the only reason to
/// re-fetch is to discover newly arrived invoices.
/// DB location: ~/.cache/ksefcli/db/invoice-cache.db
/// </summary>
internal sealed class InvoiceCache
{
    private static readonly string DefaultPath =
        Path.Combine(IGlobalCommand.CacheDir, "db", "invoice-cache.db");

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _dbPath;

    public InvoiceCache(string? dbPath = null)
    {
        _dbPath = dbPath ?? DefaultPath;
        bool isNew = !File.Exists(_dbPath);
        string? dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        Log.LogInformation($"Invoice cache DB: {_dbPath} ({(isNew ? "new" : $"{new FileInfo(_dbPath).Length / 1024.0:F1} KB")})");
        EnsureSchema();
    }

    /// <summary>Creates the table if it does not already exist, and logs row counts per profile.</summary>
    private void EnsureSchema()
    {
        using SqliteConnection conn = Open();
        using SqliteCommand createCmd = conn.CreateCommand();
        createCmd.CommandText = """
            CREATE TABLE IF NOT EXISTS invoice_cache (
                profile_key   TEXT PRIMARY KEY,
                params_json   TEXT NOT NULL,
                invoices_json TEXT NOT NULL,
                fetched_at    TEXT NOT NULL
            )
            """;
        createCmd.ExecuteNonQuery();

        using SqliteCommand notifCmd = conn.CreateCommand();
        notifCmd.CommandText = """
            CREATE TABLE IF NOT EXISTS notification_sent (
                profile_key    TEXT NOT NULL,
                ksef_number    TEXT NOT NULL,
                sent_at        TEXT NOT NULL,
                channels_ok    TEXT,
                channels_failed TEXT,
                PRIMARY KEY (profile_key, ksef_number)
            )
            """;
        notifCmd.ExecuteNonQuery();

        // Schema migration: add columns to pre-existing databases.
        // Pre-check via PRAGMA so we only run ALTER when the column is truly absent,
        // avoiding a broad catch that would hide real DB errors (locking, corruption, etc.).
        foreach ((string col, string ddl) in new (string, string)[]
        {
            ("channels_ok",   "channels_ok TEXT"),
            ("channels_failed", "channels_failed TEXT"),
            ("retry_count",   "retry_count INTEGER NOT NULL DEFAULT 0"),
            ("next_retry_at", "next_retry_at TEXT"),
        })
        {
            using SqliteCommand pragmaCmd = conn.CreateCommand();
            pragmaCmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('notification_sent') WHERE name = @col";
            pragmaCmd.Parameters.AddWithValue("@col", col);
            long exists = (long)(pragmaCmd.ExecuteScalar() ?? 0L);
            if (exists == 0)
            {
                using SqliteCommand alter = conn.CreateCommand();
                alter.CommandText = $"ALTER TABLE notification_sent ADD COLUMN {ddl}";
                alter.ExecuteNonQuery();
            }
        }

        // Log per-profile row summary for diagnostics
        using SqliteCommand statsCmd = conn.CreateCommand();
        statsCmd.CommandText = "SELECT profile_key, fetched_at, length(invoices_json) FROM invoice_cache ORDER BY fetched_at DESC";
        using SqliteDataReader reader = statsCmd.ExecuteReader();
        int rows = 0;
        while (reader.Read())
        {
            rows++;
            string key = reader.GetString(0);
            string fetchedAt = reader.GetString(1);
            long bytes = reader.GetInt64(2);
            Log.LogDebug($"  cache row [{rows}]: profile_key={key[..Math.Min(24, key.Length)]}… fetched={fetchedAt} size={bytes / 1024.0:F1} KB");
        }

        if (rows == 0)
        {
            Log.LogInformation("Invoice cache: empty (no entries yet)");
        }
        else
        {
            Log.LogInformation($"Invoice cache: {rows} profile(s) stored");
        }
    }

    /// <summary>
    /// Loads the cached invoices and search params for the given profile key.
    /// Returns (null, null, null) if no cache entry exists or the stored data is corrupt.
    /// </summary>
    public (List<InvoiceSummary>? Invoices, SearchParams? Params, DateTime? FetchedAt) Load(string profileKey)
    {
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT invoices_json, params_json, fetched_at FROM invoice_cache WHERE profile_key = @key";
        cmd.Parameters.AddWithValue("@key", profileKey);
        using SqliteDataReader reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            Log.LogInformation($"[cache] MISS — no entry for profile key {profileKey[..Math.Min(24, profileKey.Length)]}… → will use KSeF API");
            return (null, null, null);
        }

        string invoicesJson = reader.GetString(0);
        string paramsJson = reader.GetString(1);
        string fetchedAtStr = reader.GetString(2);

        try
        {
            List<InvoiceSummary>? invoices = JsonSerializer.Deserialize<List<InvoiceSummary>>(invoicesJson, _jsonOptions);
            SearchParams? searchParams = JsonSerializer.Deserialize<SearchParams>(paramsJson, _jsonOptions);
            DateTime fetchedAt = DateTime.Parse(fetchedAtStr, null, System.Globalization.DateTimeStyles.RoundtripKind);
            Log.LogInformation($"[cache] HIT  — {invoices?.Count ?? 0} invoices from DB (fetched {fetchedAt:u}, {invoicesJson.Length / 1024.0:F1} KB)");
            return (invoices, searchParams, fetchedAt);
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[cache] CORRUPT — deserialization failed for profile '{profileKey}': {ex.Message}. Ignoring stale entry → will use KSeF API");
            return (null, null, null);
        }
    }

    /// <summary>
    /// Persists the invoice list AND search params. Call on manual (user-triggered) searches only.
    /// Cyclic (auto-refresh) searches should call <see cref="SaveInvoicesOnly"/> to avoid
    /// overwriting the user's last explicit search params with whatever the auto-refresh used.
    /// </summary>
    public void Save(string profileKey, SearchParams searchParams, List<InvoiceSummary> invoices)
    {
        string invoicesJson = JsonSerializer.Serialize(invoices);
        string paramsJson = JsonSerializer.Serialize(searchParams);
        string fetchedAt = DateTime.UtcNow.ToString("o");

        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO invoice_cache (profile_key, params_json, invoices_json, fetched_at)
            VALUES (@key, @params, @invoices, @at)
            ON CONFLICT(profile_key) DO UPDATE SET
                params_json   = excluded.params_json,
                invoices_json = excluded.invoices_json,
                fetched_at    = excluded.fetched_at
            """;
        cmd.Parameters.AddWithValue("@key", profileKey);
        cmd.Parameters.AddWithValue("@params", paramsJson);
        cmd.Parameters.AddWithValue("@invoices", invoicesJson);
        cmd.Parameters.AddWithValue("@at", fetchedAt);
        cmd.ExecuteNonQuery();
        Log.LogInformation($"[cache] WRITE — {invoices.Count} invoices + params saved to DB ({invoicesJson.Length / 1024.0:F1} KB, profile key {profileKey[..Math.Min(24, profileKey.Length)]}…)");
    }

    /// <summary>
    /// Updates only the invoice list for the given profile key, leaving params_json untouched.
    /// Call on cyclic (auto-refresh) searches so the user's explicit search params are preserved.
    /// </summary>
    public void SaveInvoicesOnly(string profileKey, List<InvoiceSummary> invoices)
    {
        string invoicesJson = JsonSerializer.Serialize(invoices);
        string fetchedAt = DateTime.UtcNow.ToString("o");

        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        // UPDATE only — if no row exists yet (user never searched manually), skip silently.
        cmd.CommandText = """
            UPDATE invoice_cache
            SET invoices_json = @invoices,
                fetched_at    = @at
            WHERE profile_key = @key
            """;
        cmd.Parameters.AddWithValue("@key", profileKey);
        cmd.Parameters.AddWithValue("@invoices", invoicesJson);
        cmd.Parameters.AddWithValue("@at", fetchedAt);
        int affected = cmd.ExecuteNonQuery();
        if (affected > 0)
        {
            Log.LogInformation($"[cache] WRITE (invoices only) — {invoices.Count} invoices updated in DB ({invoicesJson.Length / 1024.0:F1} KB, profile key {profileKey[..Math.Min(24, profileKey.Length)]}…)");
        }
    }

    /// <summary>
    /// Returns the set of KSeF invoice numbers for which a webhook notification has already been sent
    /// for this profile. Used to avoid duplicate notifications across refresh cycles.
    /// </summary>
    public HashSet<string> LoadNotifiedKsefNumbers(string profileKey)
    {
        PruneNotificationSent();
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ksef_number FROM notification_sent WHERE profile_key = @key";
        cmd.Parameters.AddWithValue("@key", profileKey);
        using SqliteDataReader reader = cmd.ExecuteReader();
        HashSet<string> result = [];
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }
        return result;
    }

    /// <summary>
    /// Marks the given KSeF invoice numbers as notified for this profile so they are not
    /// re-notified in future refresh cycles. Also used to seed the baseline on first run
    /// (without sending any notification) to avoid blasting the full invoice history.
    /// </summary>
    public void MarkAsNotified(string profileKey, IEnumerable<string> ksefNumbers)
    {
        string sentAt = DateTime.UtcNow.ToString("o");
        using SqliteConnection conn = Open();
        using SqliteTransaction tx = conn.BeginTransaction();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR IGNORE INTO notification_sent (profile_key, ksef_number, sent_at)
            VALUES (@key, @ksef, @at)
            """;
        SqliteParameter pKey = cmd.Parameters.Add("@key", SqliteType.Text);
        SqliteParameter pKsef = cmd.Parameters.Add("@ksef", SqliteType.Text);
        SqliteParameter pAt = cmd.Parameters.Add("@at", SqliteType.Text);
        pKey.Value = profileKey;
        pAt.Value = sentAt;
        cmd.Prepare();
        int inserted = 0;
        foreach (string ksefNumber in ksefNumbers)
        {
            pKsef.Value = ksefNumber;
            inserted += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        if (inserted > 0)
        {
            Log.LogDebug($"[notif-sent] Marked {inserted} invoice(s) as notified for profile key {profileKey[..Math.Min(24, profileKey.Length)]}…");
        }
    }

    /// <summary>
    /// Updates the channel delivery outcome columns on already-inserted notification_sent rows.
    /// Call this after firing notification channels to record which succeeded and which failed.
    /// Rows that don't exist yet (e.g. baseline seeding) are silently skipped.
    /// </summary>
    public void UpdateNotifiedChannels(string profileKey, IEnumerable<string> ksefNumbers,
        IReadOnlyList<string> channelsOk, IReadOnlyList<string> channelsFailed)
    {
        string? ok = channelsOk.Count > 0 ? string.Join(",", channelsOk) : null;
        string? failed = channelsFailed.Count > 0 ? string.Join(",", channelsFailed) : null;
        using SqliteConnection conn = Open();
        using SqliteTransaction tx = conn.BeginTransaction();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        // When any channel fails, schedule the first retry in 5 minutes (retry_count = 0).
        // If all channels succeed, clear the retry schedule.
        string? nextRetryAt = failed is not null ? DateTime.UtcNow.AddMinutes(5).ToString("o") : null;
        cmd.CommandText = """
            UPDATE notification_sent
            SET channels_ok    = @ok,
                channels_failed = @failed,
                retry_count    = CASE WHEN @failed IS NOT NULL THEN 0 ELSE retry_count END,
                next_retry_at  = CASE WHEN @failed IS NOT NULL THEN @nextAt ELSE NULL END
            WHERE profile_key = @key AND ksef_number = @ksef
            """;
        SqliteParameter pKey = cmd.Parameters.Add("@key", SqliteType.Text);
        SqliteParameter pKsef = cmd.Parameters.Add("@ksef", SqliteType.Text);
        SqliteParameter pOk = cmd.Parameters.Add("@ok", SqliteType.Text);
        SqliteParameter pFailed = cmd.Parameters.Add("@failed", SqliteType.Text);
        SqliteParameter pNextAt = cmd.Parameters.Add("@nextAt", SqliteType.Text);
        pKey.Value = profileKey;
        pOk.Value = ok is not null ? (object)ok : DBNull.Value;
        pFailed.Value = failed is not null ? (object)failed : DBNull.Value;
        pNextAt.Value = nextRetryAt is not null ? (object)nextRetryAt : DBNull.Value;
        cmd.Prepare();
        foreach (string ksefNumber in ksefNumbers)
        {
            pKsef.Value = ksefNumber;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        string summary = ok is not null ? $"ok=[{ok}]" : "ok=[]";
        if (failed is not null) { summary += $" failed=[{failed}] retry_in=5m"; }
        Log.LogInformation($"[notif-sent] Channel outcomes recorded for profile key {profileKey[..Math.Min(24, profileKey.Length)]}… — {summary}");
    }

    /// <summary>
    /// Deletes notification_sent rows older than <paramref name="retentionDays"/> days.
    /// Call periodically (e.g. at startup or each refresh cycle) to keep the table bounded.
    /// </summary>
    public int PruneNotificationSent(int retentionDays = 90)
    {
        string cutoff = DateTime.UtcNow.AddDays(-retentionDays).ToString("o");
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM notification_sent WHERE sent_at < @cutoff";
        cmd.Parameters.AddWithValue("@cutoff", cutoff);
        int deleted = cmd.ExecuteNonQuery();
        if (deleted > 0)
        {
            Log.LogInformation($"[notif-sent] Pruned {deleted} row(s) older than {retentionDays} days.");
        }
        return deleted;
    }

    /// <summary>
    /// Returns all notification_sent rows for this profile where at least one channel failed and
    /// the row is eligible for retry: retry_count &lt; 3 and next_retry_at has elapsed (or is NULL).
    /// Backoff schedule: initial = 5 min, retry 1 = 10 min, retry 2 = 20 min, then no more.
    /// </summary>
    public List<PendingRetry> LoadPendingRetries(string profileKey)
    {
        string now = DateTime.UtcNow.ToString("o");
        using SqliteConnection conn = Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT ksef_number, channels_ok, channels_failed, retry_count
            FROM notification_sent
            WHERE profile_key    = @key
              AND channels_failed IS NOT NULL
              AND retry_count     < 3
              AND (next_retry_at IS NULL OR next_retry_at <= @now)
            ORDER BY sent_at
            """;
        cmd.Parameters.AddWithValue("@key", profileKey);
        cmd.Parameters.AddWithValue("@now", now);
        using SqliteDataReader reader = cmd.ExecuteReader();
        List<PendingRetry> result = [];
        while (reader.Read())
        {
            string ksefNumber = reader.GetString(0);
            string? channelsOk = reader.IsDBNull(1) ? null : reader.GetString(1);
            string channelsFailed = reader.GetString(2);
            int retryCount = reader.GetInt32(3);
            List<string> failedList = [.. channelsFailed.Split(',').Where(s => !string.IsNullOrEmpty(s))];
            result.Add(new PendingRetry(ksefNumber, channelsOk, failedList, retryCount));
        }
        if (result.Count > 0)
        {
            Log.LogDebug($"[notif-retry] {result.Count} pending retry row(s) for profile key {profileKey[..Math.Min(24, profileKey.Length)]}…");
        }
        return result;
    }

    /// <summary>
    /// Records the outcome of a retry attempt. Merges newly-ok channels with existing ones,
    /// updates still-failed channels, increments retry_count, and schedules the next retry
    /// using exponential backoff (10 min for retry 1, 20 min for retry 2). Stops after 3 retries.
    /// </summary>
    public void UpdateRetryOutcome(string profileKey, IEnumerable<string> ksefNumbers,
        IReadOnlyList<string> channelsOk, IReadOnlyList<string> channelsFailed, int retryCount)
    {
        string? ok = channelsOk.Count > 0 ? string.Join(",", channelsOk) : null;
        string? failed = channelsFailed.Count > 0 ? string.Join(",", channelsFailed) : null;
        // Backoff: retryCount=1 → 10 min, retryCount=2 → 20 min, retryCount=3 → done
        string? nextRetryAt = failed is not null && retryCount < 3
            ? DateTime.UtcNow.AddMinutes(5 * (1 << retryCount)).ToString("o")
            : null;

        using SqliteConnection conn = Open();
        using SqliteTransaction tx = conn.BeginTransaction();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE notification_sent
            SET channels_ok    = @ok,
                channels_failed = @failed,
                retry_count    = @retryCount,
                next_retry_at  = @nextAt
            WHERE profile_key = @key AND ksef_number = @ksef
            """;
        SqliteParameter pKey = cmd.Parameters.Add("@key", SqliteType.Text);
        SqliteParameter pKsef = cmd.Parameters.Add("@ksef", SqliteType.Text);
        SqliteParameter pOk = cmd.Parameters.Add("@ok", SqliteType.Text);
        SqliteParameter pFailed = cmd.Parameters.Add("@failed", SqliteType.Text);
        SqliteParameter pRetry = cmd.Parameters.Add("@retryCount", SqliteType.Integer);
        SqliteParameter pNextAt = cmd.Parameters.Add("@nextAt", SqliteType.Text);
        pKey.Value = profileKey;
        pOk.Value = ok is not null ? (object)ok : DBNull.Value;
        pFailed.Value = failed is not null ? (object)failed : DBNull.Value;
        pRetry.Value = retryCount;
        pNextAt.Value = nextRetryAt is not null ? (object)nextRetryAt : DBNull.Value;
        cmd.Prepare();
        foreach (string ksefNumber in ksefNumbers)
        {
            pKsef.Value = ksefNumber;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        string summary = ok is not null ? $"ok=[{ok}]" : "ok=[]";
        if (failed is not null)
        {
            string backoffLabel = retryCount < 3 ? $"next={5 * (1 << retryCount)}m" : "max-retries-reached";
            summary += $" failed=[{failed}] retry_count={retryCount} {backoffLabel}";
        }
        Log.LogInformation($"[notif-retry] Outcome for profile key {profileKey[..Math.Min(24, profileKey.Length)]}… — {summary}");
    }

    /// <summary>
    /// Returns the most recent notification row for this profile plus a count of rows
    /// pending retry. Used by the /notification-status UI endpoint.
    /// </summary>
    public NotificationStatus LoadNotificationStatus(string profileKey)
    {
        string now = DateTime.UtcNow.ToString("o");
        using SqliteConnection conn = Open();

        using SqliteCommand latestCmd = conn.CreateCommand();
        latestCmd.CommandText = """
            SELECT sent_at, channels_ok, channels_failed, retry_count
            FROM notification_sent
            WHERE profile_key = @key
            ORDER BY sent_at DESC
            LIMIT 1
            """;
        latestCmd.Parameters.AddWithValue("@key", profileKey);
        string? lastSentAt = null, lastOk = null, lastFailed = null;
        int? lastRetryCount = null;
        using (SqliteDataReader reader = latestCmd.ExecuteReader())
        {
            if (reader.Read())
            {
                lastSentAt = reader.IsDBNull(0) ? null : reader.GetString(0);
                lastOk = reader.IsDBNull(1) ? null : reader.GetString(1);
                lastFailed = reader.IsDBNull(2) ? null : reader.GetString(2);
                lastRetryCount = reader.IsDBNull(3) ? null : reader.GetInt32(3);
            }
        }

        using SqliteCommand pendingCmd = conn.CreateCommand();
        pendingCmd.CommandText = """
            SELECT COUNT(DISTINCT ksef_number)
            FROM notification_sent
            WHERE profile_key    = @key
              AND channels_failed IS NOT NULL
              AND retry_count     < 3
              AND (next_retry_at IS NULL OR next_retry_at <= @now)
            """;
        pendingCmd.Parameters.AddWithValue("@key", profileKey);
        pendingCmd.Parameters.AddWithValue("@now", now);
        int pendingRetries = (int)(long)(pendingCmd.ExecuteScalar() ?? 0L);

        return new NotificationStatus(lastSentAt, pendingRetries, lastOk, lastFailed, lastRetryCount);
    }

    private SqliteConnection Open()
    {
        SqliteConnection conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }

    /// <summary>A notification_sent row eligible for retry delivery.</summary>
    public record PendingRetry(
        string KsefNumber,
        string? ChannelsOk,
        List<string> ChannelsFailed,
        int RetryCount);

    /// <summary>Snapshot of the most recent notification state for a profile, used by the UI status badge.</summary>
    public record NotificationStatus(
        string? LastSentAt,
        int PendingRetries,
        string? LastChannelsOk,
        string? LastChannelsFailed,
        int? LastRetryCount);
}
