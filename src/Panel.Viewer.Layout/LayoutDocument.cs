using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Panel.Viewer.Layout;

/// <summary>
/// A loaded <c>layout.yaml</c>: both the parsed <see cref="Model"/> and the original source
/// text with its <see cref="YamlDotNet.RepresentationModel"/> node positions, so that targeted
/// edits (<see cref="WithLaneRenderer"/>, <see cref="WithStyleField"/>) can patch only the
/// touched value and leave the rest of the file — including comments — byte-for-byte unchanged
/// (plan.md section 11, open point 1).
/// </summary>
public sealed class LayoutDocument
{
    private readonly YamlMappingNode _root;

    public string RawText { get; }
    public LayoutModel Model { get; }

    private LayoutDocument(string rawText, LayoutModel model, YamlMappingNode root)
    {
        RawText = rawText;
        Model = model;
        _root = root;
    }

    /// <summary>
    /// Parses <paramref name="yamlText"/>. Throws <see cref="LayoutParseException"/> with a
    /// 1-based line/column on any syntax or schema error; callers that want to keep the last
    /// valid layout on error should go through <see cref="LayoutLoader"/> instead of calling
    /// this directly.
    /// </summary>
    public static LayoutDocument Parse(string yamlText)
    {
        YamlMappingNode root;
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yamlText));

            if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode mapping)
                throw new LayoutParseException(1, 1, "layout.yaml must contain a single YAML mapping document.");

            root = mapping;
        }
        catch (YamlException ex)
        {
            throw new LayoutParseException((int)ex.Start.Line, (int)ex.Start.Column, ex.Message);
        }

        var model = LayoutModelParser.Parse(root);
        return new LayoutDocument(yamlText, model, root);
    }

    public static LayoutDocument LoadFile(string path) => Parse(File.ReadAllText(path));

    public void SaveTo(string path) => File.WriteAllText(path, RawText);

    /// <summary>
    /// Returns a new <see cref="LayoutDocument"/> where the lane for <paramref name="topic"/>
    /// has its <c>as:</c> renderer set to <paramref name="renderer"/>, via a minimal text patch
    /// rather than a full re-serialize.
    /// </summary>
    /// <param name="topic">
    /// The topic exactly as written in the YAML (a concrete lane topic, or a template pattern
    /// such as <c>"{p}.raw"</c> to retarget every group that uses that template).
    /// </param>
    public LayoutDocument WithLaneRenderer(string topic, RendererKind renderer)
    {
        var laneNode = LayoutNodeLocator.FindLaneMapping(_root, topic)
            ?? throw new InvalidOperationException($"No lane found for topic '{topic}'.");

        var newText = FlowMappingEditor.SetScalarField(RawText, laneNode, "as", renderer.ToYamlString());
        return Parse(newText);
    }

    /// <summary>
    /// Like <see cref="WithLaneRenderer(string, RendererKind)"/>, but targets the lane entry at
    /// <paramref name="laneIndex"/> (see <see cref="LayoutCatalogCheck.FindLaneIndex"/>), so lanes that
    /// share a topic string can be told apart.
    /// </summary>
    public LayoutDocument WithLaneRenderer(int laneIndex, RendererKind renderer)
    {
        var lanes = LayoutNodeLocator.EnumerateLanes(_root);
        if (laneIndex < 0 || laneIndex >= lanes.Count)
            throw new InvalidOperationException($"No lane entry at index {laneIndex}.");

        var newText = FlowMappingEditor.SetScalarField(RawText, lanes[laneIndex], "as", renderer.ToYamlString());
        return Parse(newText);
    }

    /// <summary>
    /// Appends a new group of direct lanes after the last entry of <c>groups:</c> (or creates
    /// <c>groups:</c> at the end of the file), leaving every existing line unchanged.
    /// </summary>
    public LayoutDocument WithAppendedGroup(string groupName, IReadOnlyList<LaneSpec> lanes)
    {
        if (lanes.Count == 0) return this;

        var eol = RawText.Contains("\r\n") ? "\r\n" : "\n";
        var lines = RawText.Split('\n').ToList();

        var groups = LayoutNodeLocator.FindGroups(_root);
        string indent;
        int insertAt;

        if (groups is null)
        {
            if (lines[^1].Length > 0) lines.Add(string.Empty);
            insertAt = lines.Count - 1;
            lines.Insert(insertAt++, "groups:" + (eol == "\r\n" ? "\r" : string.Empty));
            indent = "  ";
        }
        else
        {
            if (groups.Style != YamlDotNet.Core.Events.SequenceStyle.Block || groups.Children.Count == 0)
                throw new InvalidOperationException("Cannot append to 'groups': it must be a non-empty block sequence.");

            var startLine = (int)groups.Children[^1].Start.Line - 1;
            var dash = lines[startLine].IndexOf('-');
            if (dash < 0)
                throw new InvalidOperationException("Cannot append to 'groups': last entry does not start with '- '.");

            indent = new string(' ', dash);
            var lastContent = startLine;
            for (var i = startLine + 1; i < lines.Count; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.Length == 0 || trimmed[0] == '#') continue;
                if (lines[i].Length - lines[i].TrimStart().Length <= dash) break;
                lastContent = i;
            }
            insertAt = lastContent + 1;
        }

        var cr = eol == "\r\n" ? "\r" : string.Empty;
        var added = new List<string>
        {
            $"{indent}- name: {Quote(groupName)}{cr}",
            $"{indent}  lanes:{cr}",
        };
        foreach (var lane in lanes)
        {
            var fields = new List<string> { $"topic: {Quote(lane.Topic)}" };
            if (lane.As is not null) fields.Add($"as: {lane.As}");
            if (lane.Unit is not null) fields.Add($"unit: {Quote(lane.Unit)}");
            if (lane.Space is not null) fields.Add($"space: {lane.Space}");
            added.Add($"{indent}    - {{ {string.Join(", ", fields)} }}{cr}");
        }

        // The final line of a file without trailing newline has no terminator of its own.
        if (insertAt == lines.Count) added[^1] = added[^1].TrimEnd('\r');
        lines.InsertRange(insertAt, added);

        return Parse(string.Join('\n', lines));
    }

    private static string Quote(string value) =>
        System.Text.RegularExpressions.Regex.IsMatch(value, @"^[A-Za-z0-9_.\-]+$")
            ? value
            : "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>
    /// Returns a new <see cref="LayoutDocument"/> where the named style's field (<c>fill</c>,
    /// <c>border</c>, or any other scalar key) is set to <paramref name="value"/>, via the same
    /// minimal text patch mechanism as <see cref="WithLaneRenderer"/>.
    /// </summary>
    public LayoutDocument WithStyleField(string styleName, string key, string value)
    {
        var styleNode = LayoutNodeLocator.FindStyleMapping(_root, styleName)
            ?? throw new InvalidOperationException($"No style named '{styleName}' found.");

        var newText = FlowMappingEditor.SetScalarField(RawText, styleNode, key, value);
        return Parse(newText);
    }
}
