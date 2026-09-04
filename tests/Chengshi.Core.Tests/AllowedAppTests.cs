using System.Text.Json;
using System.Text.Json.Serialization;
using Chengshi.Core;
using Xunit;

namespace Chengshi.Core.Tests;

public class AllowedAppTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [Fact]
    public void File_name_gains_exe_suffix()
    {
        var app = new AllowedApp("Word", "winword");
        Assert.Equal("winword.exe", app.FileName);
        Assert.Equal("winword.exe", app.Key);
    }

    [Fact]
    public void Path_rule_is_emitted_when_image_path_exists()
    {
        var app = new AllowedApp("editor", "editor.exe", @"C:\Work\Notes\editor.exe");
        var rules = app.ToRules().ToArray();
        Assert.Contains(rules, r => r is FileNameAllowRule f && f.FileName == "editor.exe");
        Assert.Contains(rules, r => r is PathAllowRule p && p.PathPrefix == @"C:\Work\Notes\editor.exe");
    }

    [Fact]
    public void Distinct_keeps_last_app_for_same_key()
    {
        var apps = Desk.Distinct(
        [
            new AllowedApp("Old", "code.exe"),
            new AllowedApp("VS Code", "Code.exe"),
        ]);
        Assert.Single(apps);
        Assert.Equal("VS Code", apps[0].DisplayName);
    }

    [Fact]
    public void Summarize_uses_display_names()
    {
        var apps = new[]
        {
            new AllowedApp("Word", "winword"),
            new AllowedApp("Excel", "excel"),
        };
        Assert.Equal("Excel、Word", Desk.Summarize(Desk.Distinct(apps)));
    }

    [Fact]
    public void Desk_json_roundtrip_keeps_apps()
    {
        var desk = new Desk("homework", "写作业", "文档", [new AllowedApp("Word", "winword")], DisconnectNetwork: true);
        var json = JsonSerializer.Serialize(desk, Json);
        var copy = JsonSerializer.Deserialize<Desk>(json, Json);
        Assert.NotNull(copy);
        Assert.Equal("homework", copy.Id);
        Assert.True(copy.DisconnectNetwork);
        Assert.Single(copy.Apps);
        Assert.Equal("winword.exe", copy.Apps[0].FileName);
        Assert.Contains(copy.Rules, r => r is FileNameAllowRule);
    }
}
