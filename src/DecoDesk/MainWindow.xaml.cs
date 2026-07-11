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
    /// 「追加...」ボタンのクリックイベント処理。モダンなフォルダ選択ダイアログを表示してリストに追加します。
    /// </summary>
    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "追加する画像フォルダを選択してください",
            Multiselect = false
        };

        if (dialog.ShowDialog() == true)
        {
            if (DataContext is ViewModels.MainWindowViewModel viewModel)
            {
                var folder = dialog.FolderName;
                if (!string.IsNullOrWhiteSpace(folder) && !viewModel.TargetDirectories.Contains(folder))
                {
                    viewModel.TargetDirectories.Add(folder);
                }
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