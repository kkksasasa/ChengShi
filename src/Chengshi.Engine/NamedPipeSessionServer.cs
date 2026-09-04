using System.IO.Pipes;
using Chengshi.Core;
using Chengshi.Ipc;

namespace Chengshi.Engine;

public static class ConfigMessageFactory
{
    /// <summary>
    /// 下发给客户端的配置不带密码哈希和找回码（见 FamilySettings.SanitizedForClient）：
    /// 管道对本机所有进程开放，哈希一旦泄露，4 位数字密码离线几分钟就能枚举完。
    /// </summary>
    public static ConfigMessage From(SessionHost host) => new(
        host.Family?.SanitizedForClient(),
        host.Desks,
        host.Budget,
        host.EtwHint,
        host.GuardHint,
        host.Snapshot,
        host.AppUsage);
}

public sealed class NamedPipeSessionServer : IDisposable
{
    private readonly SessionHost _host;
    private readonly string _pipeName;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    /// <summary>单条管道连接内的授权状态：验证过家长密码后才能改配置。</summary>
    private sealed class PipeSession
    {
        public bool Authorized { get; set; }
    }

    public NamedPipeSessionServer(SessionHost host, string? pipeName = null, Action<string>? log = null)
    {
        _host = host;
        _pipeName = pipeName ?? PipeNames.Default;
        _log = log;
    }

    public void Start()
    {
        _loop ??= Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // ignore
        }

        _cts.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = PipeFactory.CreateServer(_pipeName);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                _log?.Invoke("有客户端连上了守护管道。");
                await ServeClientAsync(server, cancellationToken).ConfigureAwait(false);
                _log?.Invoke("客户端已断开。");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                await Task.Delay(400, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                server?.Dispose();
            }
        }
    }

    private async Task ServeClientAsync(NamedPipeServerStream stream, CancellationToken cancellationToken)
    {
        var writeLock = new SemaphoreSlim(1, 1);

        async Task Send(ServerMessage message)
        {
            await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await MessageSerializer.WriteAsync(stream, message, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                writeLock.Release();
            }
        }

        async Task SendSafely(ServerMessage message)
        {
            try
            {
                await Send(message).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 客户端断开/关闭时的推送失败不算错误。
            }
        }

        void OnState(SessionSnapshot snapshot) =>
            _ = SendSafely(StateMessage.From(snapshot, string.Empty, _host.AppUsage));

        void OnBlocked(BlockedMessage blocked) =>
            _ = SendSafely(blocked);

        _host.StateChanged += OnState;
        _host.ProcessBlocked += OnBlocked;
        var session = new PipeSession();
        try
        {
            await Send(ConfigMessageFactory.From(_host)).ConfigureAwait(false);
            while (stream.IsConnected && !cancellationToken.IsCancellationRequested)
            {
                var request = await MessageSerializer.ReadAsync<ClientMessage>(stream, cancellationToken)
                    .ConfigureAwait(false);
                if (request is null)
                {
                    break;
                }

                await HandleAsync(request, Send, session).ConfigureAwait(false);
            }
        }
        finally
        {
            _host.StateChanged -= OnState;
            _host.ProcessBlocked -= OnBlocked;
        }
    }

    private async Task HandleAsync(ClientMessage request, Func<ServerMessage, Task> send, PipeSession session)
    {
        var id = request.RequestId;
        switch (request)
        {
            case HelloRequest:
                await send(new PongMessage { RequestId = id }).ConfigureAwait(false);
                await send(ConfigMessageFactory.From(_host) with { RequestId = id }).ConfigureAwait(false);
                break;
            case GetStateRequest:
                await send(StateMessage.From(_host.Tick(), _host.EtwHint, _host.AppUsage) with { RequestId = id })
                    .ConfigureAwait(false);
                break;
            case GetConfigRequest:
                await send(ConfigMessageFactory.From(_host) with { RequestId = id }).ConfigureAwait(false);
                break;
            case StartGuardRequest:
                {
                    if (!session.Authorized && _host.IsConfigured)
                    {
                        await send(new ErrorMessage("改守护相关设置前，先验证家长密码。") { RequestId = id })
                            .ConfigureAwait(false);
                        break;
                    }

                    try
                    {
                        var result = _host.StartGuard();
                        var hint = result.Status == StartSessionStatus.Started
                            ? JoinHint("守护已开始。", _host.GuardHint)
                            : "守护已经在运行。";
                        await send(new StartReply(result.Status, result.Snapshot, hint) { RequestId = id })
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        await send(new ErrorMessage(ex.Message) { RequestId = id }).ConfigureAwait(false);
                    }

                    break;
                }
            case StartSessionRequest start:
                {
                    try
                    {
                        var result = _host.Start(
                            start.DeskId,
                            TimeSpan.FromMinutes(Math.Max(1, start.DurationMinutes)),
                            start.Pinned,
                            start.Pin);
                        var hint = result.Status switch
                        {
                            StartSessionStatus.Started => "从现在起，只留这张书桌。",
                            StartSessionStatus.AlreadyRunning => "已经在书桌里。",
                            StartSessionStatus.UnknownDesk => "没有这张书桌。",
                            _ => string.Empty,
                        };
                        await send(new StartReply(result.Status, result.Snapshot, hint) { RequestId = id })
                            .ConfigureAwait(false);
                    }
                    catch (ArgumentException ex)
                    {
                        await send(new ErrorMessage(ex.Message) { RequestId = id }).ConfigureAwait(false);
                    }

                    break;
                }
            case StopSessionRequest stop:
                {
                    var result = _host.Stop(stop.Pin);
                    var hint = result.Status switch
                    {
                        StopSessionStatus.Stopped => "时间到了。桌面还你。",
                        StopSessionStatus.Idle => "现在没有书桌。",
                        StopSessionStatus.PinRequired => "钉住的书桌需要约定码。",
                        StopSessionStatus.PinRejected => "约定码不对。",
                        _ => string.Empty,
                    };
                    await send(new StopReply(result.Status, result.Snapshot, hint) { RequestId = id })
                        .ConfigureAwait(false);
                    break;
                }
            case SaveDeskRequest save:
                {
                    if (!await EnsureAuthorizedAsync(session, send, id).ConfigureAwait(false))
                    {
                        break;
                    }

                    try
                    {
                        _host.SaveDesk(save.Desk);
                        await send(ConfigMessageFactory.From(_host) with { RequestId = id }).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        await send(new ErrorMessage(ex.Message) { RequestId = id }).ConfigureAwait(false);
                    }

                    break;
                }
            case SaveFamilyRequest save:
                {
                    var wasConfigured = _host.IsConfigured;
                    if (wasConfigured && !session.Authorized)
                    {
                        await send(new ErrorMessage("修改设置前，请先用家长密码解锁。") { RequestId = id })
                            .ConfigureAwait(false);
                        break;
                    }

                    try
                    {
                        _host.SaveFamily(save.Settings);
                        if (!wasConfigured)
                        {
                            // 首次设置：刚把密码设出来的人就是家长，这条连接直接视为已授权，
                            // 否则引导流程保存后立刻「开始守护」会被门禁拦下。
                            session.Authorized = true;
                        }

                        await send(ConfigMessageFactory.From(_host) with { RequestId = id }).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        await send(new ErrorMessage(ex.Message) { RequestId = id }).ConfigureAwait(false);
                    }

                    break;
                }
            case ChangePinRequest change:
                {
                    try
                    {
                        _host.ChangePin(change.OldPin, change.NewPin);
                        session.Authorized = true;
                        await send(ConfigMessageFactory.From(_host) with { RequestId = id }).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        await send(new ErrorMessage(ex.Message) { RequestId = id }).ConfigureAwait(false);
                    }

                    break;
                }
            case RecoverPinRequest recover:
                {
                    try
                    {
                        _host.RecoverPin(recover.Token, recover.NewPin);
                        session.Authorized = true;
                        await send(ConfigMessageFactory.From(_host) with { RequestId = id }).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        await send(new ErrorMessage(ex.Message) { RequestId = id }).ConfigureAwait(false);
                    }

                    break;
                }
            case EmailRecoveryCodeRequest codeRequest:
                {
                    try
                    {
                        await _host.SendEmailRecoveryCodeAsync(codeRequest.Email).ConfigureAwait(false);
                        await send(new BoolMessage(true)
                        {
                            RequestId = id,
                        }).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        await send(new ErrorMessage(ex.Message) { RequestId = id }).ConfigureAwait(false);
                    }

                    break;
                }
            case EmailRecoveryResetRequest reset:
                {
                    try
                    {
                        var result = await _host
                            .RecoverPinWithEmailAsync(reset.Email, reset.Code, reset.NewPin)
                            .ConfigureAwait(false);
                        session.Authorized = true;
                        await send(new RecoverPinReply(true, result.Hint, result.NewRecoveryCode)
                        {
                            RequestId = id,
                        }).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        await send(new ErrorMessage(ex.Message) { RequestId = id }).ConfigureAwait(false);
                    }

                    break;
                }
            case GetSmtpRequest:
                {
                    try
                    {
                        var config = await _host.GetSmtpAsync().ConfigureAwait(false);
                        var reply = config is null
                            ? new SmtpConfigReply(string.Empty, 0, true, string.Empty, HasPassword: false)
                            : new SmtpConfigReply(
                                config.Host,
                                config.Port,
                                config.UseSsl,
                                config.User,
                                HasPassword: !string.IsNullOrEmpty(config.Password));
                        await send(reply with { RequestId = id }).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        await send(new ErrorMessage(ex.Message) { RequestId = id }).ConfigureAwait(false);
                    }

                    break;
                }
            case SaveSmtpRequest saveSmtp:
                {
                    if (!await EnsureAuthorizedAsync(session, send, id).ConfigureAwait(false))
                    {
                        break;
                    }

                    try
                    {
                        await _host.SaveSmtpAsync(saveSmtp.Config).ConfigureAwait(false);
                        await send(new BoolMessage(true) { RequestId = id }).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        await send(new ErrorMessage(ex.Message) { RequestId = id }).ConfigureAwait(false);
                    }

                    break;
                }
            case GrantExtraRequest grant:
                {
                    try
                    {
                        var result = _host.GrantExtra(grant.Pin, grant.Minutes);
                        await send(new GrantExtraReply(result.Ok, result.Hint, result.Snapshot) { RequestId = id })
                            .ConfigureAwait(false);
                        if (result.Ok)
                        {
                            // 批过加时的连接视为家长本人在操作。
                            session.Authorized = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        await send(new ErrorMessage(ex.Message) { RequestId = id }).ConfigureAwait(false);
                    }

                    break;
                }
            case VerifyPinRequest verify:
                {
                    try
                    {
                        var ok = _host.VerifyParentPin(verify.Pin);
                        if (ok)
                        {
                            session.Authorized = true;
                        }

                        await send(new BoolMessage(ok) { RequestId = id }).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        await send(new ErrorMessage(ex.Message) { RequestId = id }).ConfigureAwait(false);
                    }

                    break;
                }
        }
    }

    /// <summary>改配置的门槛：验证过家长密码的连接才放行；首次设置（还没有密码）不拦。</summary>
    private async Task<bool> EnsureAuthorizedAsync(PipeSession session, Func<ServerMessage, Task> send, int id)
    {
        if (session.Authorized || !_host.IsConfigured)
        {
            return true;
        }

        await send(new ErrorMessage("修改设置前，请先用家长密码解锁。") { RequestId = id })
            .ConfigureAwait(false);
        return false;
    }

    private static string JoinHint(params string?[] parts) =>
        string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
}
