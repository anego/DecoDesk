using System.Reflection;
using System.Windows;

namespace DecoDesk;

/// <summary>
/// アプリケーションのバージョン情報を表示するダイアログです。
/// </summary>
public partial class VersionWindow : Window
{
    /// <summary>
    /// <see cref="VersionWindow"/> クラスの新しいインスタンスを初期化します。
    /// </summary>
    public VersionWindow()
    {
        InitializeComponent();

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"バージョン {version?.ToString(3) ?? "不明"}";

        var frameworkDescription = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
        RuntimeText.Text = $"実行環境: {frameworkDescription}";
    }

    /// <summary>
    /// 「閉じる」ボタンのクリックイベント処理。
    /// </summary>
    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
