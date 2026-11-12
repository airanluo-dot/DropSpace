using System.Reflection;

namespace DropSpace.App.Services;

internal static class LocalPreview
{
    // A worktree can opt its builds into a private profile. The identity travels with the exe.
    private static readonly Guid? Id = typeof(LocalPreview).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Where(attribute => attribute.Key == "DropSpaceLocalPreviewId")
        .Select(attribute => (Guid?)Guid.ParseExact(attribute.Value!, "N"))
        .SingleOrDefault();

    public static bool IsEnabled => Id.HasValue;

    public static string? DataRoot => Id is { } id
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DropSpace.Previews", id.ToString("N"))
        : null;

    public static string ScopeName(string name) => Id is { } id ? $"{name}.Preview.{id:N}" : name;
}
