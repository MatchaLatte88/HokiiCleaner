using System.Windows.Input;

namespace Cleanii.App.Infrastructure;

/// <summary>Command mit typisiertem Parameter. Ein Parameter anderen Typs ist ein Programmierfehler und wird nicht verschluckt.</summary>
public sealed class RelayCommand<T> : ICommand
    where T : class
{
    private readonly Action<T> _execute;
    private readonly Func<T, bool>? _canExecute;

    public RelayCommand(Action<T> execute, Func<T, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => parameter is T value && (_canExecute?.Invoke(value) ?? true);

    public void Execute(object? parameter)
    {
        if (parameter is not T value)
        {
            throw new ArgumentException($"Erwartet {typeof(T).Name}, erhalten: {parameter?.GetType().Name ?? "null"}.", nameof(parameter));
        }

        _execute(value);
    }
}
