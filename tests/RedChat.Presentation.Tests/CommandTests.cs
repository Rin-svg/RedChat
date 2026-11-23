using RedChat.Presentation;
using Xunit;

namespace RedChat.Presentation.Tests;

public sealed class CommandTests
{
    [Fact]
    public async Task AsyncCommand_IgnoresReentrantExecution()
    {
        int calls = 0;
        TaskCompletionSource<bool> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using AsyncRelayCommand command = new(async token =>
        {
            Interlocked.Increment(ref calls);
            await release.Task;
        });

        Task first = command.ExecuteAsync();
        Task second = command.ExecuteAsync();
        Assert.Equal(1, calls);
        Assert.True(command.IsRunning);
        release.SetResult(true);
        await Task.WhenAll(first, second);
        Assert.False(command.IsRunning);
    }

    [Fact]
    public async Task AsyncCommand_CancellationResetsBusyState()
    {
        using AsyncRelayCommand command = new(token => Task.Delay(Timeout.InfiniteTimeSpan, token));
        Task execution = command.ExecuteAsync();
        Assert.True(command.IsRunning);
        command.Cancel();
        await execution;
        Assert.False(command.IsRunning);
    }

    [Fact]
    public async Task AsyncCommand_ReportsExceptionInsteadOfThrowing()
    {
        Exception? captured = null;
        using AsyncRelayCommand command = new(_ => throw new InvalidOperationException("échec"), ex => captured = ex);
        await command.ExecuteAsync();
        Assert.NotNull(captured);
        Assert.Equal("échec", captured!.Message);
        Assert.False(command.IsRunning);
    }

    [Fact]
    public void RelayCommand_ReportsExceptionInsteadOfThrowing()
    {
        Exception? captured = null;
        RelayCommand command = new(() => throw new InvalidOperationException("erreur"), ex => captured = ex);
        command.Execute(null);
        Assert.NotNull(captured);
    }
}
