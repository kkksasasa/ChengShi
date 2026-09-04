using Chengshi.Core;
using Chengshi.Ipc;
using Microsoft.Win32;
using Xunit;

namespace Chengshi.Engine.Tests;

public class SitePolicyBuilderTests
{
    [Fact]
    public void Empty_rules_produce_empty_spec()
    {
        var spec = SitePolicyBuilder.Build([], []);
        Assert.True(spec.IsEmpty);
        Assert.Empty(spec.Blocklist);
        Assert.Empty(spec.Allowlist);
    }

    [Fact]
    public void Whitelist_mode_blocks_everything_but_allowed_domains()
    {
        var spec = SitePolicyBuilder.Build(["ke.qq.com", "xueersi.com"], []);
        Assert.Contains("*", spec.Blocklist);
        Assert.Contains("*://ke.qq.com", spec.Allowlist);
        Assert.Contains("*://*.ke.qq.com", spec.Allowlist);
        Assert.Contains("*://xueersi.com", spec.Allowlist);
        Assert.Contains("*://*.xueersi.com", spec.Allowlist);
        Assert.Equal(4, spec.Allowlist.Count);
    }

    [Fact]
    public void Blacklist_mode_only_lists_blocked_domains()
    {
        var spec = SitePolicyBuilder.Build([], ["douyin.com", "kuaishou.com"]);
        Assert.DoesNotContain("*", spec.Blocklist);
        Assert.Contains("*://douyin.com", spec.Blocklist);
        Assert.Contains("*://*.douyin.com", spec.Blocklist);
        Assert.Contains("*://kuaishou.com", spec.Blocklist);
        Assert.Empty(spec.Allowlist);
    }

    [Fact]
    public void Whitelist_wins_over_blacklist()
    {
        var spec = SitePolicyBuilder.Build(["ke.qq.com"], ["douyin.com"]);
        Assert.Contains("*", spec.Blocklist);
        Assert.DoesNotContain("*://douyin.com", spec.Allowlist);
        Assert.DoesNotContain("*://douyin.com", spec.Blocklist);
    }

    [Fact]
    public void Spec_signature_is_order_independent()
    {
        var a = SitePolicyBuilder.Build([], ["a.com", "b.com"]);
        var b = SitePolicyBuilder.Build([], ["b.com", "a.com"]);
        Assert.Equal(a.Signature, b.Signature);
    }
}

public class BuiltinSitesTests
{
    [Fact]
    public void Blocked_domains_expand_categories_and_custom_sites()
    {
        var desk = BuiltinDesks.Class().WithBlockedSites(["youku.com"]);
        var blocked = BuiltinSites.BlockedDomainsFor(desk);

        Assert.Contains("douyin.com", blocked);
        Assert.Contains("bilibili.com", blocked);
        Assert.Contains("4399.com", blocked);
        Assert.Contains("pornhub.com", blocked);
        Assert.Contains("youku.com", blocked);
        // 三个类别共 30 条 + 1 条自定义。
        Assert.True(blocked.Count >= 31);
    }

    [Fact]
    public void Unchecking_categories_shrinks_the_list()
    {
        var desk = BuiltinDesks.Class().WithBlockCategories([]);
        var blocked = BuiltinSites.BlockedDomainsFor(desk);
        Assert.Empty(blocked);
    }

    [Fact]
    public void Unknown_category_keys_are_ignored()
    {
        var desk = BuiltinDesks.Class().WithBlockCategories(["nonsense"]);
        Assert.Empty(BuiltinSites.BlockedDomainsFor(desk));
    }
}

public class DeskSiteRuleTests
{
    [Fact]
    public void NormalizeDomains_strips_scheme_path_and_dedupes()
    {
        var normalized = Desk.NormalizeDomains([
            "https://ke.qq.com/course/123",
            "KE.QQ.COM",
            "  www.bilibili.com/video/  ",
            "not-a-domain",
            "",
        ]);

        Assert.Equal(2, normalized.Count);
        Assert.Contains("ke.qq.com", normalized);
        Assert.Contains("www.bilibili.com", normalized);
    }

    [Fact]
    public void Desk_json_roundtrip_keeps_site_rules()
    {
        var desk = BuiltinDesks.Class()
            .WithAllowedSites(["ke.qq.com"])
            .WithBlockedSites(["youku.com"])
            .WithBlockCategories(["video", "adult"]);

        var json = System.Text.Json.JsonSerializer.Serialize(desk, MessageSerializer.Options);
        var copy = System.Text.Json.JsonSerializer.Deserialize<Desk>(json, MessageSerializer.Options);
        Assert.NotNull(copy);
        Assert.Contains("ke.qq.com", copy!.AllowedSiteList);
        Assert.Contains("youku.com", copy.BlockedSiteList);
        Assert.Contains("video", copy.BlockCategoryList);
        Assert.True(copy.HasSiteRules);
    }

    [Fact]
    public void WithApps_keeps_site_rules()
    {
        var desk = BuiltinDesks.Class().WithBlockCategories(["games"]);
        var updated = desk.WithApps(desk.Apps.Append(new AllowedApp("扫雷", "winmine")));
        Assert.Contains("games", updated.BlockCategoryList);
    }
}

public class SitePolicyRegistryTests : IDisposable
{
    private readonly string _rootName = @"Software\Chengshi-Test-" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _root;

    public SitePolicyRegistryTests()
    {
        _root = Registry.CurrentUser.CreateSubKey(_rootName, writable: true)!;
    }

    public void Dispose()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(_rootName);
        }
        catch (Exception)
        {
            // ignore
        }

        _root.Dispose();
    }

    [Fact]
    public void Apply_writes_blocklist_to_both_browser_keys_and_clears_afterwards()
    {
        using var guard = new SitePolicyGuard(_root);
        var spec = SitePolicyBuilder.Build([], ["douyin.com", "youku.com"]);
        Assert.True(guard.Apply(spec));
        Assert.True(guard.IsActive);

        foreach (var browser in new[] { @"SOFTWARE\Policies\Google\Chrome", @"SOFTWARE\Policies\Microsoft\Edge" })
        {
            using var key = _root.OpenSubKey(browser);
            Assert.NotNull(key);
            var blocklist = (string[])key!.GetValue("URLBlocklist")!;
            Assert.Contains("*://douyin.com", blocklist);
            Assert.Contains("*://*.youku.com", blocklist);
            Assert.Equal(1, key.GetValue("URLBlocklistEnabled"));
        }

        guard.Dispose();
        Assert.False(guard.IsActive);
        using var cleared = _root.OpenSubKey(@"SOFTWARE\Policies\Google\Chrome");
        Assert.NotNull(cleared);
        Assert.Null(cleared!.GetValue("URLBlocklist"));
        Assert.Null(cleared.GetValue("URLAllowlist"));
    }

    [Fact]
    public void Whitelist_mode_writes_allowlist_value()
    {
        using var guard = new SitePolicyGuard(_root);
        var spec = SitePolicyBuilder.Build(["ke.qq.com"], []);
        Assert.True(guard.Apply(spec));

        using var key = _root.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Edge");
        Assert.NotNull(key);
        var blocklist = (string[])key!.GetValue("URLBlocklist")!;
        var allowlist = (string[])key.GetValue("URLAllowlist")!;
        Assert.Contains("*", blocklist);
        Assert.Contains("*://*.ke.qq.com", allowlist);
    }
}

public class BedtimeTests
{
    [Fact]
    public void Overnight_window_covers_both_edges()
    {
        var family = FamilySettings.Create("1234", 60, BuiltinDesks.ClassId);
        Assert.True(family.IsBedtime(new DateTime(2026, 8, 19, 23, 30, 0)));
        Assert.True(family.IsBedtime(new DateTime(2026, 8, 20, 6, 30, 0)));
        Assert.False(family.IsBedtime(new DateTime(2026, 8, 19, 21, 59, 0)));
        Assert.False(family.IsBedtime(new DateTime(2026, 8, 20, 7, 0, 0)));
        Assert.False(family.IsBedtime(new DateTime(2026, 8, 20, 14, 0, 0)));
    }

    [Fact]
    public void Disabled_bedtime_never_blocks()
    {
        var family = FamilySettings.Create("1234", 60, BuiltinDesks.ClassId) with { BedtimeEnabled = false };
        Assert.False(family.IsBedtime(new DateTime(2026, 8, 19, 23, 0, 0)));
    }

    [Fact]
    public void Daytime_window_works()
    {
        var family = FamilySettings.Create("1234", 60, BuiltinDesks.ClassId) with
        {
            BedtimeStartHour = 9,
            BedtimeEndHour = 17,
        };
        Assert.True(family.IsBedtime(new DateTime(2026, 8, 19, 10, 0, 0)));
        Assert.False(family.IsBedtime(new DateTime(2026, 8, 19, 20, 0, 0)));
    }

    [Fact]
    public void Missing_json_fields_default_to_bedtime_on()
    {
        // 老配置没有这几个字段，反序列化后默认 22–07 且开启。
        var json = """{"pinHash":"x","dailyMinutes":60,"deskId":"class"}""";
        var family = System.Text.Json.JsonSerializer.Deserialize<FamilySettings>(json, MessageSerializer.Options);
        Assert.NotNull(family);
        Assert.True(family!.BedtimeEnabled);
        Assert.Equal(22, family.BedtimeStartHour);
        Assert.Equal(7, family.BedtimeEndHour);
        Assert.True(family.IsBedtime(new DateTime(2026, 8, 19, 23, 0, 0)));
    }
}

public class RecordingSiteGuard : SitePolicyGuard
{
    public PolicySpec? Applied { get; private set; }

    public override bool Apply(PolicySpec spec)
    {
        Applied = spec;
        return true;
    }
}

public class RecordingNetworkGuard : NetworkGuard
{
    public bool? Applied { get; private set; }

    public override bool Apply(bool block)
    {
        Applied = block;
        return true;
    }
}

public class GreenBrowsingSessionHostTests
{
    private static SessionHost CreateHost(
        TimeSpan remaining,
        RecordingNetworkGuard network,
        RecordingSiteGuard sites,
        DateTime now,
        string deskId = BuiltinDesks.ClassId)
    {
        var clock = new ManualClock();
        var calendar = new ManualCalendar();
        var familyStore = FamilyStore.Load(Path.Combine(Path.GetTempPath(), "chengshi-test-" + Guid.NewGuid().ToString("N"), "family.json"));
        familyStore.Save(FamilySettings.Create("1234", 60, deskId));
        var deskStore = DeskStore.Load(Path.Combine(Path.GetTempPath(), "chengshi-test-" + Guid.NewGuid().ToString("N"), "desks.json"));
        return new SessionHost(
            clock,
            deskStore,
            familyStore,
            calendar,
            ScreenTimeStore.Load(calendar, TimeSpan.FromMinutes(60), Path.Combine(Path.GetTempPath(), "chengshi-test-" + Guid.NewGuid().ToString("N"), "time.json")),
            enforcer: new NoopEnforcer(),
            network: network,
            sites: sites,
            now: () => now);
    }

    [Fact]
    public void Guard_at_night_blocks_network_via_bedtime()
    {
        var network = new RecordingNetworkGuard();
        var sites = new RecordingSiteGuard();
        using var host = CreateHost(TimeSpan.FromMinutes(60), network, sites, new DateTime(2026, 8, 19, 23, 0, 0));

        var result = host.StartGuard();
        Assert.Equal(StartSessionStatus.Started, result.Status);
        Assert.True(network.Applied);
        Assert.Contains("睡觉时段", host.GuardHint);
    }

    [Fact]
    public void Guard_during_day_keeps_network_and_applies_site_policy()
    {
        var network = new RecordingNetworkGuard();
        var sites = new RecordingSiteGuard();
        using var host = CreateHost(TimeSpan.FromMinutes(60), network, sites, new DateTime(2026, 8, 19, 14, 0, 0));

        host.StartGuard();
        Assert.False(network.Applied);
        Assert.NotNull(sites.Applied);
        Assert.False(sites.Applied!.IsEmpty);
        Assert.Contains("绿色上网已生效", host.GuardHint);
        Assert.Contains("*://4399.com", sites.Applied.Blocklist);
    }

    [Fact]
    public void Stopping_guard_removes_site_policy()
    {
        var network = new RecordingNetworkGuard();
        var sites = new RecordingSiteGuard();
        using var host = CreateHost(TimeSpan.FromMinutes(60), network, sites, new DateTime(2026, 8, 19, 14, 0, 0));

        host.StartGuard();
        host.Stop("1234");
        Assert.NotNull(sites.Applied);
        Assert.True(sites.Applied!.IsEmpty);
    }

    [Fact]
    public void Whitelist_desk_hint_names_allowed_sites()
    {
        var network = new RecordingNetworkGuard();
        var sites = new RecordingSiteGuard();
        using var host = CreateHost(TimeSpan.FromMinutes(60), network, sites, new DateTime(2026, 8, 19, 14, 0, 0));
        var classDesk = host.Desks.First(d => d.Id == BuiltinDesks.ClassId);
        host.SaveDesk(classDesk.WithAllowedSites(["ke.qq.com"]));

        host.StartGuard();
        Assert.Contains("网站白名单已生效", host.GuardHint);
        Assert.Contains("ke.qq.com", host.GuardHint);
        Assert.Contains("*", sites.Applied!.Blocklist);
        Assert.Contains("*://ke.qq.com", sites.Applied.Allowlist);
    }
}
