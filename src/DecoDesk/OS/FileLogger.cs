using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace DecoDesk.OS;

/// <summary>
/// ログメッセージを日付単位のローカルテキストファイルに出力し、古いログを自動クリーンアップするロガープロバイダーです。
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _logDirectory;

    /// <summary>
    /// 出力先のディレクトリパスを指定して <see cref="FileLoggerProvider"/> を初期化します。
    /// </summary>
    /// <param name="logDirectory">ログファイルが保存されるディレクトリの絶対パス</param>
    public FileLoggerProvider(string logDirectory)
    {
        _logDirectory = logDirectory;
        if (!Directory.Exists(_logDirectory))
        {
            Directory.CreateDirectory(_logDirectory);
        }

        // 起動時に非同期で古いログのローテーションと削除処理を実行
        CleanupOldLogFiles();
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName)
    {
        return new FileLogger(categoryName, _logDirectory);
    }

    /// <summary>
    /// 保存期間（30日）を過ぎた古いログファイルをバックグラウンドスレッドで自動削除します。
    /// </summary>
    private void CleanupOldLogFiles()
    {
        Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(_logDirectory)) return;

                var thresholdDate = DateTime.Now.AddDays(-30);

                // 1. 日付ベースログ (log_yyyy-MM-dd.txt) のクリーンアップ
                var files = Directory.GetFiles(_logDirectory, "log_*.txt");
                foreach (var file in files)
                {
                    try
                    {
                        var fileInfo = new FileInfo(file);
                        if (fileInfo.LastWriteTime < thresholdDate)
                        {
                            fileInfo.Delete();
                        }
                    }
                    catch
                    {
                        // 個別のファイル削除失敗は握り潰して継続
                    }
                }

                // 2. 旧形式の単一ファイルログ (debug_log.txt) のクリーンアップ
                var oldDebugLog = Path.Combine(_logDirectory, "debug_log.txt");
                if (File.Exists(oldDebugLog))
                {
                    try
                    {
                        var fileInfo = new FileInfo(oldDebugLog);
                        if (fileInfo.LastWriteTime < thresholdDate)
                        {
                            fileInfo.Delete();
                        }
                    }
                    catch
                    {
                        // 無視
                    }
                }
            }
            catch
            {
                // 例外保護
            }
        });
    }

    /// <inheritdoc />
    public void Dispose() { }
}

/// <summary>
/// 指定されたカテゴリ名で、日付ごとのファイルにログを書き出すロガークラスです。
/// </summary>
public sealed class FileLogger : ILogger
{
    private readonly string _categoryName;
    private readonly string _logDirectory;
    private static readonly object LockObject = new();

    /// <summary>
    /// カテゴリ名と出力先のディレクトリパスを指定して <see cref="FileLogger"/> を初期化します。
    /// </summary>
    /// <param name="categoryName">ログカテゴリ名</param>
    /// <param name="logDirectory">ログファイルが保存されるディレクトリの絶対パス</param>
    public FileLogger(string categoryName, string logDirectory)
    {
        _categoryName = categoryName;
        _logDirectory = logDirectory;
    }

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (formatter == null) return;
        var message = formatter(state, exception);
        
        var now = DateTime.Now;
        var logLine = $"[{now:yyyy-MM-dd HH:mm:ss.fff}] [{logLevel}] [{_categoryName}] {message}";
        if (exception != null)
        {
            logLine += $"{Environment.NewLine}{exception}";
        }

        // 日付ごとに書き込みファイルを切り替える
        var logFileName = $"log_{now:yyyy-MM-dd}.txt";
        var logFilePath = Path.Combine(_logDirectory, logFileName);

        lock (LockObject)
        {
            try
            {
                File.AppendAllText(logFilePath, logLine + Environment.NewLine);
            }
            catch
            {
                // ロギング中の例外は上位に伝播させない
            }
        }
    }
}

/// <summary>
/// アプリケーション全体で共通して使用できる静的なロギングファクトリです。
/// </summary>
public static class AppLoggerFactory
{
    private static ILoggerFactory? _factory;

    /// <summary>
    /// 構成されたログファクトリインスタンスを取得します。
    /// </summary>
    public static ILoggerFactory Factory
    {
        get
        {
            if (_factory == null)
            {
                var tempDir = Path.Combine(Path.GetTempPath(), "DecoDesk");
                _factory = LoggerFactory.Create(builder =>
                {
                    builder.AddProvider(new FileLoggerProvider(tempDir));
                });
            }
            return _factory;
        }
    }

    /// <summary>
    /// 指定されたクラスの型に紐づく <see cref="ILogger{TCategoryName}"/> を生成します。
    /// </summary>
    /// <typeparam name="T">ログカテゴリを指定するクラスの型</typeparam>
    /// <returns>生成されたロガー</returns>
    public static ILogger<T> CreateLogger<T>() => Factory.CreateLogger<T>();
}
