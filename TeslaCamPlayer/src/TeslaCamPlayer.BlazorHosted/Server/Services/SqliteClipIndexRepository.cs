using Microsoft.Data.Sqlite;
using Serilog;
using System.Threading;
using TeslaCamPlayer.BlazorHosted.Server.Providers.Interfaces;
using TeslaCamPlayer.BlazorHosted.Server.Services.Interfaces;
using TeslaCamPlayer.BlazorHosted.Shared.Models;

namespace TeslaCamPlayer.BlazorHosted.Server.Services;

public class SqliteClipIndexRepository : IClipIndexRepository
{
    private readonly ISettingsProvider _settingsProvider;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private bool _initialized;

    public SqliteClipIndexRepository(ISettingsProvider settingsProvider)
    {
        _settingsProvider = settingsProvider;
    }

    private string DatabasePath => _settingsProvider.Settings.CacheDatabasePath ?? Path.Combine(AppContext.BaseDirectory, "clips.db");

    private string ConnectionString
    {
        get
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared
            };
            return builder.ToString();
        }
    }

    public async Task<IReadOnlyList<VideoFile>> LoadVideoFilesAsync()
    {
        await EnsureInitializedAsync();

        var results = new List<VideoFile>();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = @"SELECT file_path, event_folder, clip_type, start_ticks, camera, duration_ticks FROM video_files";

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var filePath = reader.GetString(0);
            var eventFolder = reader.IsDBNull(1) ? null : reader.GetString(1);
            var clipType = (ClipType)reader.GetInt32(2);
            var startTicks = reader.GetInt64(3);
            var camera = (Cameras)reader.GetInt32(4);
            var durationTicks = reader.GetInt64(5);

            results.Add(new VideoFile
            {
                FilePath = filePath,
                Url = $"/Api/Video/{Uri.EscapeDataString(filePath)}",
                EventFolderName = eventFolder,
                ClipType = clipType,
                StartDate = new DateTime(startTicks),
                Camera = camera,
                Duration = TimeSpan.FromTicks(durationTicks)
            });
        }

        return results;
    }

    public async Task ResetAsync()
    {
        await EnsureInitializedAsync();

        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM video_files";
        await command.ExecuteNonQueryAsync();
    }

    public async Task UpsertVideoFilesAsync(IEnumerable<VideoFile> videoFiles)
    {
        if (videoFiles == null)
        {
            return;
        }

        var list = videoFiles.Where(v => v != null).ToList();
        if (list.Count == 0)
        {
            return;
        }

        await EnsureInitializedAsync();

        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var sqliteTransaction = (SqliteTransaction)transaction;

        await using var command = connection.CreateCommand();
        command.Transaction = sqliteTransaction;
        command.CommandText = @"INSERT INTO video_files (
                file_path,
                directory_path,
                event_folder,
                clip_type,
                start_ticks,
                camera,
                duration_ticks)
            VALUES (
                $file_path,
                $directory_path,
                $event_folder,
                $clip_type,
                $start_ticks,
                $camera,
                $duration_ticks)
            ON CONFLICT(file_path) DO UPDATE SET
                directory_path = excluded.directory_path,
                event_folder = excluded.event_folder,
                clip_type = excluded.clip_type,
                start_ticks = excluded.start_ticks,
                camera = excluded.camera,
                duration_ticks = excluded.duration_ticks";

        var filePathParam = command.CreateParameter();
        filePathParam.ParameterName = "$file_path";
        command.Parameters.Add(filePathParam);

        var directoryParam = command.CreateParameter();
        directoryParam.ParameterName = "$directory_path";
        command.Parameters.Add(directoryParam);

        var eventFolderParam = command.CreateParameter();
        eventFolderParam.ParameterName = "$event_folder";
        command.Parameters.Add(eventFolderParam);

        var clipTypeParam = command.CreateParameter();
        clipTypeParam.ParameterName = "$clip_type";
        command.Parameters.Add(clipTypeParam);

        var startTicksParam = command.CreateParameter();
        startTicksParam.ParameterName = "$start_ticks";
        command.Parameters.Add(startTicksParam);

        var cameraParam = command.CreateParameter();
        cameraParam.ParameterName = "$camera";
        command.Parameters.Add(cameraParam);

        var durationParam = command.CreateParameter();
        durationParam.ParameterName = "$duration_ticks";
        command.Parameters.Add(durationParam);

        foreach (var videoFile in list)
        {
            filePathParam.Value = videoFile.FilePath;
            var directoryPath = Path.GetDirectoryName(videoFile.FilePath);
            directoryParam.Value = directoryPath ?? (object)DBNull.Value;
            eventFolderParam.Value = videoFile.EventFolderName ?? (object)DBNull.Value;
            clipTypeParam.Value = (int)videoFile.ClipType;
            startTicksParam.Value = videoFile.StartDate.Ticks;
            cameraParam.Value = (int)videoFile.Camera;
            durationParam.Value = videoFile.Duration.Ticks;

            await command.ExecuteNonQueryAsync();
        }

        await sqliteTransaction.CommitAsync();
    }

    public async Task RemoveByDirectoriesAsync(IEnumerable<string> directories)
    {
        if (directories == null)
        {
            return;
        }

        var unique = directories
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(NormalizeDirectory)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (unique.Count == 0)
        {
            return;
        }

        await EnsureInitializedAsync();

        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var sqliteTransaction = (SqliteTransaction)transaction;

        await using var command = connection.CreateCommand();
        command.Transaction = sqliteTransaction;
        command.CommandText = "DELETE FROM video_files WHERE directory_path = $directory";

        var directoryParam = command.CreateParameter();
        directoryParam.ParameterName = "$directory";
        command.Parameters.Add(directoryParam);

        foreach (var directory in unique)
        {
            directoryParam.Value = directory;
            await command.ExecuteNonQueryAsync();
        }

        await sqliteTransaction.CommitAsync();
    }

    public async Task<IReadOnlyList<EventFolderInfo>> GetDistinctEventFoldersPagedAsync(
        int skip,
        int take,
        ClipType[]? clipTypes = null,
        DateTime? fromDate = null,
        DateTime? toDate = null)
    {
        await EnsureInitializedAsync();

        var results = new List<EventFolderInfo>();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();

        var whereClauses = new List<string>();
        if (clipTypes is { Length: > 0 })
        {
            var typeParams = string.Join(", ", clipTypes.Select((_, i) => $"$type{i}"));
            whereClauses.Add($"clip_type IN ({typeParams})");
            for (int i = 0; i < clipTypes.Length; i++)
            {
                var param = command.CreateParameter();
                param.ParameterName = $"$type{i}";
                param.Value = (int)clipTypes[i];
                command.Parameters.Add(param);
            }
        }

        if (fromDate.HasValue)
        {
            whereClauses.Add("start_ticks >= $fromTicks");
            var param = command.CreateParameter();
            param.ParameterName = "$fromTicks";
            param.Value = fromDate.Value.Ticks;
            command.Parameters.Add(param);
        }

        if (toDate.HasValue)
        {
            whereClauses.Add("start_ticks <= $toTicks");
            var param = command.CreateParameter();
            param.ParameterName = "$toTicks";
            param.Value = toDate.Value.Ticks;
            command.Parameters.Add(param);
        }

        var whereClause = whereClauses.Count > 0 ? $"WHERE {string.Join(" AND ", whereClauses)}" : "";

        command.CommandText = $@"
            SELECT event_folder, directory_path, clip_type, MAX(start_ticks) as latest_ticks
            FROM video_files
            {whereClause}
            GROUP BY event_folder
            ORDER BY latest_ticks DESC
            LIMIT $take OFFSET $skip";

        var skipParam = command.CreateParameter();
        skipParam.ParameterName = "$skip";
        skipParam.Value = skip;
        command.Parameters.Add(skipParam);

        var takeParam = command.CreateParameter();
        takeParam.ParameterName = "$take";
        takeParam.Value = take;
        command.Parameters.Add(takeParam);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new EventFolderInfo
            {
                EventFolder = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                DirectoryPath = reader.IsDBNull(1) ? null : reader.GetString(1),
                ClipType = (ClipType)reader.GetInt32(2),
                LatestTicks = reader.GetInt64(3)
            });
        }

        return results;
    }

    public async Task<IReadOnlyList<VideoFile>> LoadVideoFilesByEventFoldersAsync(IEnumerable<string> eventFolders)
    {
        var folderList = eventFolders?.ToList();
        if (folderList == null || folderList.Count == 0)
        {
            return Array.Empty<VideoFile>();
        }

        await EnsureInitializedAsync();

        var results = new List<VideoFile>();
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();

        var folderParams = string.Join(", ", folderList.Select((_, i) => $"$folder{i}"));
        command.CommandText = $@"
            SELECT file_path, event_folder, clip_type, start_ticks, camera, duration_ticks
            FROM video_files
            WHERE event_folder IN ({folderParams})
            ORDER BY event_folder, start_ticks";

        for (int i = 0; i < folderList.Count; i++)
        {
            var param = command.CreateParameter();
            param.ParameterName = $"$folder{i}";
            param.Value = folderList[i] ?? (object)DBNull.Value;
            command.Parameters.Add(param);
        }

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var filePath = reader.GetString(0);
            var eventFolder = reader.IsDBNull(1) ? null : reader.GetString(1);
            var clipType = (ClipType)reader.GetInt32(2);
            var startTicks = reader.GetInt64(3);
            var camera = (Cameras)reader.GetInt32(4);
            var durationTicks = reader.GetInt64(5);

            results.Add(new VideoFile
            {
                FilePath = filePath,
                Url = $"/Api/Video/{Uri.EscapeDataString(filePath)}",
                EventFolderName = eventFolder,
                ClipType = clipType,
                StartDate = new DateTime(startTicks),
                Camera = camera,
                Duration = TimeSpan.FromTicks(durationTicks)
            });
        }

        return results;
    }

    public async Task<int> GetTotalEventCountAsync(
        ClipType[]? clipTypes = null,
        DateTime? fromDate = null,
        DateTime? toDate = null)
    {
        await EnsureInitializedAsync();

        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();

        var whereClauses = new List<string>();
        if (clipTypes is { Length: > 0 })
        {
            var typeParams = string.Join(", ", clipTypes.Select((_, i) => $"$type{i}"));
            whereClauses.Add($"clip_type IN ({typeParams})");
            for (int i = 0; i < clipTypes.Length; i++)
            {
                var param = command.CreateParameter();
                param.ParameterName = $"$type{i}";
                param.Value = (int)clipTypes[i];
                command.Parameters.Add(param);
            }
        }

        if (fromDate.HasValue)
        {
            whereClauses.Add("start_ticks >= $fromTicks");
            var param = command.CreateParameter();
            param.ParameterName = "$fromTicks";
            param.Value = fromDate.Value.Ticks;
            command.Parameters.Add(param);
        }

        if (toDate.HasValue)
        {
            whereClauses.Add("start_ticks <= $toTicks");
            var param = command.CreateParameter();
            param.ParameterName = "$toTicks";
            param.Value = toDate.Value.Ticks;
            command.Parameters.Add(param);
        }

        var whereClause = whereClauses.Count > 0 ? $"WHERE {string.Join(" AND ", whereClauses)}" : "";

        command.CommandText = $@"
            SELECT COUNT(DISTINCT event_folder)
            FROM video_files
            {whereClause}";

        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    public async Task<IReadOnlyList<DateTime>> GetAvailableDatesAsync(ClipType[]? clipTypes = null)
    {
        await EnsureInitializedAsync();

        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();

        var whereClause = "";
        if (clipTypes is { Length: > 0 })
        {
            var typeParams = string.Join(", ", clipTypes.Select((_, i) => $"$type{i}"));
            whereClause = $"WHERE clip_type IN ({typeParams})";
            for (int i = 0; i < clipTypes.Length; i++)
            {
                var param = command.CreateParameter();
                param.ParameterName = $"$type{i}";
                param.Value = (int)clipTypes[i];
                command.Parameters.Add(param);
            }
        }

        // Get distinct dates by truncating ticks to day boundary
        // SQLite doesn't have native date functions for ticks, so we compute day boundaries
        command.CommandText = $@"
            SELECT DISTINCT (start_ticks / 864000000000) * 864000000000 as day_ticks
            FROM video_files
            {whereClause}
            ORDER BY day_ticks DESC";

        var results = new List<DateTime>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var dayTicks = reader.GetInt64(0);
            results.Add(new DateTime(dayTicks));
        }

        return results;
    }

    private async Task EnsureInitializedAsync()
    {
        if (_initialized)
        {
            return;
        }

        await _initializationGate.WaitAsync();
        try
        {
            if (_initialized)
            {
                return;
            }

            var directory = Path.GetDirectoryName(DatabasePath);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();

            await using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL";
                await pragma.ExecuteNonQueryAsync();
            }

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = @"CREATE TABLE IF NOT EXISTS video_files (
                        file_path TEXT PRIMARY KEY,
                        directory_path TEXT,
                        event_folder TEXT,
                        clip_type INTEGER NOT NULL,
                        start_ticks INTEGER NOT NULL,
                        camera INTEGER NOT NULL,
                        duration_ticks INTEGER NOT NULL
                    )";
                await command.ExecuteNonQueryAsync();
            }

            // Create indexes for pagination and filtering performance
            await using (var indexCommand = connection.CreateCommand())
            {
                indexCommand.CommandText = @"
                    CREATE INDEX IF NOT EXISTS idx_video_files_clip_type ON video_files(clip_type);
                    CREATE INDEX IF NOT EXISTS idx_video_files_start_ticks ON video_files(start_ticks DESC);
                    CREATE INDEX IF NOT EXISTS idx_video_files_type_date ON video_files(clip_type, start_ticks DESC);
                    CREATE INDEX IF NOT EXISTS idx_video_files_event_folder ON video_files(event_folder);
                    CREATE INDEX IF NOT EXISTS idx_video_files_directory_path ON video_files(directory_path);
                ";
                await indexCommand.ExecuteNonQueryAsync();
            }

            _initialized = true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize clip index database at {DatabasePath}", DatabasePath);
            throw;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private static string NormalizeDirectory(string path)
    {
        var normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return normalized.TrimEnd(Path.DirectorySeparatorChar);
    }
}
