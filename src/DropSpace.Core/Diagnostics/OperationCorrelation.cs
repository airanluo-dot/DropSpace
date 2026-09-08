namespace DropSpace.Core.Diagnostics;

public static class OperationCorrelation
{
    public static string New() => Guid.NewGuid().ToString("N");
}
