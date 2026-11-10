using System;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace DecoDesk.OS;

/// <summary>
/// ユーザーへの通知可否状態を表す列挙型です。Windows API の <c>QUERY_USER_NOTIFICATION_STATE</c> に対応します。
/// </summary>
public enum UserNotificationState
{
    /// <summary>スクリーンセーバーの表示中、画面ロック中、または非アクティブなユーザー切り替えセッション中です。</summary>
    NotPresent = 1,

    /// <summary>全画面のアプリケーションが動作中、またはプレゼンテーション設定が適用されています。</summary>
    Busy = 2,

    /// <summary>全画面（排他モード）の Direct3D アプリケーションが動作中です。</summary>
    RunningD3dFullScreen = 3,

    /// <summary>ユーザーがプレゼンテーションモードを有効にしています。</summary>
    PresentationMode = 4,

    /// <summary>上記のいずれにも該当せず、通知を自由に表示できる通常の状態です。</summary>
    AcceptsNotifications = 5,

    /// <summary>ログイン直後などの「クワイエットタイム」です。</summary>
    QuietTime = 6,

    /// <summary>Windows ストアアプリが動作中です。</summary>
    RunningWindowsStoreApp = 7
}

/// <summary>
/// 全画面アプリケーション（ゲームなど）の動作状態を検出するためのクラスです。
/// ゲームプレイ中の壁紙生成による処理負荷（フレームレート低下やラグ）を避ける目的で使用します。
/// </summary>
public static class FullScreenDetector
{
    private static readonly ILogger Logger = AppLoggerFactory.Factory.CreateLogger("DecoDesk.OS.FullScreenDetector");

    /// <summary>
    /// 現在のユーザーセッションにおける通知可否状態を取得します。
    /// </summary>
    /// <param name="pquns">取得された通知可否状態</param>
    /// <returns>処理結果を示す HRESULT</returns>
    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out UserNotificationState pquns);

    /// <summary>
    /// 壁紙の更新を見送るべき状態かどうかを判定します。
    /// 全画面アプリケーションの動作中やプレゼンテーションモード中は <c>true</c> を返します。
    /// </summary>
    /// <remarks>
    /// 状態の取得に失敗した場合は <c>false</c> を返します（フェイルオープン）。
    /// 検出できないことを理由に壁紙の自動更新そのものが停止してしまう事態を避けるためです。
    /// なお、ボーダーレスウィンドウで動作するゲームは <see cref="UserNotificationState.AcceptsNotifications"/> を
    /// 返す場合があり、この方法では検出できません。その場合はタスクトレイメニューからの手動一時停止を使用してください。
    /// </remarks>
    /// <returns>壁紙の更新を見送るべき場合は <c>true</c>、更新して問題ない場合は <c>false</c></returns>
    public static bool ShouldSuppressUpdate()
    {
        if (!TryGetNotificationState(out var state))
        {
            return false;
        }

        // 画面ロック中（NotPresent）やクワイエットタイム（QuietTime）は負荷の観点では問題がないため対象外とする。
        // ストアアプリの動作中（RunningWindowsStoreApp）は全画面とは限らないため、過剰な抑止を避けて対象外とする。
        var shouldSuppress = state is UserNotificationState.Busy
            or UserNotificationState.RunningD3dFullScreen
            or UserNotificationState.PresentationMode;

        if (shouldSuppress)
        {
            Logger.LogInformation("全画面アプリケーションの動作を検出したため、壁紙の更新を見送ります。状態={State}", state);
        }

        return shouldSuppress;
    }

    /// <summary>
    /// 現在のユーザーセッションにおける通知可否状態の取得を試みます。
    /// </summary>
    /// <param name="state">取得された通知可否状態。失敗時は <see cref="UserNotificationState.AcceptsNotifications"/></param>
    /// <returns>取得に成功した場合は <c>true</c></returns>
    private static bool TryGetNotificationState(out UserNotificationState state)
    {
        try
        {
            var hr = SHQueryUserNotificationState(out state);
            if (hr != 0)
            {
                Logger.LogWarning("SHQueryUserNotificationState の呼び出しが失敗しました。HRESULT=0x{HResult:X8}", hr);
                state = UserNotificationState.AcceptsNotifications;
                return false;
            }

            Logger.LogDebug("現在の通知可否状態: {State}", state);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "全画面アプリケーションの状態取得中に例外が発生しました。検出をスキップします。");
            state = UserNotificationState.AcceptsNotifications;
            return false;
        }
    }
}
