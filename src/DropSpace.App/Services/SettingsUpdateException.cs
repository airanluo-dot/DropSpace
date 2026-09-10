namespace DropSpace.App.Services;

public sealed class SettingsUpdateException(string stageResourceKey, string operationId, Exception innerException)
    : Exception("The settings transaction failed.", innerException)
{
    public string StageResourceKey { get; } = stageResourceKey;
    public string OperationId { get; } = operationId;
}
