using Chengshi.Core;
using Chengshi.Engine;

// 诊断工具：检查守护服务是否在跑、配置是什么。
// 用法：dotnet run --project tools\Probe [管道名]
var pipeName = args.Length > 0 ? args[0] : null;
try
{
    using var client = SessionClient.Connect(TimeSpan.FromSeconds(3), pipeName);
    Console.WriteLine("已连上守护服务。");
    Console.WriteLine($"  已配置: {client.IsConfigured}");
    Console.WriteLine($"  守护中: {client.IsGuarding}");
    Console.WriteLine($"  书桌数: {client.Desks.Count}");
    Console.WriteLine($"  ETW:    {client.EtwHint}");
    Console.WriteLine($"  提示:   {client.GuardHint}");
    Console.WriteLine($"  状态:   {client.Snapshot.Phase}, 剩余 {client.Snapshot.Remaining}, 家长模式 {client.Snapshot.Parental}");
    if (client.Family is { } family)
    {
        Console.WriteLine($"  每天:   {family.DailyMinutes} 分钟, 默认书桌 {family.DeskId}");
    }
}
catch (Exception ex)
{
    Console.WriteLine("没连上守护服务：" + ex.Message);
    Console.WriteLine("服务没装/没起时，澄时界面会用本机守护（断网、防强杀不生效）。");
    Environment.ExitCode = 1;
}
