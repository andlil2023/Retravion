using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace Retravion.Web.Features.Mcp;

// Discovery is opt-in so existing callers retain the complete array contract.
internal static class CollectionDiscovery
{
    internal const string Description = " Optional search matches ID/title substrings (ordinal, case-insensitive; at most 200 characters); status is an exact case-insensitive status name. Supplying any discovery parameter returns {items, offset, limit, total, remaining, nextOffset, complete}. offset defaults to 0 (nonnegative); limit defaults to 50 (1-200). Ordered by ordinal ID then immutable identity. Omit all discovery parameters for the legacy complete array. Counts describe authorized filtered results only. Pages are independent reads; concurrent changes may move boundaries.";

    internal static CallToolResult Result<T>(IEnumerable<T> source, Func<T, string> id, Func<T, string> title,
        Func<T, string> statusOf, Func<T, Guid> identity, Func<T, object> values, string[] statuses,
        string? search, string? status, int? offset, int? limit)
    {
        if (search?.Length > 200 || offset < 0 || limit is < 1 or > 200 ||
            status is not null && !statuses.Contains(status, StringComparer.OrdinalIgnoreCase))
            return new() { IsError = true, Content = [new TextContentBlock { Text =
                $"Use search of at most 200 characters, a nonnegative offset, limit 1-200, and status: {string.Join(", ", statuses)}." }] };

        var ordered = source.OrderBy(id, StringComparer.Ordinal).ThenBy(identity);
        object result;
        if (search is null && status is null && offset is null && limit is null)
            result = ordered.Select(values).ToArray();
        else
        {
            var matches = ordered.Where(row =>
                (search is null || id(row).Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    title(row).Contains(search, StringComparison.OrdinalIgnoreCase)) &&
                (status is null || statusOf(row).Equals(status, StringComparison.OrdinalIgnoreCase))).ToArray();
            var start = offset ?? 0;
            var size = limit ?? 50;
            var items = matches.Skip(start).Take(size).Select(values).ToArray();
            var remaining = Math.Max(0L, (long)matches.Length - start - items.Length);
            result = new
            {
                items, offset = start, limit = size, total = matches.Length, remaining,
                nextOffset = remaining > 0 ? (int?) (start + items.Length) : null,
                complete = start == 0 && remaining == 0
            };
        }
        return new() { Content = [new TextContentBlock { Text = JsonSerializer.Serialize(result, Json) }] };
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}
