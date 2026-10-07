using YamlDotNet.RepresentationModel;

namespace TopicMonitor.Viewer.Layout;

/// <summary>
/// Applies a minimal textual edit to a YAML document's source text: changes one scalar field of
/// one flow mapping (e.g. <c>{ topic: "led.1.raw", as: lines }</c>) in place, leaving every other
/// byte — including comments elsewhere in the file — untouched. This is the mechanism behind
/// , open point 1: saves patch the located node's text instead of
/// re-serializing the whole document, so hand-written comments and formatting survive.
/// </summary>
internal static class FlowMappingEditor
{
    /// <summary>
    /// Returns a new document text with <paramref name="key"/> set to <paramref name="value"/>
    /// inside <paramref name="mapping"/>. If the key already exists, only its value's text is
    /// replaced. If not, a new <c>key: value</c> entry is inserted just before the mapping's
    /// closing <c>}</c> (lane and style entries in <c>layout.yaml</c> are always flow mappings;
    /// see the examples ).
    /// </summary>
    public static string SetScalarField(string text, YamlMappingNode mapping, string key, string value)
    {
        foreach (var (k, v) in mapping.Children)
        {
            if (k is YamlScalarNode { Value: { } kv } && kv == key && v is YamlScalarNode scalarValue)
            {
                var start = (int)scalarValue.Start.Index;
                var end = (int)scalarValue.End.Index;
                return text[..start] + FormatScalar(value) + text[end..];
            }
        }

        var closeBraceIndex = FindClosingBrace(text, mapping);
        var beforeBrace = text[..closeBraceIndex].TrimEnd(' ', '\t');
        var insertion = $", {key}: {FormatScalar(value)} }}";
        return beforeBrace + insertion + text[(closeBraceIndex + 1)..];
    }

    /// <summary>
    /// Finds the index of the <c>}</c> that closes the flow mapping starting at
    /// <paramref name="mapping"/>'s <see cref="YamlNode.Start"/>. YamlDotNet's representation
    /// model only gives composite nodes (mappings/sequences) a reliable <em>start</em> mark --
    /// their <c>End</c> mark is copied from the opening event, not the real extent of the node --
    /// so the matching close brace is found by scanning forward with simple depth/quote tracking
    /// rather than trusting <c>mapping.End</c>.
    /// </summary>
    private static int FindClosingBrace(string text, YamlMappingNode mapping)
    {
        var idx = (int)mapping.Start.Index;
        if (idx >= text.Length || text[idx] != '{')
            throw new InvalidOperationException("Expected the lane/style entry to be a flow mapping starting with '{'.");

        var depth = 0;
        for (; idx < text.Length; idx++)
        {
            var c = text[idx];
            if (c is '"' or '\'')
            {
                idx = SkipQuoted(text, idx, c);
                continue;
            }
            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                    return idx;
            }
        }

        throw new InvalidOperationException("Could not find the closing '}' for this flow mapping.");
    }

    /// <summary>Returns the index of the closing quote matching <paramref name="quoteChar"/> at <paramref name="startIndex"/>.</summary>
    private static int SkipQuoted(string text, int startIndex, char quoteChar)
    {
        var idx = startIndex + 1;
        while (idx < text.Length)
        {
            if (quoteChar == '"' && text[idx] == '\\')
            {
                idx += 2; // double-quoted scalars use backslash escapes
                continue;
            }
            if (text[idx] == quoteChar)
            {
                if (quoteChar == '\'' && idx + 1 < text.Length && text[idx + 1] == '\'')
                {
                    idx += 2; // single-quoted scalars escape a quote by doubling it
                    continue;
                }
                return idx;
            }
            idx++;
        }
        return idx;
    }

    private static bool NeedsQuoting(string value)
    {
        if (value.Length == 0)
            return true;
        if (value != value.Trim())
            return true;
        foreach (var c in value)
        {
            if (":#{}[],&*!|>'\"%@`".IndexOf(c) >= 0)
                return true;
        }
        return false;
    }

    private static string FormatScalar(string value) =>
        NeedsQuoting(value) ? "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"" : value;
}
