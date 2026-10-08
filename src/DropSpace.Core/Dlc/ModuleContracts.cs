using System.Text.Json;

namespace DropSpace.Core.Dlc;

// Contract versions are independent of the App release number. Unknown required versions fail closed.
public sealed record ModuleCapability(string Id, int Version, string? Fallback = null);
public sealed record ModuleDependency(string Id, string MinimumVersion, string MaximumVersionExclusive);
public sealed record ModuleFile(string Path, long Bytes, string Sha256);
public sealed record ModuleText(string Key);
public sealed record ModuleAction(string Id, ModuleText Label);
public sealed record ModuleSetting(string Id, ModuleText Label, string Kind, string DefaultValue);
public sealed record ModulePage(string Id, ModuleText Title, ModuleText Description, ModuleAction[] Actions);
public sealed record ModuleUi(string Icon, ModulePage[] Pages, ModuleSetting[] Settings);
public sealed record ModuleDataFormat(int Version, int MinimumReadableVersion, int MaximumReadableVersion);
public sealed record ModuleManifest
{
    public int ManifestVersion { get; init; } = 1;
    public required string Id { get; init; }
    public required string Version { get; init; }
    public int MinimumHostInterface { get; init; } = 1;
    public int MaximumHostInterface { get; init; } = 1;
    public int ProtocolVersion { get; init; } = 1;
    public int UiVersion { get; init; } = 1;
    public string System { get; init; } = "windows";
    public string Architecture { get; init; } = "x64";
    public int MinimumWindowsBuild { get; init; } = 20348;
    public required string EntryPoint { get; init; }
    public required ModuleText Name { get; init; }
    public ModuleCapability[] RequiredCapabilities { get; init; } = [];
    public ModuleCapability[] OptionalCapabilities { get; init; } = [];
    public ModuleDependency[] Dependencies { get; init; } = [];
    public ModuleDataFormat Data { get; init; } = new(1, 1, 1);
    public ModuleUi Ui { get; init; } = new("Puzzle", [], []);
    public Dictionary<string, Dictionary<string, string>> Resources { get; init; } = [];
    public ModuleFile[] Files { get; init; } = [];
    public string Publisher { get; init; } = "airanluo-dot/DropSpace";
}

// This is an embedded official allowlist, not a trust assertion read from the downloaded manifest.
public sealed record OfficialModulePackage(string Id, string Version, string Url, long Bytes, string Sha256);
public enum ModuleRunState { Stopped, Starting, Running, Stopping, Faulted }
public enum ModuleTransactionState { None, Downloading, Preparing, Activating, Cleaning, PendingCleanup }
public sealed record ModuleInstallation(string Id, string? Version, bool Enabled, int DataVersion,
    ModuleTransactionState Transaction = ModuleTransactionState.None, string? PreviousVersion = null,
    string? CandidateVersion = null, string? ErrorCode = null);
public sealed record ModuleSnapshot(ModuleInstallation Installation, ModuleRunState RunState, ModuleManifest? Manifest);
public sealed record ModuleIslandContent(ModuleText Compact, ModuleText Expanded, ModuleAction[] Actions, DateTimeOffset ExpiresAt);
public sealed record ModuleReply(JsonElement Result, ModuleIslandContent? Island = null);
public sealed record ModuleEnvelope(int Protocol, string Session, string Id, string Method, JsonElement Payload,
    string? ErrorCode = null);

public static class ModuleContract
{
    public const int HostInterface = 1, Protocol = 1, MaximumMessageBytes = 65536;
    public static readonly IReadOnlyDictionary<string, int> Capabilities = new Dictionary<string, int>
    {
        ["ui.pages"] = 1, ["ui.settings"] = 1, ["island.content"] = 1,
        ["module.data"] = 1,
    };
    public static readonly string[] Languages = ["en-US", "zh-CN", "zh-TW", "ja-JP", "ko-KR", "de-DE", "fr-FR", "es-ES", "pt-BR", "ru-RU"];
    public static bool ValidId(string value) => value.Length is > 0 and <= 80 &&
        value[0] is >= 'a' and <= 'z' && value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.');
    public static bool ValidVersion(string value) => System.Version.TryParse(value, out var parsed) && parsed.Revision == -1 &&
        parsed.Build >= 0 && value == parsed.ToString(3);
    public static string Text(ModuleManifest manifest, ModuleText text, string language)
    {
        if (manifest.Resources.TryGetValue(language, out var localized) && localized.TryGetValue(text.Key, out var value)) return value;
        return manifest.Resources["en-US"].TryGetValue(text.Key, out value) ? value : text.Key;
    }
    public static void Validate(ModuleManifest manifest, IReadOnlyDictionary<string, ModuleInstallation> installed, int windowsBuild)
    {
        if (manifest.ManifestVersion != 1 || manifest.ProtocolVersion != Protocol || manifest.UiVersion != 1 ||
            manifest.MinimumHostInterface > HostInterface || manifest.MaximumHostInterface < HostInterface)
            throw new InvalidDataException("HostInterfaceRequired:" + manifest.MinimumHostInterface);
        if (!ValidId(manifest.Id) || !ValidVersion(manifest.Version) || manifest.Publisher != "airanluo-dot/DropSpace" ||
            manifest.System != "windows" || manifest.Architecture != "x64" || manifest.MinimumWindowsBuild > windowsBuild)
            throw new InvalidDataException("ModuleIncompatible");
        foreach (var capability in manifest.RequiredCapabilities)
            if (!Capabilities.TryGetValue(capability.Id, out var version) || version < capability.Version || capability.Version < 1)
                throw new InvalidDataException("RequiredCapabilityMissing:" + capability.Id);
        foreach (var capability in manifest.OptionalCapabilities)
            if (capability.Version < 1 || (!Capabilities.TryGetValue(capability.Id, out var version) || version < capability.Version) &&
                capability.Fallback != "omit") throw new InvalidDataException("OptionalFallbackRequired");
        foreach (var dependency in manifest.Dependencies)
            if (dependency.Id == manifest.Id || !ValidId(dependency.Id) || !ValidVersion(dependency.MinimumVersion) ||
                !ValidVersion(dependency.MaximumVersionExclusive) || !installed.TryGetValue(dependency.Id, out var entry) ||
                entry.Version is null || System.Version.Parse(entry.Version) < System.Version.Parse(dependency.MinimumVersion) ||
                System.Version.Parse(entry.Version) >= System.Version.Parse(dependency.MaximumVersionExclusive))
                throw new InvalidDataException("DependencyUnavailable:" + dependency.Id);
        if (manifest.Data.Version < 1 || manifest.Data.MinimumReadableVersion < 1 ||
            manifest.Data.MinimumReadableVersion > manifest.Data.Version || manifest.Data.MaximumReadableVersion < manifest.Data.Version)
            throw new InvalidDataException("InvalidDataFormat");
        if (manifest.Files.Length is < 1 or > 128 || !manifest.Files.Any(f => f.Path == manifest.EntryPoint) ||
            !manifest.EntryPoint.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            manifest.Ui.Pages.Length > 8 || manifest.Ui.Settings.Length > 32 ||
            manifest.Ui.Icon is not ("Puzzle" or "Document" or "Tools") ||
            manifest.Ui.Pages.Select(p => p.Id).Distinct().Count() != manifest.Ui.Pages.Length ||
            manifest.Ui.Settings.Select(p => p.Id).Distinct().Count() != manifest.Ui.Settings.Length)
            throw new InvalidDataException("InvalidModuleDeclaration");
        var texts = new List<ModuleText> { manifest.Name };
        foreach (var page in manifest.Ui.Pages)
        {
            if (!ValidId(page.Id) || page.Actions.Length > 8 || page.Actions.Select(a => a.Id).Distinct().Count() != page.Actions.Length)
                throw new InvalidDataException("InvalidPage");
            texts.Add(page.Title); texts.Add(page.Description);
            foreach (var action in page.Actions) { if (!ValidId(action.Id)) throw new InvalidDataException("InvalidAction"); texts.Add(action.Label); }
        }
        foreach (var setting in manifest.Ui.Settings)
        {
            if (!ValidId(setting.Id) || setting.Kind is not ("text" or "boolean") || setting.DefaultValue.Length > 4096 ||
                setting.Kind == "boolean" && setting.DefaultValue is not ("true" or "false")) throw new InvalidDataException("InvalidSetting");
            texts.Add(setting.Label);
        }
        foreach (var language in Languages)
        {
            if (!manifest.Resources.TryGetValue(language, out var catalog) || catalog.Count is < 1 or > 256 ||
                texts.Any(text => !catalog.TryGetValue(text.Key, out var value) || string.IsNullOrWhiteSpace(value)) ||
                catalog.Any(pair => pair.Key.Length is < 1 or > 128 || pair.Value.Length > 4096))
                throw new InvalidDataException("ModuleTranslationMissing:" + language);
        }
        if (manifest.Ui.Pages.Length > 0 && !HasCapability(manifest, "ui.pages") ||
            manifest.Ui.Settings.Length > 0 && !HasCapability(manifest, "ui.settings")) throw new InvalidDataException("UndeclaredUiCapability");
    }
    public static bool HasCapability(ModuleManifest manifest, string id) =>
        manifest.RequiredCapabilities.Concat(manifest.OptionalCapabilities).Any(c => c.Id == id &&
            Capabilities.TryGetValue(id, out var version) && c.Version >= 1 && c.Version <= version);
}
