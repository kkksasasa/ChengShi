using Chengshi.Core;
using Chengshi.Engine;

namespace Chengshi.Service;

/// <summary>把 .NET 日志转发到数据目录的文件里，方便在现场机器上还原故障经过。</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new FileLogger();

    public void Dispose()
    {
    }

    private sealed class FileLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            if (logLevel >= LogLevel.Error)
            {
                FileLog.Error("service", message, exception);
            }
            else
            {
                FileLog.Write("service", message);
            }
        }
    }
}
