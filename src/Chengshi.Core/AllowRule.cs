namespace Chengshi.Core;

public abstract record AllowRule;

public sealed record FileNameAllowRule : AllowRule
{
    public FileNameAllowRule(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        var name = Path.GetFileName(fileName.Trim());
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name += ".exe";
        }

        FileName = name;
    }

    public string FileName { get; }
}

public sealed record PathAllowRule : AllowRule
{
    public PathAllowRule(string pathPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathPrefix);
        PathPrefix = pathPrefix.Trim();
    }

    public string PathPrefix { get; }
    public bool IsDirectory => PathPrefix.EndsWith('\\') || PathPrefix.EndsWith('/');
}

public sealed record PublisherAllowRule : AllowRule
{
    public PublisherAllowRule(string publisher)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publisher);
        Publisher = publisher.Trim();
    }

    public string Publisher { get; }
}

public sealed record PackageFamilyAllowRule : AllowRule
{
    public PackageFamilyAllowRule(string packageFamilyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageFamilyName);
        PackageFamilyName = packageFamilyName.Trim();
    }

    public string PackageFamilyName { get; }
}
