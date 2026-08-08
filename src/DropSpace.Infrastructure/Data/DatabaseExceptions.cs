namespace DropSpace.Infrastructure.Data;

public sealed class UnsupportedSchemaVersionException(int foundVersion, int supportedVersion)
    : InvalidOperationException($"Database schema {foundVersion} is newer than supported schema {supportedVersion}.")
{
    public int FoundVersion { get; } = foundVersion;

    public int SupportedVersion { get; } = supportedVersion;
}

public sealed class DatabaseMigrationException(string message, Exception innerException)
    : InvalidOperationException(message, innerException);
