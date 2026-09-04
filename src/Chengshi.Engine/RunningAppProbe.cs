using System.Diagnostics;
using Chengshi.Core;

namespace Chengshi.Engine;

/// <summary>
/// 找出「当前书桌里被允许、而且此刻正在运行」的软件。只用于用量记账，不做拦截。
/// </summary>
public interface IRunningAppProbe
{
    /// <summary>返回正在运行的软件的 Key（= AllowedApp.Key）。</summary>
    IReadOnlyCollection<string> RunningKeys(Desk desk);
}

/// <summary>
/// 按进程名匹配允许名单。只统计当前交互会话的进程——
/// 与拦截逻辑保持一致：其他登录用户和系统服务的进程不算孩子在用。
/// </summary>
public sealed class ProcessRunningAppProbe : IRunningAppProbe
{
    public IReadOnlyCollection<string> RunningKeys(Desk desk)
    {
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byStem = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in desk.Apps)
        {
            byStem[Stem(app.FileName)] = app.Key;
        }

        if (byStem.Count == 0)
        {
            return running;
        }

        var active = ActiveSession.ConsoleSessionId;
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.Id == Environment.ProcessId)
                {
                    continue;
                }

                // 会话 0（服务）与取不到控制台会话时都不记账，避免把系统进程算到孩子头上。
                if (active == 0 || process.SessionId != active)
                {
                    continue;
                }

                if (byStem.TryGetValue(process.ProcessName, out var key))
                {
                    running.Add(key);
                }
            }
            catch (Exception)
            {
                // 进程瞬时退出，跳过。
            }
            finally
            {
                process.Dispose();
            }
        }

        return running;
    }

    private static string Stem(string fileName) =>
        Path.GetFileNameWithoutExtension(fileName.Trim());
}
