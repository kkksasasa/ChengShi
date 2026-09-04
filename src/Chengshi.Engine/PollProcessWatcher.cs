using System.Diagnostics;
using Chengshi.Core;

namespace Chengshi.Engine;

public sealed class PollProcessWatcher : IDisposable
{
    private readonly IProcessEnforcer _enforcer;
    private readonly Func<Desk?> _currentDesk;
    private readonly TimeSpan _interval;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public PollProcessWatcher(IProcessEnforcer enforcer, Func<Desk?> currentDesk, TimeSpan? interval = null)
    {
        _enforcer = enforcer;
        _currentDesk = currentDesk;
        _interval = interval ?? TimeSpan.FromMilliseconds(800);
    }

    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void Dispose()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // ignore
        }

        cts.Dispose();
        _loop = null;
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var desk = _currentDesk();
                if (desk is not null)
                {
                    foreach (var process in Process.GetProcesses())
                    {
                        try
                        {
                            if (process.Id == Environment.ProcessId)
                            {
                                continue;
                            }

                            var identity = ProcessIdentityFactory.FromPid(process.Id, hintName: process.ProcessName);
                            _enforcer.TryEnforce(identity, desk);
                        }
                        catch (Exception)
                        {
                            // ignore per-process
                        }
                        finally
                        {
                            process.Dispose();
                        }
                    }
                }
            }
            catch (Exception)
            {
                // keep looping
            }

            try
            {
                await Task.Delay(_interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
