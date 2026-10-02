using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace DropSpace.ResidentWorkerFixture;

// Synthetic worker for managed ownership/protocol tests. It never loads a model or a GPU.
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        var scenarioPath = Path.Combine(AppContext.BaseDirectory, "process-fixture.json");
        if (File.Exists(scenarioPath)) return RunOneShot(args, scenarioPath);
        var root = Path.GetDirectoryName(Path.GetFullPath(Argument(args, "--model")))!;
        var mode = Argument(args, "--mode");
        var modelProfile = Array.IndexOf(args, "--model-profile") >= 0 ? Argument(args, "--model-profile") : "hy-mt2-1.8b-q8";
        var pid = Environment.ProcessId;
        Record(root, "starts", new { pid, mode, modelProfile });
        if (mode == "vulkan" && File.Exists(Path.Combine(root, "fail-vulkan-startup")))
        {
            // EOF while the child stays alive verifies that fallback first reaps the child.
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            if (!CloseHandle(GetStdHandle(-11))) return 28;
            Thread.Sleep(60_000);
            return 27;
        }
        if (File.Exists(Path.Combine(root, "omit-model-profile")))
            WriteFrame(new { protocol = 1, ready = true, backend = mode });
        else WriteFrame(new { protocol = 1, ready = true, backend = mode,
            modelProfile = File.Exists(Path.Combine(root, "wrong-model-profile")) ? "wrong-model" : modelProfile });
        while (Console.ReadLine() is { } line)
        {
            using var request = JsonDocument.Parse(line);
            var packet = request.RootElement;
            var protocol = packet.GetProperty("protocol").GetInt32();
            var id = packet.GetProperty("id").GetString();
            var prompt = packet.GetProperty("prompt").GetString()!;
            Record(root, "requests", new { protocol, id, prompt, pid });
            if (prompt == "partial-and-block")
            {
                Console.Write("{\"protocol\":1,\"text\":\"private incomplete output");
                Console.Out.Flush();
                File.WriteAllText(Path.Combine(root, "blocked"), string.Empty);
                Thread.Sleep(60_000);
            }
            else
            {
                var responseId = prompt == "mismatched-id" ? "wrong-host-request-id" : id;
                var responseProtocol = prompt == "wrong-protocol" ? 2 : 1;
                var complete = prompt != "incomplete-response";
                var text = prompt == "oversized-output" ? new string('夜', 6000) : prompt + " [end of text]";
                WriteFrame(new { protocol = responseProtocol, id = responseId, complete, text });
            }
        }
        return 0;
    }

    // An actual Windows child process exercises the older completion/tokenizer and
    // CT2 host boundaries. Scenarios are explicit test data, never shell commands.
    private static int RunOneShot(string[] args, string scenarioPath)
    {
        using var scenario = JsonDocument.Parse(File.ReadAllBytes(scenarioPath));
        var data = scenario.RootElement;
        string? Text(string name) => data.TryGetProperty(name, out var value) ? value.GetString() : null;
        int Number(string name) => data.TryGetProperty(name, out var value) ? value.GetInt32() : 0;
        var kind = Text("kind");
        if (kind is not ("completion" or "ct2" or "lifetime")) return 64;
        if (Text("pidPath") is { } pidPath)
        {
            // File existence is the parent's readiness signal. Publish only
            // after closing the writer so Windows readers cannot race its handle.
            var pendingPidPath = pidPath + ".pending";
            File.WriteAllText(pendingPidPath, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            // The same verified helper package can be launched repeatedly. Replace
            // its previous PID signal without reopening the published file to write.
            File.Move(pendingPidPath, pidPath, overwrite: true);
        }
        if (Text("argumentsPath") is { } argumentsPath) File.WriteAllLines(argumentsPath, args, new UTF8Encoding(false));
        if (Text("capturedPromptPath") is { } captured)
        {
            // Native Windows exclusive sharing proves that the parent closed its
            // prompt writer before handing the filename to the child.
            using var prompt = new FileStream(Argument(args, "-f"), FileMode.Open, FileAccess.Read, FileShare.None);
            using var output = File.Create(captured);
            prompt.CopyTo(output);
        }
        if (Text("environmentPath") is { } environmentPath)
            File.WriteAllLines(environmentPath, Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .Select(entry => $"{entry.Key}={entry.Value}"), new UTF8Encoding(false));
        if (kind == "ct2") _ = Console.In.ReadToEnd();
        if (Text("markerPath") is { } marker) File.WriteAllText(marker, "started");
        if (Number("stdoutBytes") is var stdoutBytes && stdoutBytes > 0) Console.Write(new string('0', stdoutBytes));
        if (Number("stderrBytes") is var stderrBytes && stderrBytes > 0) Console.Error.Write(new string('0', stderrBytes));
        Console.Write(Text("output") ?? "");
        Console.Out.Flush(); Console.Error.Flush();
        if (Text("readyPath") is { } ready) File.WriteAllText(ready, "ready");
        if (Text("releasePath") is { } release) while (!File.Exists(release)) Thread.Sleep(20);
        if (Number("delayMilliseconds") is var delay && delay > 0) Thread.Sleep(delay);
        if (Text("lateOutput") is { } late) { Console.Write(late); Console.Out.Flush(); }
        return Number("exitCode");
    }

    private static string Argument(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : throw new ArgumentException("Missing fixture argument.");
    }

    private static void Record(string root, string name, object packet) =>
        File.AppendAllText(Path.Combine(root, name), JsonSerializer.Serialize(packet) + "\n", new UTF8Encoding(false));

    private static void WriteFrame(object packet)
    {
        Console.WriteLine(JsonSerializer.Serialize(packet));
        Console.Out.Flush();
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll")]
    private static extern nint GetStdHandle(int standardHandle);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
