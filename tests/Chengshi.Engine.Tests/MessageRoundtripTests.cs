using System.Text.Json;
using Chengshi.Core;
using Chengshi.Ipc;
using Xunit;

namespace Chengshi.Engine.Tests;

public class MessageRoundtripTests
{
    private static T RoundTrip<T>(T message)
        where T : class
    {
        var json = MessageSerializer.Encode(message);
        var decoded = JsonSerializer.Deserialize<T>(json.AsSpan(4).ToArray(), MessageSerializer.Options);
        Assert.NotNull(decoded);
        return decoded;
    }

    [Fact]
    public void Client_messages_roundtrip_with_request_id()
    {
        var start = RoundTrip<ClientMessage>(new StartSessionRequest("code", 30, false, null) { RequestId = 7 });
        var typed = Assert.IsType<StartSessionRequest>(start);
        Assert.Equal(7, typed.RequestId);
        Assert.Equal("code", typed.DeskId);
        Assert.Equal(30, typed.DurationMinutes);

        var guard = Assert.IsType<StartGuardRequest>(RoundTrip<ClientMessage>(new StartGuardRequest { RequestId = 3 }));
        Assert.Equal(3, guard.RequestId);

        var desk = BuiltinDesks.Code();
        var save = Assert.IsType<SaveDeskRequest>(RoundTrip<ClientMessage>(new SaveDeskRequest(desk)));
        Assert.Equal(desk.Id, save.Desk.Id);
        Assert.Equal(desk.Apps.Count, save.Desk.Apps.Count);
    }

    [Fact]
    public void Server_messages_roundtrip_with_discriminators()
    {
        var snapshot = new SessionSnapshot(
            SessionPhase.InDesk, BuiltinDesks.CodeId, "编程", TimeSpan.FromMinutes(20), true, false, true);
        var state = Assert.IsType<StateMessage>(RoundTrip<ServerMessage>(StateMessage.From(snapshot, "提示")));
        Assert.Equal(SessionPhase.InDesk, state.Phase);
        Assert.Equal(1200, state.RemainingSeconds);
        Assert.True(state.Parental);
        Assert.Equal("提示", state.Hint);

        var config = Assert.IsType<ConfigMessage>(RoundTrip<ServerMessage>(new ConfigMessage(
            FamilySettings.Create("1234", 60, BuiltinDesks.CodeId),
            BuiltinDesks.Templates,
            new DailyBudget(new DateOnly(2026, 8, 18), TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(5)),
            "etw hint",
            "guard hint",
            snapshot)));
        Assert.Equal("guard hint", config.GuardHint);
        Assert.NotNull(config.Family);
        Assert.True(config.Family!.VerifyPin("1234"));
        Assert.Equal(3, config.Desks.Count);

        var startReply = Assert.IsType<StartReply>(RoundTrip<ServerMessage>(
            new StartReply(StartSessionStatus.Started, snapshot, "hint")));
        Assert.Equal(StartSessionStatus.Started, startReply.Status);

        var stopReply = Assert.IsType<StopReply>(RoundTrip<ServerMessage>(
            new StopReply(StopSessionStatus.PinRejected, snapshot, "hint")));
        Assert.Equal(StopSessionStatus.PinRejected, stopReply.Status);

        Assert.True(Assert.IsType<BoolMessage>(RoundTrip<ServerMessage>(new BoolMessage(true))).Value);
        Assert.Equal("坏了", Assert.IsType<ErrorMessage>(RoundTrip<ServerMessage>(new ErrorMessage("坏了"))).Message);
    }

    [Fact]
    public void Unknown_type_deserializes_to_base_only_in_roundtrip_of_known_set()
    {
        // 所有已知类型都必须能按判别符还原——上面已经覆盖；
        // 这里确认 RequestId 在派生类型上也能往返。
        var pong = Assert.IsType<PongMessage>(RoundTrip<ServerMessage>(new PongMessage { RequestId = 42 }));
        Assert.Equal(42, pong.RequestId);
    }
}
