using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DropSpace.Core.Agent;
using Microsoft.UI.Dispatching;

namespace DropSpace.App.Services.Agent;

public sealed record AppAgentCommandBinding(AppAgentCommand Descriptor,
    Func<JsonElement, CancellationToken, Task<JsonElement>> Handler);

public interface IAppAgentDomain
{
    IReadOnlyList<AppAgentCommandBinding> Commands { get; }
}

/// <summary>Finite, discoverable application commands. No reflection, container lookup or code execution.</summary>
public sealed class AppAgentDispatcher : IAppAgentDispatcher
{
    private readonly Dictionary<string, AppAgentCommandBinding> _commands;
    private readonly DispatcherQueue _dispatcher;
    public AppAgentDispatcher(IEnumerable<IAppAgentDomain> domains, DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _commands = domains.SelectMany(domain => domain.Commands).ToDictionary(command => command.Descriptor.Name, StringComparer.Ordinal);
        Catalog = _commands.Values.Select(command => command.Descriptor).OrderBy(command => command.Name, StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyList<AppAgentCommand> Catalog { get; }

    public Task<JsonElement> ExecuteAsync(string command, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (!_commands.TryGetValue(command, out var binding)) throw new AppAgentCommandException("unknown_command", "Unknown command.");
        Validate(arguments, binding.Descriptor.Parameters);
        cancellationToken.ThrowIfCancellationRequested();
        if (_dispatcher.HasThreadAccess) return binding.Handler(arguments, cancellationToken);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(async () =>
        {
            try { cancellationToken.ThrowIfCancellationRequested(); completion.TrySetResult(await binding.Handler(arguments, cancellationToken)); }
            catch (OperationCanceledException) { completion.TrySetCanceled(cancellationToken); }
            catch (Exception error) { completion.TrySetException(error); }
        })) completion.TrySetException(new AppAgentCommandException("app_unavailable", "The application is closing."));
        return completion.Task;
    }

    private static void Validate(JsonElement args, JsonElement schema)
    {
        if (args.ValueKind != JsonValueKind.Object) throw new AppAgentCommandException("invalid_arguments", "Arguments must be an object.");
        var properties = schema.GetProperty("properties");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in args.EnumerateObject())
        {
            if (!seen.Add(value.Name)) throw new AppAgentCommandException("invalid_arguments", "Duplicate arguments are not accepted.");
            if (!properties.TryGetProperty(value.Name, out var property)) throw new AppAgentCommandException("invalid_arguments", "An argument is not declared by this command.");
            var type = property.GetProperty("type").GetString();
            var valid = type switch { "string" => value.Value.ValueKind == JsonValueKind.String, "boolean" => value.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "integer" => value.Value.ValueKind == JsonValueKind.Number && value.Value.TryGetInt64(out _), "number" => value.Value.ValueKind == JsonValueKind.Number, "array" => value.Value.ValueKind == JsonValueKind.Array,
                "object" => value.Value.ValueKind == JsonValueKind.Object, _ => false };
            if (!valid) throw new AppAgentCommandException("invalid_arguments", "An argument has the wrong type.");
        }
        foreach (var required in schema.GetProperty("required").EnumerateArray())
            if (!args.TryGetProperty(required.GetString()!, out _)) throw new AppAgentCommandException("invalid_arguments", "A required argument is missing.");
    }
}

public abstract class AppAgentDomain : IAppAgentDomain
{
    protected static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter() }, MaxDepth = 32, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private readonly List<AppAgentCommandBinding> _commands = [];
    public IReadOnlyList<AppAgentCommandBinding> Commands => _commands;

    // Parameter notation: name!:type is required; name:type is optional. Types are JSON-schema types.
    protected void Add(string name, string description, bool mutates, string parameters,
        Func<JsonElement, CancellationToken, Task<JsonElement>> handler)
    {
        var properties = new JsonObject(); var required = new JsonArray();
        foreach (var entry in parameters.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split(':'); var key = parts[0].TrimEnd('!');
            properties[key] = new JsonObject { ["type"] = parts[1] };
            if (parts[0].EndsWith('!')) required.Add(key);
        }
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required, ["additionalProperties"] = false };
        _commands.Add(new(new(name, description, JsonSerializer.SerializeToElement(schema), mutates), handler));
    }
    protected static JsonElement Result(object? value) => JsonSerializer.SerializeToElement(value, JsonOptions);
    protected static Task<JsonElement> Value(object? value) => Task.FromResult(Result(value));
    protected static JsonElement Done() => Result(new { status = "completed" });
    protected static string String(JsonElement args, string name, string? fallback = null, int max = 32768)
    {
        var value = args.TryGetProperty(name, out var property) ? property.GetString() : fallback;
        if (value is null || value.Length > max) throw Invalid("A string argument is missing or too long.");
        return value;
    }
    protected static string? OptionalString(JsonElement args, string name, int max = 32768) => args.TryGetProperty(name, out _) ? String(args, name, max: max) : null;
    protected static bool Boolean(JsonElement args, string name, bool fallback = false) => args.TryGetProperty(name, out var value) ? value.GetBoolean() : fallback;
    protected static int Integer(JsonElement args, string name, int fallback, int min, int max)
    {
        var value = args.TryGetProperty(name, out var property) ? property.GetInt32() : fallback;
        if (value < min || value > max) throw Invalid("An integer is outside its allowed range.");
        return value;
    }
    protected static Guid Id(JsonElement args, string name = "id") => Guid.TryParse(String(args, name, max: 40), out var value) ? value : throw Invalid("Invalid item identifier.");
    protected static T EnumValue<T>(JsonElement args, string name, T fallback = default) where T : struct, Enum =>
        !args.TryGetProperty(name, out _) ? fallback : Enum.TryParse<T>(String(args, name, max: 80), true, out var value) && Enum.IsDefined(value) ? value : throw Invalid("Unknown enum value.");
    protected static string[] Strings(JsonElement args, string name, int maxCount = 100, int maxLength = 32768)
    {
        if (!args.TryGetProperty(name, out var values) || values.ValueKind != JsonValueKind.Array || values.GetArrayLength() is 0 || values.GetArrayLength() > maxCount) throw Invalid("Invalid array length.");
        return values.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String && value.GetString() is { } text && text.Length <= maxLength ? text : throw Invalid("Invalid array value.")).ToArray();
    }
    protected static AppAgentCommandException Invalid(string message) => new("invalid_arguments", message);
    protected static async Task<JsonElement> PendingAsync(AppAgentDesktopControl desktop, string page, string operation, object? details = null)
    {
        await desktop.OpenSettingsAsync(page);
        return Result(new { status = "pending_confirmation", operation, page, details, nextStep = "Complete this operation using its existing controls in the opened application page." });
    }
}
