using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using DropSpace.App.Services.Agent;
using DropSpace.Core.Agent;
using DropSpace.Infrastructure.Storage;

var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
var root = Path.Combine(repository, "artifacts", "1.1-beta1-agent", "skill-export-" + Guid.NewGuid().ToString("N"));
var service = new AgentSkillPackageService(new AppStoragePaths(root));
var checks = new List<string>();
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidDataException(description);
    checks.Add(description);
}

async Task WriteReceiptAsync(string boundary)
{
    var receipt = JsonSerializer.Serialize(new { passed = true, checkedAt = DateTimeOffset.UtcNow, runtime = Environment.Version.ToString(), checks, boundary, root },
        new JsonSerializerOptions { WriteIndented = true });
    await File.WriteAllTextAsync(Path.Combine(root, "receipt.json"), receipt);
    Console.WriteLine(receipt);
}

if (args.Contains("--prompt-only", StringComparer.Ordinal))
{
    const string fixtureKey = "fixture-key-not-a-credential";
    var invitation = new AppAgentPairingInvitation(Guid.Parse("981776e3-73cf-4227-b874-379dd5da8fc3"),
        new Uri("http://127.0.0.1:12345"), fixtureKey, DateTimeOffset.UtcNow.AddMinutes(1));
    var endpointFile = Path.Combine(root, "Agent", "endpoint.json");
    var initialCulture = CultureInfo.CurrentCulture;
    var initialUiCulture = CultureInfo.CurrentUICulture;
    var prompts = new List<string>();
    try
    {
        foreach (var language in new[] { "en-US", "zh-CN", "ar-SA" })
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
            prompts.Add(await service.CreateInstallPromptAsync(invitation, endpointFile));
        }
    }
    finally { CultureInfo.CurrentCulture = initialCulture; CultureInfo.CurrentUICulture = initialUiCulture; }
    Check(prompts.Distinct(StringComparer.Ordinal).Count() == 1, "Install prompt is identical under English, Chinese and Arabic cultures");
    Check(prompts[0].StartsWith("Read " + service.SkillDirectory + "/SKILL.md and install this skill", StringComparison.Ordinal) &&
        prompts[0].EndsWith("This request authorizes skill installation and pairing only, not other App actions.", StringComparison.Ordinal),
        "Install prompt retains the fixed English template and installation-only scope");
    Check(prompts[0].Contains("{\"pairingKey\":\"" + fixtureKey + "\"}", StringComparison.Ordinal) &&
        prompts[0].Contains("-BindingId " + invitation.BindingId.ToString("D"), StringComparison.Ordinal) &&
        prompts[0].Contains("-EndpointFile \"" + endpointFile + "\"", StringComparison.Ordinal),
        "JSON braces, binding ID and endpoint path format correctly without localization");
    Check(Assembly.GetExecutingAssembly().GetManifestResourceNames().Count(name => name.StartsWith("DropSpace.AgentSkill/", StringComparison.Ordinal)) == 6,
        "Fixed prompt does not add a seventh product skill resource");
    await WriteReceiptAsync("Targeted fixed-English prompt checks against the linked production service; no WinUI launch, no live invitation.");
    return;
}

await service.ExportAsync();
var assembly = Assembly.GetExecutingAssembly();
var resources = assembly.GetManifestResourceNames().Where(name => name.StartsWith("DropSpace.AgentSkill/", StringComparison.Ordinal)).ToArray();
Check(resources.Length == 6, "Exactly six skill files embedded with the App resource naming pattern");
foreach (var name in resources)
{
    var relative = name["DropSpace.AgentSkill/".Length..].Replace('\\', '/');
    using var source = assembly.GetManifestResourceStream(name)!;
    var expectedHash = await SHA256.HashDataAsync(source);
    using var output = File.OpenRead(Path.Combine(service.SkillDirectory, relative));
    var actualHash = await SHA256.HashDataAsync(output);
    Check(expectedHash.AsSpan().SequenceEqual(actualHash), "Export hash matches embedded resource: " + relative);
}

var skillFile = Path.Combine(service.SkillDirectory, "SKILL.md");
var previousWrite = File.GetLastWriteTimeUtc(skillFile);
await service.ExportAsync();
Check(File.GetLastWriteTimeUtc(skillFile) == previousWrite, "Unchanged exports are not rewritten");
var legacyStage = skillFile + ".new";
await File.WriteAllTextAsync(legacyStage, "Leave pre-existing temporary files untouched.");
await using (var large = new FileStream(skillFile, FileMode.Create, FileAccess.Write, FileShare.None))
    large.SetLength(64L * 1024 * 1024);
await service.ExportAsync();
Check(new FileInfo(skillFile).Length < 1024 * 1024 && (await File.ReadAllTextAsync(skillFile)).StartsWith("---", StringComparison.Ordinal),
    "A 64 MiB pre-existing file is replaced with the bounded bundled content");
Check(await File.ReadAllTextAsync(legacyStage) == "Leave pre-existing temporary files untouched.", "A pre-existing .new path is never overwritten");
Check(!Directory.EnumerateFiles(service.SkillDirectory, "*.new-*", SearchOption.AllDirectories).Any(), "Owned staging files are cleaned after export");
using var cancelled = new CancellationTokenSource();
cancelled.Cancel();
try { await service.ExportAsync(cancelled.Token); throw new InvalidDataException("Canceled export was accepted."); }
catch (OperationCanceledException) { checks.Add("Canceled export rejects admission"); }
await WriteReceiptAsync("Real linked export service and real embedded asset bytes in an isolated net10.0 console fixture; no WinUI launch.");
