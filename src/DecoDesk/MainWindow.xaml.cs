using System.ComponentModel;
using System.Windows;

namespace DecoDesk;

/// <summary>
/// 設定画面を提供するメインウィンドウのクラスです。
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// <see cref="MainWindow"/> クラスの新しいインスタンスを初期化します。
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();

        if (DataContext is ViewModels.MainWindowViewModel viewModel)
        {
            viewModel.RequestClose += () => Close();
        }
    }

    /// <summary>
    /// 「参照...」ボタンのクリックイベント処理。モダンなフォルダ選択ダイアログを表示します。
    /// </summary>
    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "対象の画像フォルダを選択してください",
            Multiselect = false
        };

        if (dialog.ShowDialog() == true)
        {
            if (DataContext is ViewModels.MainWindowViewModel viewModel)
            {
                viewModel.TargetDirectory = dialog.FolderName;
            }
        }
    }

    /// <summary>
    /// 「閉じる」ボタンのクリックイベント処理。
    /// </summary>
    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>
    /// ウィンドウが閉じられる際の処理を行います。
    /// </summary>
    /// <param name="e">イベント引数。キャンセルを設定して非表示にすることができます。</param>
    protected override void OnClosing(CancelEventArgs e)
    {
        // アプリケーション全体の終了ではなく、非表示（Hide）にする
        e.Cancel = true;
        Hide();
        
        base.OnClosing(e);
    }
}