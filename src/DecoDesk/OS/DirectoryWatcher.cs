using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using DecoDesk.Database;
using DecoDesk.IO;

namespace DecoDesk.OS;

/// <summary>
/// 指定されたディレクトリ下の画像ファイルの変更（追加・削除・リネーム）を監視し、
/// SQLite データベースと非同期で自動同期するクラスです。
/// </summary>
public sealed class DirectoryWatcher : IDisposable
{
    private static readonly ILogger Logger = AppLoggerFactory.CreateLogger<DirectoryWatcher>();

    private readonly DatabaseManager _dbManager;
    private readonly string _watchDirectory;
    private FileSystemWatcher? _watcher;
    private bool _disposed;

    private static readonly HashSet<string> TargetExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png"
    };

    /// <summary>
    /// <see cref="DirectoryWatcher"/> クラスの新しいインスタンスを初期化します。
    /// </summary>
    /// <param name="dbManager">データベース接続を管理するマネージャー</param>
    /// <param name="watchDirectory">監視する画像フォルダの絶対パス</param>
    public DirectoryWatcher(DatabaseManager dbManager, string watchDirectory)
    {
        _dbManager = dbManager ?? throw new ArgumentNullException(nameof(dbManager));
        _watchDirectory = watchDirectory;
    }

    /// <summary>
    /// ディレクトリ監視（自動同期）を開始します。
    /// </summary>
    public void Start()
    {
        if (_disposed) return;
        if (string.IsNullOrWhiteSpace(_watchDirectory) || !Directory.Exists(_watchDirectory))
        {
            Logger.LogWarning("監視対象ディレクトリが無効なため、DirectoryWatcher を開始しません。パス={Path}", _watchDirectory);
            return;
        }

        try
        {
            _watcher = new FileSystemWatcher(_watchDirectory)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                Filter = "*.*",
                IncludeSubdirectories = true,
                EnableRaisingEvents = true
            };

            _watcher.Created += OnCreated;
            _watcher.Deleted += OnDeleted;
            _watcher.Renamed += OnRenamed;

            Logger.LogInformation("フォルダリアルタイム同期を開始しました。対象={Path}", _watchDirectory);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "DirectoryWatcher の開始中にエラーが発生しました。パス={Path}", _watchDirectory);
        }
    }

    /// <summary>
    /// ディレクトリ監視（自動同期）を停止し、リソースを解放します。
    /// </summary>
    public void Stop()
    {
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnCreated;
            _watcher.Deleted -= OnDeleted;
            _watcher.Renamed -= OnRenamed;
            _watcher.Dispose();
            _watcher = null;
            Logger.LogInformation("フォルダリアルタイム同期を停止しました。");
        }
    }

    private void OnCreated(object sender, FileSystemEventArgs e)
    {
        if (!IsTargetImageFile(e.FullPath)) return;
        Logger.LogInformation("新規ファイルを検知しました。Path={FilePath}", e.FullPath);
        
        _ = Task.Run(async () =>
        {
            try
            {
                if (await WaitForFileAccessAsync(e.FullPath))
                {
                    var size = ImageHeaderParser.GetImageSize(e.FullPath);
                    if (size == null)
                    {
                        Logger.LogWarning("新規検知したファイルのメタデータ取得に失敗しました。Path={FilePath}", e.FullPath);
                        return;
                    }

                    var (width, height) = size.Value;
                    if (width <= 0 || height <= 0) return;

                    double aspectRatio = (double)width / height;

                    using var connection = _dbManager.CreateConnection();
                    await connection.OpenAsync();
                    using var command = connection.CreateCommand();
                    command.CommandText = @"
                        INSERT OR REPLACE INTO Images (Path, Width, Height, AspectRatio, LastUsed)
                        VALUES (@Path, @Width, @Height, @AspectRatio, 0);
                    ";
                    command.Parameters.AddWithValue("@Path", e.FullPath);
                    command.Parameters.AddWithValue("@Width", width);
                    command.Parameters.AddWithValue("@Height", height);
                    command.Parameters.AddWithValue("@AspectRatio", aspectRatio);

                    await command.ExecuteNonQueryAsync();
                    Logger.LogInformation("新規画像をデータベースに同期しました。Path={FilePath}, Size={Width}x{Height}", e.FullPath, width, height);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "新規ファイルのDB同期中に例外が発生しました。Path={FilePath}", e.FullPath);
            }
        });
    }

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        if (!IsTargetImageFile(e.FullPath)) return;
        Logger.LogInformation("ファイルの削除を検知しました。Path={FilePath}", e.FullPath);

        _ = Task.Run(async () =>
        {
            try
            {
                using var connection = _dbManager.CreateConnection();
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM Images WHERE Path = @Path;";
                command.Parameters.AddWithValue("@Path", e.FullPath);

                int rows = await command.ExecuteNonQueryAsync();
                if (rows > 0)
                {
                    Logger.LogInformation("削除された画像をデータベースから除外しました。Path={FilePath}", e.FullPath);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "ファイル削除のDB同期中に例外が発生しました。Path={FilePath}", e.FullPath);
            }
        });
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        bool wasTarget = IsTargetImageFile(e.OldFullPath);
        bool isTarget = IsTargetImageFile(e.FullPath);

        if (!wasTarget && !isTarget) return;

        Logger.LogInformation("ファイル名の変更を検知しました。OldPath={OldPath}, NewPath={NewPath}", e.OldFullPath, e.FullPath);

        _ = Task.Run(async () =>
        {
            try
            {
                using var connection = _dbManager.CreateConnection();
                await connection.OpenAsync();

                if (wasTarget && !isTarget)
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = "DELETE FROM Images WHERE Path = @Path;";
                    command.Parameters.AddWithValue("@Path", e.OldFullPath);
                    await command.ExecuteNonQueryAsync();
                    Logger.LogInformation("対象外となったため画像をDBから削除しました。Path={FilePath}", e.OldFullPath);
                }
                else if (!wasTarget && isTarget)
                {
                    if (await WaitForFileAccessAsync(e.FullPath))
                    {
                        var size = ImageHeaderParser.GetImageSize(e.FullPath);
                        if (size == null) return;
                        var (width, height) = size.Value;
                        if (width <= 0 || height <= 0) return;
                        double aspectRatio = (double)width / height;

                        using var command = connection.CreateCommand();
                        command.CommandText = @"
                            INSERT OR REPLACE INTO Images (Path, Width, Height, AspectRatio, LastUsed)
                            VALUES (@Path, @Width, @Height, @AspectRatio, 0);
                        ";
                        command.Parameters.AddWithValue("@Path", e.FullPath);
                        command.Parameters.AddWithValue("@Width", width);
                        command.Parameters.AddWithValue("@Height", height);
                        command.Parameters.AddWithValue("@AspectRatio", aspectRatio);
                        await command.ExecuteNonQueryAsync();
                        Logger.LogInformation("新規対象となった画像をDBに登録しました。Path={FilePath}", e.FullPath);
                    }
                }
                else
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = "UPDATE Images SET Path = @NewPath WHERE Path = @OldPath;";
                    command.Parameters.AddWithValue("@NewPath", e.FullPath);
                    command.Parameters.AddWithValue("@OldPath", e.OldFullPath);

                    int rows = await command.ExecuteNonQueryAsync();
                    if (rows > 0)
                    {
                        Logger.LogInformation("データベース内の画像パスを更新しました。Old={Old}, New={New}", e.OldFullPath, e.FullPath);
                    }
                    else
                    {
                        if (await WaitForFileAccessAsync(e.FullPath))
                        {
                            var size = ImageHeaderParser.GetImageSize(e.FullPath);
                            if (size != null)
                            {
                                var (width, height) = size.Value;
                                double aspectRatio = (double)width / height;
                                using var insertCmd = connection.CreateCommand();
                                insertCmd.CommandText = @"
                                    INSERT OR REPLACE INTO Images (Path, Width, Height, AspectRatio, LastUsed)
                                    VALUES (@Path, @Width, @Height, @AspectRatio, 0);
                                ";
                                insertCmd.Parameters.AddWithValue("@Path", e.FullPath);
                                insertCmd.Parameters.AddWithValue("@Width", width);
                                insertCmd.Parameters.AddWithValue("@Height", height);
                                insertCmd.Parameters.AddWithValue("@AspectRatio", aspectRatio);
                                await insertCmd.ExecuteNonQueryAsync();
                                Logger.LogInformation("データベースに新規登録しました。Path={FilePath}", e.FullPath);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "ファイル名変更のDB同期中に例外が発生しました。Path={FilePath}", e.FullPath);
            }
        });
    }

    private bool IsTargetImageFile(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return TargetExtensions.Contains(ext);
    }

    private async Task<bool> WaitForFileAccessAsync(string filePath)
    {
        int retries = 15;
        int delay = 200;
        for (int i = 0; i < retries; i++)
        {
            try
            {
                if (!File.Exists(filePath)) return false;
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.None);
                return true;
            }
            catch (IOException)
            {
                await Task.Delay(delay);
            }
        }
        Logger.LogWarning("ファイルの書き込み完了（ロック解除）をタイムアウトしました。Path={FilePath}", filePath);
        return false;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
