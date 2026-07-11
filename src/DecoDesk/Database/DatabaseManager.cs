using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace DecoDesk.Database;

/// <summary>
/// SQLite データベースへの接続管理、テーブル初期化、およびクエリ実行を担当するクラスです。
/// </summary>
public sealed class DatabaseManager
{
    private readonly string _dbPath;
    private readonly string _connectionString;

    /// <summary>
    /// デフォルトのパス（%LOCALAPPDATA%/DecoDesk/decodesk.db）を使用して <see cref="DatabaseManager"/> を初期化します。
    /// </summary>
    public DatabaseManager() : this(GetDefaultDbPath())
    {
    }

    /// <summary>
    /// 指定されたデータベースパスを使用して <see cref="DatabaseManager"/> を初期化します。
    /// </summary>
    /// <param name="dbPath">データベースファイルの絶対パス</param>
    public DatabaseManager(string dbPath)
    {
        if (string.IsNullOrWhiteSpace(dbPath))
        {
            throw new ArgumentException("データベースパスを空にすることはできません。", nameof(dbPath));
        }

        _dbPath = dbPath;
        
        // 接続文字列の構築
        // 高速化のため、デフォルトでWALモードやジャーナル設定を反映できるように設定
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };
        _connectionString = builder.ToString();
    }

    /// <summary>
    /// デフォルトのデータベースパスを取得します。
    /// </summary>
    private static string GetDefaultDbPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appDir = Path.Combine(localAppData, "DecoDesk");
        return Path.Combine(appDir, "decodesk.db");
    }

    /// <summary>
    /// データベースファイルと必要なテーブル・インデックスを初期化します。
    /// </summary>
    public async Task InitializeAsync()
    {
        // データベースディレクトリの作成
        var dir = Path.GetDirectoryName(_dbPath);
        if (dir != null && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var connection = CreateConnection();
        await connection.OpenAsync();

        // WALモードと同期モードの設定による書き込みパフォーマンス最適化
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";
            await command.ExecuteNonQueryAsync();
        }

        // テーブル定義の作成
        using (var command = connection.CreateCommand())
        {
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS Images (
                    Path TEXT PRIMARY KEY,
                    Width INTEGER NOT NULL,
                    Height INTEGER NOT NULL,
                    AspectRatio REAL NOT NULL,
                    LastUsed INTEGER NOT NULL,
                    IsFavorite INTEGER NOT NULL DEFAULT 0
                );
                
                CREATE INDEX IF NOT EXISTS IX_Images_AspectRatio ON Images(AspectRatio);
                CREATE INDEX IF NOT EXISTS IX_Images_LastUsed ON Images(LastUsed);
            ";
            await command.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// データベースへの新しい接続オブジェクトを生成します。
    /// 接続を開く責任は呼び出し側にあります。
    /// </summary>
    /// <returns>初期化済みの <see cref="SqliteConnection"/></returns>
    public SqliteConnection CreateConnection()
    {
        return new SqliteConnection(_connectionString);
    }

    /// <summary>
    /// 画像メタデータを表すレコード構造体です。
    /// </summary>
    public record ImageRecord(string Path, int Width, int Height, double AspectRatio, long LastUsed, bool IsFavorite);

    /// <summary>
    /// 指定されたアスペクト比や使用状況の条件を満たす画像を、インデックスを活用して高速に1件ランダム抽出します。
    /// </summary>
    /// <param name="minAspectRatio">最小アスペクト比（Null許容）</param>
    /// <param name="maxAspectRatio">最大アスペクト比（Null許容）</param>
    /// <param name="maxLastUsed">この日時以前に使用された画像を対象とする閾値（Null許容）</param>
    /// <returns>条件を満たす画像メタデータ。見つからない場合は null</returns>
    public async Task<ImageRecord?> GetRandomImageAsync(double? minAspectRatio, double? maxAspectRatio, DateTime? maxLastUsed)
    {
        using var connection = CreateConnection();
        await connection.OpenAsync();

        // 1. 条件文の動的構築
        var whereClause = "WHERE 1=1";
        if (minAspectRatio.HasValue) whereClause += " AND AspectRatio >= @MinRatio";
        if (maxAspectRatio.HasValue) whereClause += " AND AspectRatio <= @MaxRatio";
        if (maxLastUsed.HasValue) whereClause += " AND LastUsed <= @MaxLastUsed";

        // 2. 件数(N)の高速取得
        int count = 0;
        using (var countCommand = connection.CreateCommand())
        {
            countCommand.CommandText = $"SELECT COUNT(*) FROM Images {whereClause}";
            BindParameters(countCommand, minAspectRatio, maxAspectRatio, maxLastUsed);
            
            var result = await countCommand.ExecuteScalarAsync();
            if (result != null)
            {
                count = Convert.ToInt32(result);
            }
        }

        if (count == 0)
        {
            return null;
        }

        // 3. 乱数 R ([0, N-1]) の決定
        var random = new Random();
        int offset = random.Next(count);

        // 4. LIMIT 1 OFFSET R による高速レコード取得
        using (var selectCommand = connection.CreateCommand())
        {
            selectCommand.CommandText = $@"
                SELECT Path, Width, Height, AspectRatio, LastUsed, IsFavorite 
                FROM Images 
                {whereClause} 
                LIMIT 1 OFFSET @Offset";
            
            BindParameters(selectCommand, minAspectRatio, maxAspectRatio, maxLastUsed);
            selectCommand.Parameters.AddWithValue("@Offset", offset);

            using var reader = await selectCommand.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return new ImageRecord(
                    reader.GetString(0),
                    reader.GetInt32(1),
                    reader.GetInt32(2),
                    reader.GetDouble(3),
                    reader.GetInt64(4),
                    reader.GetInt32(5) != 0
                );
            }
        }

        return null;
    }

    private static void BindParameters(SqliteCommand command, double? minAspectRatio, double? maxAspectRatio, DateTime? maxLastUsed)
    {
        if (minAspectRatio.HasValue)
        {
            command.Parameters.AddWithValue("@MinRatio", minAspectRatio.Value);
        }
        if (maxAspectRatio.HasValue)
        {
            command.Parameters.AddWithValue("@MaxRatio", maxAspectRatio.Value);
        }
        if (maxLastUsed.HasValue)
        {
            var unixTime = ((DateTimeOffset)maxLastUsed.Value).ToUnixTimeSeconds();
            command.Parameters.AddWithValue("@MaxLastUsed", unixTime);
        }
    }
}
