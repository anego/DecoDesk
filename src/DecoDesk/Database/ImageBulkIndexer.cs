using System;
using System.Threading;
using System.Threading.Tasks;
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
    /// 指定されたルートパス配下の画像をスキャンし、データベースのインデックスを構築・更新します。
    /// </summary>
    /// <param name="rootPath">走査するルートディレクトリのパス</param>
    /// <param name="progress">進捗を通知するプログレス通知インターフェース</param>
    /// <param name="cancellationToken">キャンセル要求を監視するためのトークン</param>
    public async Task RunIndexAsync(string rootPath, IProgress<IndexProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        // データベースの初期化を確認
        await _dbManager.InitializeAsync();

        // 1. ファイルを列挙する
        var imageFiles = FileEnumerator.EnumerateImageFiles(rootPath);

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
}
