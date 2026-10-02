using System.Text.Json;

namespace CSweet.Agent.Producer.VideoGame;

/// <summary>
/// Reads a JSON object from a model reply. Models often wrap the requested JSON in a markdown fence
/// (```json ... ```) or add a sentence around it even when told to return only JSON, so the reply is
/// unwrapped before parsing. A reply that still is not valid JSON becomes an InvalidOperationException,
/// which callers report as a visible wait reason instead of crashing the whole work item.
/// </summary>
internal static class ModelJson
{
    private static readonly string Fence = new('`', 3);

    internal static T Deserialize<T>(string? reply, JsonSerializerOptions options, string purpose) where T : class
    {
        var text = Unwrap(reply);
        if (text.Length == 0) throw new InvalidOperationException($"{purpose} returned an empty reply.");
        try
        {
            return JsonSerializer.Deserialize<T>(text, options)
                ?? throw new InvalidOperationException($"{purpose} returned no decision.");
        }
        catch (JsonException)
        {
            // Fall back to the outermost object when the model added prose around it.
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start >= 0 && end > start && (start > 0 || end < text.Length - 1))
            {
                try
                {
                    return JsonSerializer.Deserialize<T>(text[start..(end + 1)], options)
                        ?? throw new InvalidOperationException($"{purpose} returned no decision.");
                }
                catch (JsonException) { }
            }
            throw new InvalidOperationException($"{purpose} returned a reply that is not valid JSON.");
        }
    }

    internal static string Unwrap(string? reply)
    {
        var text = (reply ?? "").Trim();
        if (!text.StartsWith(Fence, StringComparison.Ordinal)) return text;
        var firstLine = text.IndexOf('\n');
        var closing = text.LastIndexOf(Fence, StringComparison.Ordinal);
        return firstLine >= 0 && closing > firstLine ? text[(firstLine + 1)..closing].Trim() : text.Trim('`').Trim();
    }
}
