using System.Windows.Input;
using Microsoft.Extensions.Logging;

namespace PLCSIM.UnitTest.App.Wpf.ViewModels;

public sealed class AsyncCommand : ICommand
{
    private readonly Func<CancellationToken, Task> execute;
    private readonly Func<bool> canExecute;
    private readonly ILogger logger;
    private bool isExecuting;

    public AsyncCommand(
        Func<CancellationToken, Task> execute,
        Func<bool> canExecute,
        ILogger logger)
    {
        this.execute = execute;
        this.canExecute = canExecute;
        this.logger = logger;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
    {
        return !isExecuting && canExecute();
    }

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        isExecuting = true;
        RaiseCanExecuteChanged();

        try
        {
            using var executionCancellation = new CancellationTokenSource();
            await execute(executionCancellation.Token);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Command execution failed.");
            throw;
        }
        finally
        {
            isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
