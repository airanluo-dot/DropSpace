using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DropSpace.Core.Dlc;

if (args.Contains("--child"))
{
    using var held = new FileStream("child-lock.txt", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    await File.WriteAllTextAsync("child-ready.txt", Environment.ProcessId.ToString());
    await Task.Delay(Timeout.InfiniteTimeSpan);
    return 0;
}
Console.InputEncoding = new UTF8Encoding(false);
Console.OutputEncoding = new UTF8Encoding(false);
var index = Array.IndexOf(args, "--module-session");
var session = args[index + 1];
var manifest = JsonSerializer.Deserialize<ModuleManifest>(
    await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "manifest.json")),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
if (File.Exists("spawn-child.txt"))
{
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
    start.ArgumentList.Add("--child");
    using var child = Process.Start(start)!;
    await File.WriteAllTextAsync("child-pid.txt", child.Id.ToString());
}
while (await Console.In.ReadLineAsync() is { } line)
{
    var request = JsonSerializer.Deserialize<ModuleEnvelope>(line)!;
    if (request.Method == "hello" && File.Exists("hold-hello.txt"))
    {
        await File.WriteAllTextAsync("hello-entered.txt", "entered");
        while (!File.Exists("release-hello.txt")) await Task.Delay(10);
    }
    if (request.Method == "action" && request.Payload.GetProperty("action").GetString() == "crash") return 9;
    var result = request.Method == "hello"
        ? JsonSerializer.SerializeToElement(new { moduleId = manifest.Id, moduleVersion = manifest.Version, protocol = 1, dataVersion = 1 })
        : JsonSerializer.SerializeToElement(new { stopped = true });
    await Console.Out.WriteLineAsync(JsonSerializer.Serialize(
        new ModuleEnvelope(1, session, request.Id, "result", JsonSerializer.SerializeToElement(new ModuleReply(result)))));
    await Console.Out.FlushAsync();
    if (request.Method == "stop") return 0;
}
return 0;
