namespace DropSpace.Infrastructure.Storage;

public sealed class AppStoragePaths
{
    public static AppStoragePaths CreateForCurrentUser()
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException("The current user's local application data folder is unavailable.");
        }

        return new AppStoragePaths(Path.Combine(localAppData, "DropSpace"));
    }

    public AppStoragePaths(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        Root = Path.GetFullPath(rootPath);
        Data = Path.Combine(Root, "data");
        Payloads = Path.Combine(Root, "payloads");
        Exports = Path.Combine(Root, "exports");
        Thumbnails = Path.Combine(Root, "cache", "thumbnails");
        Previews = Path.Combine(Root, "cache", "previews");
        Backups = Path.Combine(Root, "backups");
        Logs = Path.Combine(Root, "logs");
        Quarantine = Path.Combine(Root, "quarantine");
        Updates = Path.Combine(Root, "Updates");
        Staging = Path.Combine(Root, "staging");
        Database = Path.Combine(Data, "dropspace.db");
        Settings = Path.Combine(Data, "settings.json");
    }

    public string Root { get; }

    public string Data { get; }

    public string Payloads { get; }

    public string Exports { get; }

    public string Thumbnails { get; }

    public string Previews { get; }

    public string Backups { get; }

    public string Logs { get; }

    public string Quarantine { get; }

    public string Updates { get; }

    public string Staging { get; }

    public string Database { get; }

    public string Settings { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Payloads);
        Directory.CreateDirectory(Exports);
        Directory.CreateDirectory(Thumbnails);
        Directory.CreateDirectory(Previews);
        Directory.CreateDirectory(Backups);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Quarantine);
        Directory.CreateDirectory(Updates);
        Directory.CreateDirectory(Staging);
    }
}
