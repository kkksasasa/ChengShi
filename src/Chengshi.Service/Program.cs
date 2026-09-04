using Chengshi.Engine;
using Chengshi.Service;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "Chengshi");
builder.Logging.AddProvider(new FileLoggerProvider());

// SessionHost 的构造参数全是可选的，用工厂构造，避免 DI 尝试解析内部依赖。
builder.Services.AddSingleton<SessionHost>(_ => new SessionHost());
builder.Services.AddHostedService<ChengshiWorker>();

var host = builder.Build();
await host.RunAsync();
