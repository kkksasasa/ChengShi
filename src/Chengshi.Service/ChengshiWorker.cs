using Chengshi.Engine;

namespace Chengshi.Service;

public sealed class ChengshiWorker : BackgroundService
{
    private readonly SessionHost _host;
    private readonly ILogger<ChengshiWorker> _logger;

    public ChengshiWorker(SessionHost host, ILogger<ChengshiWorker> logger)
    {
        _host = host;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        StorePaths.EnsureConfigured();
        // 数据目录收紧成 Users 只读：孩子账号改不了 desks/family 配置。
        // 放在服务启动时做（SYSTEM 有权改 ACL），安装脚本的 icacls 只是第一道。
        StorePaths.EnsureDataDirHardened();
        _logger.LogInformation("澄时守护服务已启动。{EtwHint}", _host.EtwHint);
        using var pipe = new NamedPipeSessionServer(
            _host,
            log: message => _logger.LogInformation("{Message}", message));
        pipe.Start();

        // 开机守护：孩子重启电脑也逃不掉（除非家长在应用里暂停）。
        if (_host.Family?.GuardOnLaunch == true)
        {
            try
            {
                var result = _host.StartGuard();
                _logger.LogInformation("开机守护：{Status}。{GuardHint}", result.Status, _host.GuardHint);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "开机守护失败。");
            }
        }

        var ticks = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                _host.Tick();

                // 界面程序走管道时这里总是最新；只有它没连上服务、自己改磁盘时，
                // 才需要热刷新跟上。每 5 秒核对一次，开销可忽略。
                if (++ticks % 5 == 0)
                {
                    _host.RefreshFromDisk();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }
}
