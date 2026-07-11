using System;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using Microsoft.Data.Sqlite;
using DecoDesk.IO;

namespace DecoDesk.Database;

/// <summary>
/// ディレクトリ走査から画像サイズ抽出、SQLiteへのバルクインサートまでの一連のインデックス登録処理を行うクラスです。
/// </summary>
public sealed class ImageBulkIndexer
{
    private readonly DatabaseManager _dbManager;
    private const int BatchSize = 2000;

    /// <summary>
    /// <see cref="ImageBulkIndexer"/> クラスの新しいインスタンスを初期化します。
    /// </summary>
    /// <param name="dbManager">使用するデータベースマネージャー</param>
    public ImageBulkIndexer(DatabaseManager dbManager)
    {
        _dbManager = dbManager ?? throw new ArgumentNullException(nameof(dbManager));
    }

    /// <summary>
    /// インデックス作成時の進捗状況を表すデータ構造です。
    /// </summary>
    /// <param name="FoundCount">検出された画像ファイルの総数</param>
    /// <param name="IndexedCount">DBに正常にインデックス登録された画像ファイルの総数</param>
    public record IndexProgress(int FoundCount, int IndexedCount);

    /// <summary>
    /// 指定された複数のルートパス配下の画像をスキャンし、データベースのインデックスを構築・更新します。
    /// </summary>
    /// <param name="rootPaths">走査するルートディレクトリのパスのリスト</param>
    /// <param name="progress">進捗を通知するプログレス通知インターフェース</param>
    /// <param name="cancellationToken">キャンセル要求を監視するためのトークン</param>
    public async Task RunIndexAsync(System.Collections.Generic.IEnumerable<string> rootPaths, IProgress<IndexProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        // データベースの初期化を確認
        await _dbManager.InitializeAsync();

        // 走査対象外（リストから除外されたフォルダ等）の不要なレコードをDBからクリーンアップ
        await DeleteOrphanedRecordsAsync(rootPaths);

        using var connection = _dbManager.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        int foundCount = 0;
        int indexedCount = 0;

        SqliteTransaction? transaction = null;
        SqliteCommand? insertCommand = null;

        try
        {
            // パラメータ化クエリの設定
            insertCommand = connection.CreateCommand();
            insertCommand.CommandText = @"
                INSERT OR REPLACE INTO Images (Path, Width, Height, AspectRatio, LastUsed)
                VALUES (@Path, @Width, @Height, @AspectRatio, @LastUsed);
            ";

            var paramPath = insertCommand.Parameters.Add("@Path", SqliteType.Text);
            var paramWidth = insertCommand.Parameters.Add("@Width", SqliteType.Integer);
            var paramHeight = insertCommand.Parameters.Add("@Height", SqliteType.Integer);
            var paramAspectRatio = insertCommand.Parameters.Add("@AspectRatio", SqliteType.Real);
            var paramLastUsed = insertCommand.Parameters.Add("@LastUsed", SqliteType.Integer);

            transaction = connection.BeginTransaction();
            insertCommand.Transaction = transaction;

            foreach (var rootPath in rootPaths)
            {
                if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
                {
                    continue;
                }

                // 1. ファイルを列挙する
                var imageFiles = FileEnumerator.EnumerateImageFiles(rootPath);

                foreach (var filePath in imageFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    foundCount++;

                    // バイナリヘッダから幅と高さを高速抽出
                    var size = ImageHeaderParser.GetImageSize(filePath);
                    if (size == null)
                    {
                        // 画像のメタデータが正常に取得できなかった場合はスキップ
                        continue;
                    }

                    var (width, height) = size.Value;
                    if (width <= 0 || height <= 0)
                    {
                        continue;
                    }

                    double aspectRatio = (double)width / height;

                    // パラメータのバインド
                    paramPath.Value = filePath;
                    paramWidth.Value = width;
                    paramHeight.Value = height;
                    paramAspectRatio.Value = aspectRatio;
                    // 初回登録時の LastUsed は 0（未適用状態）とする
                    paramLastUsed.Value = 0;

                    await insertCommand.ExecuteNonQueryAsync(cancellationToken);
                    indexedCount++;

                    // 一定バッチサイズごとにコミットして新しいトランザクションを開始
                    if (indexedCount % BatchSize == 0)
                    {
                        await transaction.CommitAsync(cancellationToken);
                        transaction.Dispose();

                        // 進捗通知
                        progress?.Report(new IndexProgress(foundCount, indexedCount));

                        transaction = connection.BeginTransaction();
                        insertCommand.Transaction = transaction;
                    }
                }
            }

            // 残りのデータをコミット
            if (transaction != null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            progress?.Report(new IndexProgress(foundCount, indexedCount));
        }
        catch
        {
            if (transaction != null)
            {
                try
                {
                    await transaction.RollbackAsync(cancellationToken);
                }
                catch
                {
                    // ロールバック失敗は無視
                }
            }
            throw;
        }
        finally
        {
            insertCommand?.Dispose();
            transaction?.Dispose();
        }
    }

    /// <summary>
    /// 指定されたすべての登録ディレクトリ配下に属さない古い画像レコードをデータベースから削除します。
    /// </summary>
    private async Task DeleteOrphanedRecordsAsync(System.Collections.Generic.IEnumerable<string> rootPaths)
    {
        using var connection = _dbManager.CreateConnection();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();

        var sql = new System.Text.StringBuilder();
        sql.Append("DELETE FROM Images");

        var conditions = new System.Collections.Generic.List<string>();
        int idx = 0;

        foreach (var rootPath in rootPaths)
        {
            if (string.IsNullOrWhiteSpace(rootPath)) continue;

            string normRoot = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string pRoot = $"@Root{idx}";
            string pPat1 = $"@Pat1_{idx}";
            string pPat2 = $"@Pat2_{idx}";

            conditions.Add($"(Path <> {pRoot} AND Path NOT LIKE {pPat1} AND Path NOT LIKE {pPat2})");

            command.Parameters.AddWithValue(pRoot, normRoot);
            command.Parameters.AddWithValue(pPat1, normRoot + Path.DirectorySeparatorChar + "%");
            command.Parameters.AddWithValue(pPat2, normRoot + Path.AltDirectorySeparatorChar + "%");
            idx++;
        }

        if (conditions.Count > 0)
        {
            sql.Append(" WHERE ");
            sql.Append(string.Join(" AND ", conditions));
        }

        command.CommandText = sql.ToString();
        await command.ExecuteNonQueryAsync();
    }
}
