using System.Runtime.InteropServices;

// Read-only WDDM counters, scoped to the production runner's own PID. No GPU probing workload.
internal sealed class GpuCounters : IDisposable
{
    internal sealed record Observation(string name, double value, string unit);
    internal sealed record Evidence(double PeakEnginePercent, double PeakDedicatedBytes);
    public Dictionary<int, Evidence> ByProcess { get; } = [];
    private nint _query, _engines, _memory;
    public string? Error { get; private set; }
    internal GpuCounters()
    {
        var status = PdhOpenQueryW(null, 0, out _query);
        if (status == 0) status = PdhAddEnglishCounterW(_query, @"\GPU Engine(*)\Utilization Percentage", 0, out _engines);
        if (status == 0) status = PdhAddEnglishCounterW(_query, @"\GPU Process Memory(*)\Dedicated Usage", 0, out _memory);
        if (status != 0) Error = status.ToString("X8");
        if (_query != 0) PdhCollectQueryData(_query);
    }
    internal object[] Sample(int? pid)
    {
        if (Error is not null) return [];
        var status = PdhCollectQueryData(_query);
        if (status != 0) return [];
        if (pid is null) return [];
        var values = Read(_engines, pid.Value, "percent").Concat(Read(_memory, pid.Value, "bytes")).ToArray();
        var previous = ByProcess.GetValueOrDefault(pid.Value) ?? new(0, 0);
        ByProcess[pid.Value] = new(Math.Max(previous.PeakEnginePercent,
            values.Where(item => item.unit == "percent" && !item.name.Contains("engtype_Copy", StringComparison.Ordinal))
                .Select(item => item.value).DefaultIfEmpty().Max()),
            Math.Max(previous.PeakDedicatedBytes, values.Where(item => item.unit == "bytes").Select(item => item.value).DefaultIfEmpty().Max()));
        return values;
    }
    private static IEnumerable<Observation> Read(nint counter, int pid, string unit)
    {
        uint size = 0, count = 0;
        PdhGetFormattedCounterArrayW(counter, 0x200, ref size, ref count, 0);
        if (size is 0 or > 16_777_216) yield break;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, 0x200, ref size, ref count, buffer) != 0) yield break;
            for (var i = 0; i < count; i++)
            {
                var item = buffer + i * 24; // x64 PDH_FMT_COUNTERVALUE_ITEM_W
                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item));
                var status = Marshal.ReadInt32(item + 8);
                if (status is not (0 or 1) || name?.StartsWith("pid_" + pid + "_", StringComparison.Ordinal) != true) continue;
                var value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item + 16));
                if (value > 0) yield return new(name, value, unit);
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    public void Dispose() { if (_query != 0) PdhCloseQuery(_query); _query = 0; }
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhOpenQueryW(string? source, nuint user, out nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhAddEnglishCounterW(nint query, string path, nuint user, out nint counter);
    [DllImport("pdh.dll", ExactSpelling = true)] private static extern uint PdhCollectQueryData(nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint bytes, ref uint count, nint buffer);
    [DllImport("pdh.dll", ExactSpelling = true)] private static extern uint PdhCloseQuery(nint query);
}
