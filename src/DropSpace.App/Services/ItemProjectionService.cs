using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;

namespace DropSpace.App.Services;

public sealed record ItemProjectionRequest(string Section, string SearchText);

public sealed class ItemProjectionService(IItemRepository repository)
{
    public const int DefaultPageSize = 200;

    public Task<ItemQueryPage> LoadPageAsync(
        ItemProjectionRequest request,
        ItemQueryCursor? cursor = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = BuildQuery(request);
        // Microsoft.Data.Sqlite executes its async APIs synchronously. Move the whole
        // query invocation off the UI caller; awaiting an already-created task would
        // leave connection setup, SQL execution and row materialization on that caller.
        return Task.Run(() => repository.QueryPageAsync(query, cursor, cancellationToken), cancellationToken);
    }

    internal static ItemQuery BuildQuery(ItemProjectionRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.SearchText))
        {
            return new ItemQuery(Search: request.SearchText, Limit: DefaultPageSize);
        }

        return request.Section switch
        {
            "Clipboard" => new ItemQuery(Source: ItemSource.Clipboard, Limit: DefaultPageSize),
            "Pinned" => new ItemQuery(PinnedOnly: true, Limit: DefaultPageSize),
            _ => new ItemQuery(Source: ItemSource.Space, Limit: DefaultPageSize),
        };
    }
}
