using System.Text.Json;

namespace DigitalBanking.Admin.Api.Ops.Data;

/// <summary>
/// Write-ahead log of a province's banking changes, kept as files on the host disk (data/wal/&lt;province&gt;),
/// outside every database container. While a province's standby is down, each change committed on the primary
/// is also appended here, so it survives even if the primary is then lost for good. Before the standby is
/// promoted it replays the log; rows are matched by primary key, so nothing is applied twice.
/// Once the standby catches up through normal replication the log is no longer needed and is cleared.
/// </summary>
public sealed class WriteAheadLog(IConfiguration configuration, IHostEnvironment environment, BankReplicator bank)
{
    private const string StateFile = "state.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    // Repository root /data/wal by default (the admin runs from src/Services/Admin/DigitalBanking.Admin.Api).
    public string Root { get; } = Path.GetFullPath(configuration["WriteAheadLog:Directory"]
        ?? Path.Combine(environment.ContentRootPath, "..", "..", "..", "..", "data", "wal"));

    // Which primary the log follows, the change-tracking version it reached and the number of the next file.
    private sealed record WalState(string Source, long LastVersion, int NextFile);

    public int PendingFiles(string province)
    {
        var folder = Folder(province);
        return Directory.Exists(folder) ? Directory.GetFiles(folder, "*.wal").Length : 0;
    }

    /// <summary>
    /// Appends what the primary committed since the last entry (from startVersion, the version the standby
    /// already holds, when the log is new). Returns the number of rows written.
    /// </summary>
    public async Task<long> CaptureAsync(string province, string sourceDatabase, string sourceConnectionString, long startVersion, CancellationToken cancellationToken)
    {
        var folder = Folder(province);
        Directory.CreateDirectory(folder);
        var state = ReadState(folder);
        if (state is null || !string.Equals(state.Source, sourceDatabase, StringComparison.OrdinalIgnoreCase))
        {
            // A log that follows another node no longer applies: that node is not the primary any more.
            Clear(province);
            state = new WalState(sourceDatabase, startVersion, 1);
            await WriteStateAsync(folder, state, cancellationToken);
        }

        var batch = await bank.ReadChangesSinceAsync(sourceConnectionString, sourceDatabase, state.LastVersion, cancellationToken);
        if (batch is null)
        {
            return 0;
        }

        var rows = batch.Tables.Sum(table => (long)table.Upserts.Count + table.Deletes.Count);
        var next = state.NextFile;
        if (rows > 0)
        {
            // Written to a temporary name first, so a crash never leaves half a file behind.
            var path = Path.Combine(folder, $"{next:D6}.wal");
            await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(batch, Json), cancellationToken);
            File.Move(path + ".tmp", path, overwrite: true);
            next++;
        }

        await WriteStateAsync(folder, state with { LastVersion = batch.ToVersion, NextFile = next }, cancellationToken);
        return rows;
    }

    /// <summary>
    /// Applies every pending entry to the node about to be promoted, then moves the files to applied/&lt;time&gt;
    /// (kept as a record). Returns the source the log followed, the files and rows applied.
    /// </summary>
    public async Task<(string Source, int Files, long Rows)> ReplayAsync(string province, string targetDatabase, string targetConnectionString, CancellationToken cancellationToken)
    {
        var folder = Folder(province);
        var state = ReadState(folder);
        var files = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.wal").Order(StringComparer.Ordinal).ToList() : [];
        if (state is null || files.Count == 0 || string.Equals(state.Source, targetDatabase, StringComparison.OrdinalIgnoreCase))
        {
            return (state?.Source ?? string.Empty, 0, 0);
        }

        var batches = new List<WalBatch>();
        foreach (var file in files)
        {
            batches.Add(JsonSerializer.Deserialize<WalBatch>(await File.ReadAllTextAsync(file, cancellationToken), Json)!);
        }

        var rows = await bank.ApplyWalAsync(targetConnectionString, batches, cancellationToken);
        var archive = Path.Combine(folder, "applied", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(archive);
        foreach (var file in files.Append(Path.Combine(folder, StateFile)).Where(File.Exists))
        {
            File.Move(file, Path.Combine(archive, Path.GetFileName(file)), overwrite: true);
        }

        return (state.Source, files.Count, rows);
    }

    /// <summary>The standby received everything up to version from this source: the log is no longer needed.</summary>
    public bool ClearIfCaughtUp(string province, string sourceDatabase, long version)
    {
        var state = ReadState(Folder(province));
        if (state is null || !string.Equals(state.Source, sourceDatabase, StringComparison.OrdinalIgnoreCase) || version < state.LastVersion)
        {
            return false;
        }

        Clear(province);
        return true;
    }

    private void Clear(string province)
    {
        var folder = Folder(province);
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (var file in Directory.GetFiles(folder, "*.wal").Append(Path.Combine(folder, StateFile)).Where(File.Exists))
        {
            File.Delete(file);
        }
    }

    private string Folder(string province) => Path.Combine(Root, province);

    private static WalState? ReadState(string folder)
    {
        var path = Path.Combine(folder, StateFile);
        return File.Exists(path) ? JsonSerializer.Deserialize<WalState>(File.ReadAllText(path), Json) : null;
    }

    private static async Task WriteStateAsync(string folder, WalState state, CancellationToken cancellationToken)
    {
        var path = Path.Combine(folder, StateFile);
        await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(state, Json), cancellationToken);
        File.Move(path + ".tmp", path, overwrite: true);
    }
}
