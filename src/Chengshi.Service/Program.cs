using Chengshi.Engine;
using Chengshi.Service;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "Chengshi");
builder.Logging.AddProvider(new FileLoggerProvider());

// SessionHost 的构造参数全是可选的，用工厂构造，避免 DI 尝试解析内部依赖。
// 锁屏探测只在真实宿主启用：测试注入 Null 探测器保证记账口径确定。
builder.Services.AddSingleton<SessionHost>(_ => new SessionHost(lockProbe: WtsWorkstationLockProbe.Instance));
builder.Services.AddHostedService<ChengshiWorker>();

var host = builder.Build();
await host.RunAsync();
