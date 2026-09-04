using Chengshi.Core;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

namespace Chengshi.Engine;

public sealed class EtwProcessWatcher : IDisposable
{
    private readonly IProcessEnforcer _enforcer;
    private readonly Func<Desk?> _currentDesk;
    private readonly string _sessionName;
    private TraceEventSession? _session;
    private Thread? _thread;
    private volatile bool _running;

    public EtwProcessWatcher(IProcessEnforcer enforcer, Func<Desk?> currentDesk, string? sessionName = null)
    {
        _enforcer = enforcer;
        _currentDesk = currentDesk;
        _sessionName = sessionName ?? "Chengshi-Kernel-Process";
    }

    public bool IsRunning => _running;
    public string? LastError { get; private set; }

    public bool TryStart()
    {
        if (_running)
        {
            return true;
        }

        if (!(TraceEventSession.IsElevated() ?? false))
        {
            LastError = "ETW 内核会话需要管理员权限，已回退到轮询。";
            return false;
        }

        try
        {
            _session = new TraceEventSession(_sessionName);
            _session.EnableKernelProvider(KernelTraceEventParser.Keywords.Process);
            _session.Source.Kernel.ProcessStart += OnProcessStart;
            _running = true;
            _thread = new Thread(ProcessSource)
            {
                IsBackground = true,
                Name = "Chengshi-ETW",
            };
            _thread.Start();
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            DisposeSession();
            _running = false;
            return false;
        }
    }

    public void Dispose()
    {
        _running = false;
        DisposeSession();
        if (_thread is { IsAlive: true } && !_thread.Join(TimeSpan.FromSeconds(2)))
        {
            // 会话已停，线程应随 Process() 返回。
        }
    }

    private void ProcessSource()
    {
        try
        {
            _session?.Source.Process();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _running = false;
        }
    }

    private void OnProcessStart(ProcessTraceData data)
    {
        try
        {
            var desk = _currentDesk();
            if (desk is null)
            {
                return;
            }

            var identity = ProcessIdentityFactory.FromPid(
                data.ProcessID,
                data.ParentID,
                data.ImageFileName);
            _enforcer.TryEnforce(identity, desk);
        }
        catch (Exception)
        {
            // 回调里不能抛。
        }
    }

    private void DisposeSession()
    {
        try
        {
            _session?.Dispose();
        }
        catch (Exception)
        {
            // ignore
        }

        _session = null;
    }
}
