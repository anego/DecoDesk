using System;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using DecoDesk.IO;

namespace DecoDesk.Database;

/// <summary>
/// ディレクトリ走査から画像サイズ抽出、SQLiteへのバルクインサートまでの一連のインデックス登録処理を行うクラスです。
/// </summary>
public sealed class ImageBulkIndexer
{
    private static readonly ILogger Logger = OS.AppLoggerFactory.CreateLogger<ImageBulkIndexer>();

    private readonly DatabaseManager _dbManager;
    private const int BatchSize = 2000;

    /// <summary>
    /// インデックス同期の同時実行を防ぐためのセマフォです。
    /// 設定保存に伴う自動同期と、設定画面からの手動同期が重なると、
    /// 孤立レコードの削除とバルクInsertが互いのトランザクションと競合するため直列化します。
    /// </summary>
    private static readonly SemaphoreSlim IndexGate = new(1, 1);

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
    /// <returns>データベースに登録された画像ファイルの件数</returns>
    public async Task<int> RunIndexAsync(System.Collections.Generic.IEnumerable<string> rootPaths, IProgress<IndexProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        // 同時実行を防ぐため、先行する同期の完了を待ってから処理を開始する
        if (!await IndexGate.WaitAsync(0, cancellationToken))
        {
            Logger.LogInformation("ほかのインデックス同期が実行中のため、完了を待機します。");
            await IndexGate.WaitAsync(cancellationToken);
        }

        try
        {
            return await RunIndexCoreAsync(rootPaths, progress, cancellationToken);
        }
        finally
        {
            IndexGate.Release();
        }
    }

    /// <summary>
    /// インデックス同期の本体です。呼び出し元で <see cref="IndexGate"/> による直列化が行われている前提とします。
    /// </summary>
    /// <param name="rootPaths">走査するルートディレクトリのパスのリスト</param>
    /// <param name="progress">進捗を通知するプログレス通知インターフェース</param>
    /// <param name="cancellationToken">キャンセル要求を監視するためのトークン</param>
    /// <returns>データベースに登録された画像ファイルの件数</returns>
    private async Task<int> RunIndexCoreAsync(System.Collections.Generic.IEnumerable<string> rootPaths, IProgress<IndexProgress>? progress, CancellationToken cancellationToken)
    {
        var rootPathList = new System.Collections.Generic.List<string>(rootPaths);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        Logger.LogInformation("画像インデックスの同期を開始します。対象ディレクトリ数={DirectoryCount}", rootPathList.Count);

        // データベースの初期化を確認
        await _dbManager.InitializeAsync();

        // 走査対象外（リストから除外されたフォルダ等）の不要なレコードをDBからクリーンアップ
        var deletedCount = await DeleteOrphanedRecordsAsync(rootPathList);
        if (deletedCount > 0)
        {
            Logger.LogInformation("登録ディレクトリ配下に存在しない画像レコードを削除しました。削除件数={DeletedCount}", deletedCount);
        }

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

            foreach (var rootPath in rootPathList)
            {
                if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
                {
                    Logger.LogWarning("登録ディレクトリにアクセスできないため、走査をスキップしました。パス={Path}", rootPath);
                    continue;
                }

                Logger.LogDebug("ディレクトリの走査を開始します。パス={Path}", rootPath);

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

            stopwatch.Stop();
            Logger.LogInformation(
                "画像インデックスの同期が完了しました。検出={FoundCount}件, 登録={IndexedCount}件, スキップ={SkippedCount}件, 所要時間={ElapsedMilliseconds}ms",
                foundCount, indexedCount, foundCount - indexedCount, stopwatch.ElapsedMilliseconds);

            return indexedCount;
        }
        catch (OperationCanceledException)
        {
            await RollbackQuietlyAsync(transaction);
            stopwatch.Stop();
            Logger.LogInformation(
                "画像インデックスの同期がキャンセルされました。検出={FoundCount}件, 登録={IndexedCount}件, 所要時間={ElapsedMilliseconds}ms",
                foundCount, indexedCount, stopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (Exception ex)
        {
            await RollbackQuietlyAsync(transaction);
            stopwatch.Stop();
            Logger.LogError(ex,
                "画像インデックスの同期中に例外が発生したため、処理を中断しました。検出={FoundCount}件, 登録={IndexedCount}件",
                foundCount, indexedCount);
            throw;
        }
        finally
        {
            insertCommand?.Dispose();
            transaction?.Dispose();
        }
    }

    /// <summary>
    /// トランザクションのロールバックを試み、失敗しても例外を伝播させずに記録のみ行います。
    /// 元の失敗原因を上位へ伝えるため、ロールバック自体の失敗で処理を止めないようにしています。
    /// </summary>
    /// <param name="transaction">ロールバック対象のトランザクション（null 可）</param>
    private static async Task RollbackQuietlyAsync(SqliteTransaction? transaction)
    {
        if (transaction == null)
        {
            return;
        }

        try
        {
            // キャンセル済みのトークンを渡すとロールバック自体が失敗するため、トークンは渡さない
            await transaction.RollbackAsync();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "トランザクションのロールバックに失敗しました。未コミットの変更は破棄されます。");
        }
    }

    /// <summary>
    /// 指定されたすべての登録ディレクトリ配下に属さない古い画像レコードをデータベースから削除します。
    /// </summary>
    /// <param name="rootPaths">有効な登録ディレクトリのパス一覧</param>
    /// <returns>削除されたレコード件数。クリーンアップをスキップした場合は 0</returns>
    private async Task<int> DeleteOrphanedRecordsAsync(System.Collections.Generic.IEnumerable<string> rootPaths)
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

        if (conditions.Count == 0)
        {
            // 有効な登録ディレクトリが1件も無い場合、WHERE句なしの全件DELETEを実行すると
            // データベースの内容を全消去してしまうため、安全のため何もせず処理を中断する。
            Logger.LogWarning("有効な登録ディレクトリが存在しないため、孤立レコードのクリーンアップをスキップしました。");
            return 0;
        }

        sql.Append(" WHERE ");
        sql.Append(string.Join(" AND ", conditions));

        command.CommandText = sql.ToString();
        return await command.ExecuteNonQueryAsync();
    }
}
