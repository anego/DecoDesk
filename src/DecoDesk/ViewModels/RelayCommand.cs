using System;
using System.Windows.Input;

namespace DecoDesk.ViewModels;

/// <summary>
/// アクションやメソッドをバインドするための <see cref="ICommand"/> の標準的な実装です。
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    /// <summary>
    /// 指定された実行処理と実行可能判定を使用してコマンドを初期化します。
    /// </summary>
    /// <param name="execute">コマンド実行時の処理</param>
    /// <param name="canExecute">コマンドが実行可能かどうかを判定する処理（Null許容）</param>
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    /// <summary>
    /// コマンドの実行可能状態が変化したときに発生します。
    /// </summary>
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    /// <summary>
    /// コマンドが実行可能かどうかを判定します。
    /// </summary>
    public bool CanExecute(object? parameter)
    {
        return _canExecute == null || _canExecute();
    }

    /// <summary>
    /// コマンドを実行します。
    /// </summary>
    public void Execute(object? parameter)
    {
        _execute();
    }
}

/// <summary>
/// パラメータを受け取る <see cref="ICommand"/> の実装です。
/// </summary>
/// <typeparam name="T">パラメータの型</typeparam>
public sealed class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _execute;
    private readonly Func<T?, bool>? _canExecute;

    /// <summary>
    /// 指定された実行処理と実行可能判定を使用してコマンドを初期化します。
    /// </summary>
    /// <param name="execute">コマンド実行時の処理</param>
    /// <param name="canExecute">コマンドが実行可能かどうかを判定する処理（Null許容）</param>
    public RelayCommand(Action<T?> execute, Func<T?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    /// <summary>
    /// コマンドの実行可能状態が変化したときに発生します。
    /// </summary>
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    /// <summary>
    /// コマンドが実行可能かどうかを判定します。
    /// </summary>
    public bool CanExecute(object? parameter)
    {
        if (_canExecute == null) return true;
        
        if (parameter == null && typeof(T).IsValueType)
        {
            return _canExecute(default);
        }
        return _canExecute((T?)parameter);
    }

    /// <summary>
    /// コマンドを実行します。
    /// </summary>
    public void Execute(object? parameter)
    {
        if (parameter == null && typeof(T).IsValueType)
        {
            _execute(default);
        }
        else
        {
            _execute((T?)parameter);
        }
    }
}
