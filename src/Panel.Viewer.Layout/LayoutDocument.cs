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
