using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using SkiaSharp;
using DecoDesk.Database;
using DecoDesk.OS;
using DecoDesk.Rendering;

namespace DecoDesk.Tests;

/// <summary>
/// アプリケーションのボトルネックとなりやすい画像レンダリングのメモリ管理、
/// およびファイル監視同期と DB 書き込み性能をプロファイリング検証するテストクラスです。
/// </summary>
public sealed class PerformanceProfilerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    /// <summary>
    /// 一時テスト用ディレクトリとデータベースパスを初期化します。
    /// </summary>
    public PerformanceProfilerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "PerformanceTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test.db");
    }

    /// <summary>
    /// テスト実行後に作成した一時リソースをすべてクリーンアップします。
    /// </summary>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // クリーンアップエラーは無視
        }
    }

    private void CreateDummyImage(string filePath, int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Red);

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 80);
        using var stream = File.OpenWrite(filePath);
        data.SaveTo(stream);
    }

    /// <summary>
    /// DirectoryWatcher による画像ディレクトリ監視が、ファイルの新規作成・名前変更・削除イベントに追従して
    /// SQLite DB レコードを適切に挿入・更新・削除できることを実証します。
    /// </summary>
    [Fact]
    public async Task DirectoryWatcher_ShouldSynchronizeDbRecords()
    {
        var dbManager = new DatabaseManager(_dbPath);
        await dbManager.InitializeAsync();

        var watchDir = Path.Combine(_tempDir, "WatchDir");
        Directory.CreateDirectory(watchDir);

        using var watcher = new DirectoryWatcher(dbManager, watchDir);
        watcher.Start();

        var testFile = Path.Combine(watchDir, "photo.jpg");

        // 1. ファイル作成検知テスト
        CreateDummyImage(testFile, 800, 600);

        // FileSystemWatcher イベントの非同期発火とファイル書き込みロック完了待ちのため少し長めに待機
        await Task.Delay(1500);

        using (var connection = dbManager.CreateConnection())
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM Images WHERE Path = @Path;";
            command.Parameters.AddWithValue("@Path", testFile);
            var count = (long)(await command.ExecuteScalarAsync() ?? 0L);
            Assert.Equal(1L, count);
        }

        // 2. 名前変更検知テスト
        var renamedFile = Path.Combine(watchDir, "photo_renamed.jpg");
        File.Move(testFile, renamedFile);

        await Task.Delay(1500);

        using (var connection = dbManager.CreateConnection())
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM Images WHERE Path = @Path;";
            command.Parameters.AddWithValue("@Path", renamedFile);
            var count = (long)(await command.ExecuteScalarAsync() ?? 0L);
            Assert.Equal(1L, count);

            command.CommandText = "SELECT COUNT(*) FROM Images WHERE Path = @Path;";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("@Path", testFile);
            var oldCount = (long)(await command.ExecuteScalarAsync() ?? 0L);
            Assert.Equal(0L, oldCount);
        }

        // 3. 削除検知テスト
        File.Delete(renamedFile);

        await Task.Delay(1500);

        using (var connection = dbManager.CreateConnection())
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM Images WHERE Path = @Path;";
            command.Parameters.AddWithValue("@Path", renamedFile);
            var count = (long)(await command.ExecuteScalarAsync() ?? 0L);
            Assert.Equal(0L, count);
        }
    }

    /// <summary>
    /// SkiaSharp による巨大なコラージュレンダリングループを高負荷で繰り返し実行した際に、
    /// ネイティブメモリリーク（SKBitmapやSKCanvas等のDispose漏れ）が発生しないことをプロファイリング実証します。
    /// </summary>
    [Fact]
    public void SkiaSharpRenderer_ShouldNotLeakMemoryOnHeavyRenderCycle()
    {
        var renderer = new WallpaperRenderer();
        var watchDir = Path.Combine(_tempDir, "RenderDir");
        Directory.CreateDirectory(watchDir);

        var imagePath = Path.Combine(watchDir, "src.jpg");
        CreateDummyImage(imagePath, 1920, 1080);
        var imagePaths = new System.Collections.Generic.List<string> { imagePath };

        // 初回実行による JIT 最適化や内部ランタイムのメモリ確保を安定させる
        using (var bmp = renderer.CreateRandomComposite(imagePaths, 1920, 1080, 200, 1.0, "#2F4F4F")) { }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long initialMemory = Process.GetCurrentProcess().WorkingSet64;

        // レンダリングを 50 回繰り返し実行し、アンマネージド領域のメモリ解放が正しく行われるか確認
        for (int i = 0; i < 50; i++)
        {
            using var bmp = renderer.CreateRandomComposite(imagePaths, 1920, 1080, 200, 1.0, "#2F4F4F");
            Assert.NotNull(bmp);
            Assert.Equal(1920, bmp.Width);
            Assert.Equal(1080, bmp.Height);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long finalMemory = Process.GetCurrentProcess().WorkingSet64;

        // メモリリークがある場合、巨大画像50枚分のサイズ（約400MB以上）のメモリ増加が発生する
        // 正しく解放されていれば、GCのフラグメンテーションを考慮しても増加量は 50MB 未満に収まることをアサーション
        long diff = finalMemory - initialMemory;
        Assert.True(diff < 50 * 1024 * 1024, $"SkiaSharp のレンダリング処理においてメモリリークが発生している可能性があります。増加量: {diff / 1024 / 1024} MB");
    }

    /// <summary>
    /// 大規模インデックス登録時における SQLite のバルクインサート性能を検証し、
    /// トランザクションが適切に機能し十分高速にインデックス処理が行えることをアサーションします。
    /// </summary>
    [Fact]
    public async Task SQLiteBulkInsert_ShouldBeHighlyPerformant()
    {
        var dbManager = new DatabaseManager(_dbPath);
        await dbManager.InitializeAsync();

        var indexer = new ImageBulkIndexer(dbManager);
        var mockImageDir = Path.Combine(_tempDir, "MockBulkDir");
        Directory.CreateDirectory(mockImageDir);

        // 100 件のダミー画像ファイルを作成
        for (int i = 0; i < 100; i++)
        {
            var filePath = Path.Combine(mockImageDir, $"img_{i}.jpg");
            CreateDummyImage(filePath, 800, 600);
        }

        var stopwatch = Stopwatch.StartNew();
        await indexer.RunIndexAsync(mockImageDir);
        stopwatch.Stop();

        // 100件のヘッダ解析＋DB登録が1.5秒以内に完了することを検証
        Assert.True(stopwatch.ElapsedMilliseconds < 1500, $"バルクインサートの処理速度が基準値を満たしていません。所要時間: {stopwatch.ElapsedMilliseconds} ms");

        using var connection = dbManager.CreateConnection();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Images;";
        var count = (long)(await command.ExecuteScalarAsync() ?? 0L);
        Assert.Equal(100L, count);
    }
}
