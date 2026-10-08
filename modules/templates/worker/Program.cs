using System.Globalization;
using System.Text;
using System.Text.Json;
using DropSpace.Core.Dlc;
using DropSpace.Module.Worker;

// Standard output belongs exclusively to the wire protocol. Diagnostics never include user data.
Console.InputEncoding = new UTF8Encoding(false, true);
Console.OutputEncoding = new UTF8Encoding(false);
var sessionIndex = Array.IndexOf(args, "--module-session");
var dataIndex = Array.IndexOf(args, "--data-version");
if (sessionIndex < 0 || sessionIndex + 1 >= args.Length || dataIndex < 0 || dataIndex + 1 >= args.Length ||
    !Guid.TryParseExact(args[sessionIndex + 1], "N", out _) ||
    !int.TryParse(args[dataIndex + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var dataVersion) ||
    dataVersion != ModuleIdentity.DataVersion)
    return 2;
var session = args[sessionIndex + 1];
var input = Console.OpenStandardInput();
var output = Console.OpenStandardOutput();
var ready = false;
var showIsland = true;
var islandSupported = false;
var lastId = 0L;
try
{
    // Settings are validated/owned/persisted by the host after the worker acknowledges them.
    // This sample writes no files and has no migration or user-data dependency.
    while (await ReadEnvelopeAsync(input) is { } request)
    {
        if (request.Protocol != ModuleContract.Protocol || request.Session != session ||
            !long.TryParse(request.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= lastId)
            return 4;
        lastId = id;
        var stop = false;
        ModuleReply? reply = null;
        string? error = null;
        try
        {
            switch (request.Method)
            {
                case "hello" when !ready:
                    if (request.Payload.GetProperty("moduleId").GetString() != ModuleIdentity.Id ||
                        request.Payload.GetProperty("moduleVersion").GetString() != ModuleIdentity.Version ||
                        request.Payload.GetProperty("protocol").GetInt32() != ModuleContract.Protocol ||
                        request.Payload.GetProperty("hostInterface").GetInt32() != ModuleContract.HostInterface ||
                        request.Payload.GetProperty("dataVersion").GetInt32() != ModuleIdentity.DataVersion)
                        throw new InvalidDataException("HandshakeMismatch");
                    ready = true;
                    islandSupported = request.Payload.GetProperty("capabilities").EnumerateArray().Any(capability =>
                        capability.GetProperty("Id").GetString() == "island.content" && capability.GetProperty("Version").GetInt32() >= 1);
                    reply = new(JsonSerializer.SerializeToElement(new
                    {
                        moduleId = ModuleIdentity.Id, moduleVersion = ModuleIdentity.Version,
                        protocol = ModuleContract.Protocol, dataVersion = ModuleIdentity.DataVersion,
                    }));
                    break;
                case "action" when ready:
                    if (request.Payload.GetProperty("action").GetString() != "show")
                        throw new InvalidDataException("UnknownAction");
                    reply = new(JsonSerializer.SerializeToElement(new { messageKey = "message" }),
                        showIsland && islandSupported ? new(new("compact"), new("expanded"),
                            [new("show", new("action"))], DateTimeOffset.UtcNow.AddSeconds(30)) : null);
                    break;
                case "settings" when ready:
                    if (request.Payload.EnumerateObject().Any(setting => setting.Name != "show-island"))
                        throw new InvalidDataException("UnknownSetting");
                    var value = request.Payload.GetProperty("show-island").GetString();
                    if (value is not ("true" or "false")) throw new InvalidDataException("InvalidSetting");
                    showIsland = value == "true";
                    reply = new(JsonSerializer.SerializeToElement(new { saved = true }));
                    break;
                case "stop":
                    // No background tasks or subscriptions exist in this minimal sample.
                    reply = new(JsonSerializer.SerializeToElement(new { stopped = true }));
                    stop = true;
                    break;
                default:
                    error = ready ? "UnknownMethod" : "HandshakeRequired";
                    break;
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or
            KeyNotFoundException or InvalidOperationException or FormatException or IOException or UnauthorizedAccessException)
        {
            error = exception is IOException or UnauthorizedAccessException ? "ModuleDataUnavailable" : "InvalidRequest";
        }
        await WriteEnvelopeAsync(output, new(ModuleContract.Protocol, session, request.Id, "result",
            JsonSerializer.SerializeToElement(reply), error));
        if (stop) return 0;
    }
    return 0;
}
catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException or
    KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException)
{
    Console.Error.WriteLine("ModuleStopped");
    return 5;
}

static async Task<ModuleEnvelope?> ReadEnvelopeAsync(Stream input)
{
    var bytes = new byte[ModuleContract.MaximumMessageBytes];
    var one = new byte[1];
    var count = 0;
    while (true)
    {
        var read = await input.ReadAsync(one);
        if (read == 0) return count == 0 ? null : throw new InvalidDataException("IncompleteMessage");
        if (one[0] == (byte)'\n') break;
        if (count == bytes.Length) throw new InvalidDataException("MessageTooLarge");
        bytes[count++] = one[0];
    }
    return JsonSerializer.Deserialize<ModuleEnvelope>(bytes.AsSpan(0, count))
        ?? throw new InvalidDataException("InvalidMessage");
}

static async Task WriteEnvelopeAsync(Stream output, ModuleEnvelope response)
{
    var bytes = JsonSerializer.SerializeToUtf8Bytes(response);
    if (bytes.Length > ModuleContract.MaximumMessageBytes) throw new InvalidDataException("MessageTooLarge");
    await output.WriteAsync(bytes);
    await output.WriteAsync(new byte[] { (byte)'\n' });
    await output.FlushAsync();
}
