using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DecoDesk.ViewModels;

/// <summary>
/// プロパティ変更通知（MVVMパターン）をサポートする ViewModel の基底クラスです。
/// </summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    /// <summary>
    /// プロパティ値が変更されたときに発生するイベントです。
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// プロパティ値を設定し、変更があった場合は通知イベントを発生させます。
    /// </summary>
    /// <typeparam name="T">プロパティの型</typeparam>
    /// <param name="field">バッキングフィールドへの参照</param>
    /// <param name="value">新しい値</param>
    /// <param name="propertyName">プロパティ名（自動設定）</param>
    /// <returns>値が変更された場合は true、それ以外は false</returns>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    /// <summary>
    /// 指定されたプロパティ名の変更通知を強制的に発生させます。
    /// </summary>
    /// <param name="propertyName">変更されたプロパティ名</param>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
