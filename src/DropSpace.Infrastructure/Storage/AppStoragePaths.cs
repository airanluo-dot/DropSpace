namespace DropSpace.Infrastructure.Storage;

public sealed class AppStoragePaths
{
    public AppStoragePaths(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        Root = Path.GetFullPath(rootPath);
        Data = Path.Combine(Root, "data");
        Payloads = Path.Combine(Root, "payloads");
        Thumbnails = Path.Combine(Root, "cache", "thumbnails");
        Backups = Path.Combine(Root, "backups");
        Logs = Path.Combine(Root, "logs");
        Quarantine = Path.Combine(Root, "quarantine");
        Database = Path.Combine(Data, "dropspace.db");
        Settings = Path.Combine(Data, "settings.json");
    }

    public string Root { get; }

    public string Data { get; }

    public string Payloads { get; }

    public string Thumbnails { get; }

    public string Backups { get; }

    public string Logs { get; }

    public string Quarantine { get; }

    public string Database { get; }

    public string Settings { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Payloads);
        Directory.CreateDirectory(Thumbnails);
        Directory.CreateDirectory(Backups);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Quarantine);
    }
}
