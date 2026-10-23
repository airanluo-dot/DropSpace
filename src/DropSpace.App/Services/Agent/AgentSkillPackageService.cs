using System.Globalization;
using System.Reflection;
using DropSpace.Core.Agent;
using DropSpace.Infrastructure.Storage;

namespace DropSpace.App.Services.Agent;

/// <summary>Exports only bundled, fixed skill resources. Pairing secrets are never written to its files.</summary>
public sealed class AgentSkillPackageService(AppStoragePaths paths)
{
    private const string ResourcePrefix = "DropSpace.AgentSkill/";
    private const int MaximumResourceBytes = 1024 * 1024;
    private const string InstallPromptTemplate = """
        Read {0}/SKILL.md and install this skill in your supported local skill directory. If skills are unsupported, use its PowerShell client. Pair local DropSpace once using scripts/Invoke-DropSpace.ps1 -Action Pair -BindingId {2} -EndpointFile "{3}". Send {{"pairingKey":"{1}"}} as UTF-8 JSON on stdin. Keep the key out of command-line arguments, files, logs and reusable instructions. Afterwards use this binding’s saved credential and discover the command catalog. This request authorizes skill installation and pairing only, not other App actions.
        """;
    private static readonly string[] RequiredFiles =
    [
        "SKILL.md", "agents/openai.yaml", "scripts/Invoke-DropSpace.ps1",
        "scripts/dropspace-mcp.mjs", "references/connection.md", "references/commands.md",
    ];
    private readonly SemaphoreSlim _export = new(1, 1);
    public string SkillDirectory => Path.Combine(paths.Root, "Agent", "Skill", "dropspace-agent");

    public async Task<string> CreateInstallPromptAsync(AppAgentPairingInvitation invitation, string discoveryFile,
        CancellationToken cancellationToken = default)
    {
        await ExportAsync(cancellationToken);
        return string.Format(CultureInfo.InvariantCulture, InstallPromptTemplate, SkillDirectory, invitation.PairingKey,
            invitation.BindingId.ToString("D"), discoveryFile);
    }

    public async Task ExportAsync(CancellationToken cancellationToken = default)
    {
        await _export.WaitAsync(cancellationToken);
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var names = assembly.GetManifestResourceNames().Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal)).ToArray();
            var relativeNames = names.Select(name => name[ResourcePrefix.Length..].Replace('\\', '/')).ToArray();
            if (relativeNames.Length != RequiredFiles.Length || !relativeNames.Order(StringComparer.Ordinal).SequenceEqual(RequiredFiles.Order(StringComparer.Ordinal)))
                throw new InvalidDataException("The bundled Agent skill inventory is incomplete or unexpected.");
            foreach (var name in names)
            {
                var relative = name[ResourcePrefix.Length..].Replace('\\', '/');
                if (relative.Split('/').Any(part => part is "" or "." or ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                    throw new InvalidDataException("Invalid bundled skill path.");
                var destination = Path.GetFullPath(Path.Combine(SkillDirectory, relative));
                if (!destination.StartsWith(SkillDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Invalid bundled skill destination.");
                for (var current = new DirectoryInfo(Path.GetDirectoryName(destination)!); current is not null; current = current.Parent)
                    if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        throw new IOException("The skill directory cannot traverse a reparse point.");
                if (File.Exists(destination) && File.GetAttributes(destination).HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException("The skill file cannot be a reparse point.");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var resource = assembly.GetManifestResourceStream(name) ?? throw new InvalidDataException("Missing bundled skill file.");
                if (resource.Length is <= 0 or > MaximumResourceBytes) throw new InvalidDataException("Bundled skill file exceeds its size bound.");
                var bytes = new byte[checked((int)resource.Length)];
                await resource.ReadExactlyAsync(bytes, cancellationToken);
                if (await MatchesExistingAsync(destination, bytes, cancellationToken)) continue;
                var staged = destination + ".new-" + Guid.NewGuid().ToString("N");
                var stagedOwned = false;
                try
                {
                    // CreateNew avoids following or truncating a pre-existing temporary
                    // file/hard link. Only this newly owned path is cleaned up below.
                    await using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                        8192, FileOptions.Asynchronous))
                    {
                        stagedOwned = true;
                        await output.WriteAsync(bytes, cancellationToken);
                        await output.FlushAsync(cancellationToken);
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Move(staged, destination, overwrite: true);
                }
                finally
                {
                    try { if (stagedOwned && File.Exists(staged)) File.Delete(staged); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                }
            }
        }
        finally { _export.Release(); }
    }

    private static async Task<bool> MatchesExistingAsync(string destination, byte[] expected, CancellationToken cancellationToken)
    {
        if (!File.Exists(destination)) return false;
        await using var input = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read, 8192,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        // A maliciously enlarged existing file is never allocated or read in full.
        if (input.Length != expected.Length) return false;
        var buffer = new byte[8192];
        var offset = 0;
        while (offset < expected.Length)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, expected.Length - offset)), cancellationToken);
            if (read == 0 || !buffer.AsSpan(0, read).SequenceEqual(expected.AsSpan(offset, read))) return false;
            offset += read;
        }
        return true;
    }
}
