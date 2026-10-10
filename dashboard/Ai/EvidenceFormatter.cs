using System.Text;
using System.Text.Json.Nodes;

namespace Pitstop.ResilienceDashboard;

/// <summary>
/// Turns a model's evidence citation into something a human can read.
/// </summary>
/// <remarks>
/// The model is asked to cite the evidence its claims rest on. Given JSON, it
/// tends to answer with the path it read - `DeterministicAnalysis.Dimensions[1].Earned` -
/// which is true, traceable and useless on screen.
///
/// Two fixes, because either alone is insufficient. The prompt now asks for
/// readable statements, and this resolver repairs path-shaped citations that
/// still arrive by walking the evidence and substituting the real value.
///
/// It never invents. A citation that cannot be resolved is returned unchanged
/// rather than replaced with a guess, because a wrong-but-readable citation is
/// worse than an ugly one.
/// </remarks>
public static class EvidenceFormatter
{
    /// <summary>Labels an array element by its own name instead of its index.</summary>
    private static readonly string[] NameProperties = ["Name", "Title", "Module", "Provider", "Service"];

    public static IReadOnlyList<string> ResolveAll(IEnumerable<string>? citations, JsonNode? evidence)
    {
        if (citations is null) return [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolved = new List<string>();
        foreach (var citation in citations)
        {
            if (string.IsNullOrWhiteSpace(citation)) continue;
            var text = Resolve(citation, evidence);
            // The model often cites the same field more than once across claims.
            if (seen.Add(text)) resolved.Add(text);
        }
        return resolved;
    }

    public static string Resolve(string citation, JsonNode? evidence)
    {
        var trimmed = citation.Trim();

        // "Field = value" or prose: already readable, leave it alone.
        if (evidence is null || !LooksLikePath(trimmed)) return trimmed;

        var segments = Parse(trimmed);
        if (segments.Count == 0) return trimmed;

        JsonNode? node = evidence;
        var labels = new List<string>();

        foreach (var segment in segments)
        {
            if (node is null) return trimmed;

            if (segment.Index is { } index)
            {
                if (node is not JsonArray array || index < 0 || index >= array.Count) return trimmed;
                node = array[index];
                var label = LabelFor(node, index);
                // "Dimensions → Graceful degradation" says nothing "Graceful
                // degradation" does not, so a named element replaces the
                // container label. An unnamed one keeps it for context.
                if (!label.StartsWith('#') && labels.Count > 0) labels[^1] = label;
                else labels.Add(label);
                continue;
            }

            var property = MatchProperty(node, segment.Name!);
            if (property is null) return trimmed;
            node = node[property];
            labels.Add(Humanise(property));
        }

        var value = Render(node);
        if (value is null) return trimmed;

        // The last label is the field; everything before it is context.
        var leaf = labels[^1];
        var context = labels.Count > 1 ? string.Join(" → ", labels[..^1]) + " → " : string.Empty;
        return $"{context}{leaf}: {value}";
    }

    // A citation may be a dotted path, an indexed path, or a single field name.
    // Anything with whitespace is prose. A bare identifier that does not resolve
    // is returned unchanged by the caller, so attempting it costs nothing.
    private static bool LooksLikePath(string text) =>
        text.Length > 0 && !text.Any(char.IsWhiteSpace);

    private readonly record struct Segment(string? Name, int? Index);

    private static List<Segment> Parse(string path)
    {
        var segments = new List<Segment>();
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = part;
            var bracket = part.IndexOf('[');
            if (bracket >= 0)
            {
                name = part[..bracket];
                if (!string.IsNullOrEmpty(name)) segments.Add(new Segment(name, null));

                // A path may chain indices, as in Samples[0][1].
                var rest = part[bracket..];
                foreach (var chunk in rest.Split(']', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (int.TryParse(chunk.TrimStart('['), out var index)) segments.Add(new Segment(null, index));
                    else return [];
                }
                continue;
            }
            segments.Add(new Segment(name, null));
        }
        return segments;
    }

    /// <summary>Matches a property case-insensitively; models rarely match our casing.</summary>
    private static string? MatchProperty(JsonNode node, string name)
    {
        if (node is not JsonObject obj) return null;
        foreach (var pair in obj)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)) return pair.Key;
        }
        return null;
    }

    private static string LabelFor(JsonNode? node, int index)
    {
        if (node is JsonObject obj)
        {
            foreach (var candidate in NameProperties)
            {
                var key = MatchProperty(obj, candidate);
                if (key is not null && obj[key] is JsonValue value)
                {
                    var text = value.ToString();
                    if (!string.IsNullOrWhiteSpace(text)) return text;
                }
            }
        }
        return $"#{index + 1}";
    }

    private static string? Render(JsonNode? node) => node switch
    {
        null => null,
        JsonValue value => value.ToString(),
        // A cited object or array is summarised rather than dumped: the point is
        // to show the operator what was relied on, not to paste the evidence back.
        JsonArray array => array.Count == 0 ? "none" : string.Join(", ", array.Take(4).Select(Render)) + (array.Count > 4 ? $", … (+{array.Count - 4})" : string.Empty),
        JsonObject obj => obj.Count == 0 ? "none" : string.Join(", ", obj.Take(3).Select(p => $"{Humanise(p.Key)}: {Render(p.Value)}")) + (obj.Count > 3 ? ", …" : string.Empty),
        _ => node.ToString(),
    };

    /// <summary>`RecoverySeconds` becomes `Recovery seconds`.</summary>
    public static string Humanise(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier)) return identifier;

        var builder = new StringBuilder(identifier.Length + 6);
        for (var i = 0; i < identifier.Length; i++)
        {
            var c = identifier[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(identifier[i - 1])) builder.Append(' ');
            builder.Append(i == 0 ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
        }
        return builder.ToString();
    }
}
