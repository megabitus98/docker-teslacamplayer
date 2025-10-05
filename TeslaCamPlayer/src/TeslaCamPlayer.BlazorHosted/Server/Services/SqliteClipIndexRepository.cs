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
