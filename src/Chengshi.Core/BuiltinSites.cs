namespace Chengshi.Core;

/// <summary>
/// 「绿色上网」的预置禁止类别。类别名单是人工维护的头部站点清单，
/// 起兜底作用；按书桌勾选，家长可整类取消或补充自定义域名。
/// </summary>
public static class BuiltinSites
{
    public sealed record Category(string Name, IReadOnlyList<string> Domains);

    public static readonly IReadOnlyDictionary<string, Category> Categories =
        new Dictionary<string, Category>(StringComparer.OrdinalIgnoreCase)
        {
            ["video"] = new Category("短视频 / 直播", [
                "douyin.com",
                "kuaishou.com",
                "ixigua.com",
                "bilibili.com",
                "huya.com",
                "douyu.com",
                "twitch.tv",
                "yy.com",
                "huoshan.com",
                "zhanqi.tv",
            ]),
            ["games"] = new Category("游戏", [
                "4399.com",
                "7k7k.com",
                "2144.cn",
                "yxdown.com",
                "gamersky.com",
                "3dmgame.com",
                "youxiduo.com",
                "steamcommunity.com",
                "wanmei.com",
                "op.gg",
            ]),
            ["adult"] = new Category("成人内容", [
                "pornhub.com",
                "xvideos.com",
                "xnxx.com",
                "xhamster.com",
                "redtube.com",
                "youporn.com",
                "porn.com",
                "spankbang.com",
                "eporner.com",
                "t66y.com",
            ]),
        };

    public static Category? Find(string key) =>
        Categories.TryGetValue(key, out var category) ? category : null;

    /// <summary>书桌的最终禁止域名：自定义黑名单 + 勾选类别的预置名单，去重。</summary>
    public static IReadOnlyList<string> BlockedDomainsFor(Desk desk)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var domain in desk.BlockedSiteList)
        {
            set.Add(domain);
        }

        foreach (var key in desk.BlockCategoryList)
        {
            if (Categories.TryGetValue(key, out var category))
            {
                foreach (var domain in category.Domains)
                {
                    set.Add(domain);
                }
            }
        }

        return set.ToArray();
    }
}
