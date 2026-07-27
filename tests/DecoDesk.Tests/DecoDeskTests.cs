using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
using SkiaSharp;
using DecoDesk.Database;
using DecoDesk.IO;
using DecoDesk.Configuration;

namespace DecoDesk.Tests;

/// <summary>
/// DecoDesk Phase 2 の各コンポーネント（I/O、データベース、インデクサー）の動作を検証するテストクラスです。
/// </summary>
public sealed class DecoDeskTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    /// <summary>
    /// テスト実行ごとに、分離された一時ディレクトリとDBパスを初期化します。
    /// </summary>
    public DecoDeskTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DecoDeskTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test.db");
    }

    /// <summary>
    /// テスト後に一時ディレクトリをクリーンアップします。
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
            // テスト終了後の削除例外は無視
        }
    }

    /// <summary>
    /// テスト用の画像ファイルを動的に生成します。
    /// </summary>
    private string CreateTestImage(string relativePath, int width, int height, SKEncodedImageFormat format)
    {
        var filePath = Path.Combine(_tempDir, relativePath);
        var dir = Path.GetDirectoryName(filePath);
        if (dir != null && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Blue);

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 80);
        using var stream = File.OpenWrite(filePath);
        data.SaveTo(stream);

        return filePath;
    }

    /// <summary>
    /// 画像ファイルを完全にデコードせずに、ヘッダのバイナリパースから正常に解像度を取得できるかをテストします。
    /// </summary>
    [Fact]
    public void ImageHeaderParser_ShouldCorrectlyParseImageResolution()
    {
        // Arrange
        var pngPath = CreateTestImage("test.png", 320, 240, SKEncodedImageFormat.Png);
        var jpegPath = CreateTestImage("test.jpg", 640, 480, SKEncodedImageFormat.Jpeg);
        var webpPath = CreateTestImage("test.webp", 800, 600, SKEncodedImageFormat.Webp);

        // Act
        var pngSize = ImageHeaderParser.GetImageSize(pngPath);
        var jpegSize = ImageHeaderParser.GetImageSize(jpegPath);
        var webpSize = ImageHeaderParser.GetImageSize(webpPath);

        // Assert
        Assert.NotNull(pngSize);
        Assert.Equal(320, pngSize.Value.Width);
        Assert.Equal(240, pngSize.Value.Height);

        Assert.NotNull(jpegSize);
        Assert.Equal(640, jpegSize.Value.Width);
        Assert.Equal(480, jpegSize.Value.Height);

        Assert.NotNull(webpSize);
        Assert.Equal(800, webpSize.Value.Width);
        Assert.Equal(600, webpSize.Value.Height);
    }

    /// <summary>
    /// データベースの初期化と、インデックスを活用した高速ランダムクエリの条件抽出結果をテストします。
    /// </summary>
    [Fact]
    public async Task DatabaseManager_ShouldInitializeAndPerformFastRandomQuery()
    {
        // Arrange
        var dbManager = new DatabaseManager(_dbPath);
        await dbManager.InitializeAsync();

        using (var connection = dbManager.CreateConnection())
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            
            // 擬似データをDBに登録
            // 1. 横長画像 (アスペクト比 1.5)
            command.CommandText = "INSERT INTO Images (Path, Width, Height, AspectRatio, LastUsed) VALUES ('C:\\img1.jpg', 300, 200, 1.5, 1000);";
            await command.ExecuteNonQueryAsync();

            // 2. 縦長画像 (アスペクト比 0.5)
            command.CommandText = "INSERT INTO Images (Path, Width, Height, AspectRatio, LastUsed) VALUES ('C:\\img2.jpg', 100, 200, 0.5, 2000);";
            await command.ExecuteNonQueryAsync();

            // 3. 超横長画像 (アスペクト比 2.0)
            command.CommandText = "INSERT INTO Images (Path, Width, Height, AspectRatio, LastUsed) VALUES ('C:\\img3.jpg', 400, 200, 2.0, 3000);";
            await command.ExecuteNonQueryAsync();
        }

        // Act & Assert
        // A. アスペクト比 1.0 〜 2.5 の横長画像を条件指定してランダム抽出
        var record = await dbManager.GetRandomImageAsync(1.0, 2.5, null);
        Assert.NotNull(record);
        Assert.True(record.AspectRatio >= 1.0 && record.AspectRatio <= 2.5);
        Assert.Contains(record.Path, new[] { "C:\\img1.jpg", "C:\\img3.jpg" });

        // B. 縦長画像のみを抽出
        var verticalRecord = await dbManager.GetRandomImageAsync(0.1, 0.9, null);
        Assert.NotNull(verticalRecord);
        Assert.Equal("C:\\img2.jpg", verticalRecord.Path);

        // C. 条件に一致しない無効なアスペクト比を指定した場合
        var emptyRecord = await dbManager.GetRandomImageAsync(3.0, 4.0, null);
        Assert.Null(emptyRecord);
    }

    /// <summary>
    /// バルクインデクサーが指定ディレクトリ配下の全画像ファイルを検知し、DBへ一括登録できるかをテストします。
    /// </summary>
    [Fact]
    public async Task ImageBulkIndexer_ShouldRegisterAllFiles()
    {
        // Arrange
        var rootDir = Path.Combine(_tempDir, "scan_root");
        Directory.CreateDirectory(rootDir);

        // 一時フォルダ配下にテスト画像を生成
        CreateTestImage("scan_root/img1.png", 100, 100, SKEncodedImageFormat.Png);
        CreateTestImage("scan_root/sub/img2.jpg", 200, 100, SKEncodedImageFormat.Jpeg);
        
        // テスト対象外（画像以外）のファイルを生成
        File.WriteAllText(Path.Combine(rootDir, "ignore.txt"), "DecoDesk mock text file");

        var dbManager = new DatabaseManager(_dbPath);
        var indexer = new ImageBulkIndexer(dbManager);

        var progressList = new System.Collections.Concurrent.ConcurrentBag<ImageBulkIndexer.IndexProgress>();
        var progress = new Progress<ImageBulkIndexer.IndexProgress>(p => progressList.Add(p));

        // Act
        await indexer.RunIndexAsync(new[] { rootDir }, progress);

        // 進捗イベントのキュー処理完了を少し待つ
        await Task.Delay(150);

        // Assert
        using var connection = dbManager.CreateConnection();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Images;";
        var count = Convert.ToInt32(await command.ExecuteScalarAsync());

        Assert.Equal(2, count); // img1.png と img2.jpg のみが登録されていること
        Assert.NotEmpty(progressList);
    }

    /// <summary>
    /// SettingsManager が設定の保存と読み込みを正しく行えるかをテストします。
    /// </summary>
    [Fact]
    public void SettingsManager_ShouldSaveAndLoadSettingsCorrectly()
    {
        // Arrange
        var settingsPath = Path.Combine(_tempDir, "settings.json");
        var manager = new SettingsManager(settingsPath);

        // 1. 初回ロード（ファイル未存在）でデフォルト値が返ることを確認
        var defaultSettings = manager.Load();
        Assert.NotNull(defaultSettings);
        Assert.Empty(defaultSettings.TargetDirectories);
        Assert.False(defaultSettings.AutoUpdateEnabled);
        Assert.Equal(900, defaultSettings.UpdateIntervalSeconds);
        Assert.Equal("None", defaultSettings.ProcessingMode);
        Assert.Equal(6, defaultSettings.WallpaperImageCount);
        Assert.Equal(350, defaultSettings.WallpaperImageSize);
        Assert.Equal(1.0, defaultSettings.WallpaperDensity);
        Assert.Equal("#2F4F4F", defaultSettings.WallpaperBackgroundColor);

        // 2. 設定を変更して保存
        var newSettings = new AppSettings
        {
            TargetDirectories = new System.Collections.Generic.List<string> { @"C:\MockImages" },
            AutoUpdateEnabled = true,
            UpdateIntervalSeconds = 1800,
            ProcessingMode = "Postcard",
            WallpaperImageCount = 8,
            WallpaperImageSize = 400,
            WallpaperDensity = 1.2,
            WallpaperBackgroundColor = "#123456",
            StartWithWindows = true,
            UpdateWallpaperOnStartup = true,
            StartupDelaySeconds = 120
        };
        manager.Save(newSettings);

        // 3. 別のマネージャーインスタンスで再ロードして整合性を検証
        var anotherManager = new SettingsManager(settingsPath);
        var loadedSettings = anotherManager.Load();

        // Assert
        Assert.NotNull(loadedSettings);
        Assert.Single(loadedSettings.TargetDirectories);
        Assert.Equal(@"C:\MockImages", loadedSettings.TargetDirectories[0]);
        Assert.True(loadedSettings.AutoUpdateEnabled);
        Assert.Equal(1800, loadedSettings.UpdateIntervalSeconds);
        Assert.Equal("Postcard", loadedSettings.ProcessingMode);
        Assert.Equal(8, loadedSettings.WallpaperImageCount);
        Assert.Equal(400, loadedSettings.WallpaperImageSize);
        Assert.Equal(1.2, loadedSettings.WallpaperDensity);
        Assert.Equal("#123456", loadedSettings.WallpaperBackgroundColor);
        Assert.True(loadedSettings.StartWithWindows);
        Assert.True(loadedSettings.UpdateWallpaperOnStartup);
        Assert.Equal(120, loadedSettings.StartupDelaySeconds);
    }

    [Fact]
    public void DumpMonitorsTest()
    {
        try
        {
            var monitors = OS.WallpaperInterop.GetDisplayMonitors();
            foreach (var m in monitors)
            {
                Console.WriteLine($"MONITOR_DUMP: ID={m.MonitorId}, Width={m.Width}, Height={m.Height}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"MONITOR_DUMP_ERROR: {ex.Message}");
        }
    }

    [Fact]
    public async Task GetLeastUsedImages_ShouldReturnUnusedAndOldestImagesFirst()
    {
        // 準備
        var dbManager = new DatabaseManager(_dbPath);
        await dbManager.InitializeAsync();

        // テスト用テーブルをクリアして初期化
        using (var connection = dbManager.CreateConnection())
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM Images;";
            await command.ExecuteNonQueryAsync();

            // テストデータを挿入
            // 1. 未使用画像 (LastUsed = 0)
            // 2. 過去に使われた古い画像 (LastUsed = 100)
            // 3. 最近使われた新しい画像 (LastUsed = 200)
            command.CommandText = @"
                INSERT INTO Images (Path, Width, Height, AspectRatio, LastUsed) VALUES 
                ('img_recent.jpg', 800, 600, 1.33, 200),
                ('img_unused.jpg', 800, 600, 1.33, 0),
                ('img_old.jpg', 800, 600, 1.33, 100);
            ";
            await command.ExecuteNonQueryAsync();
        }

        // 実行: 3枚取得を試みる
        var result = await dbManager.GetLeastUsedImagesAsync(3, null, null);

        // 検証
        Assert.NotNull(result);
        Assert.Equal(3, result.Count);
        
        // 順序の検証: 1番目は LastUsed = 0 (img_unused.jpg)、2番目は LastUsed = 100 (img_old.jpg)、3番目は LastUsed = 200 (img_recent.jpg)
        Assert.Equal("img_unused.jpg", result[0].Path);
        Assert.Equal("img_old.jpg", result[1].Path);
        Assert.Equal("img_recent.jpg", result[2].Path);
    }
}
