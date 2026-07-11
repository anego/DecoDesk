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