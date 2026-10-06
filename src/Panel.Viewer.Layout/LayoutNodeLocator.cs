using YamlDotNet.RepresentationModel;

namespace Panel.Viewer.Layout;

/// <summary>
/// Finds the YAML mapping node for a given lane by its literal <c>topic:</c> value, searching
/// both <c>templates</c> lane lists and groups' direct <c>lanes</c> lists. Used by
/// <see cref="LayoutDocument"/> to locate the exact node to patch when saving a lane change
/// (plan.md section 11, open point 1: "locate that specific scalar/mapping node by its
/// already-known position in the document").
/// </summary>
internal static class LayoutNodeLocator
{
    /// <param name="topic">
    /// The topic exactly as written in the YAML, e.g. a concrete name like
    /// <c>"display.line1.text"</c> for a group's direct lane, or a template pattern like
    /// <c>"{p}.raw"</c> to edit a template entry shared by every group that uses it.
    /// </param>
    public static YamlMappingNode? FindLaneMapping(YamlMappingNode root, string topic)
    {
        foreach (var (key, value) in root.Children)
        {
            if (key is not YamlScalarNode { Value: "templates" } || value is not YamlMappingNode templatesMap)
                continue;

            foreach (var (_, templateValue) in templatesMap.Children)
            {
                if (templateValue is YamlSequenceNode seq && FindInSequence(seq, topic) is { } found)
                    return found;
            }
        }

        foreach (var (key, value) in root.Children)
        {
            if (key is not YamlScalarNode { Value: "groups" } || value is not YamlSequenceNode groupsSeq)
                continue;

            foreach (var groupNode in groupsSeq.Children)
            {
                if (groupNode is not YamlMappingNode groupMap)
                    continue;

                foreach (var (laneKey, laneValue) in groupMap.Children)
                {
                    if (laneKey is YamlScalarNode { Value: "lanes" } && laneValue is YamlSequenceNode lanesSeq
                        && FindInSequence(lanesSeq, topic) is { } found)
                        return found;
                }
            }
        }

        return null;
    }

    /// <summary>Finds the mapping node for a named entry of the <c>styles:</c> section.</summary>
    public static YamlMappingNode? FindStyleMapping(YamlMappingNode root, string styleName)
    {
        foreach (var (key, value) in root.Children)
        {
            if (key is not YamlScalarNode { Value: "styles" } || value is not YamlMappingNode stylesMap)
                continue;

            foreach (var (styleKey, styleValue) in stylesMap.Children)
            {
                if (styleKey is YamlScalarNode styleKeyScalar && styleKeyScalar.Value == styleName
                    && styleValue is YamlMappingNode styleMap)
                    return styleMap;
            }
        }
        return null;
    }

    private static YamlMappingNode? FindInSequence(YamlSequenceNode seq, string topic)
    {
        foreach (var item in seq.Children)
        {
            if (item is YamlMappingNode laneMap && TryGetTopic(laneMap, out var itemTopic) && itemTopic == topic)
                return laneMap;
        }
        return null;
    }

    private static bool TryGetTopic(YamlMappingNode map, out string? topic)
    {
        foreach (var (key, value) in map.Children)
        {
            if (key is YamlScalarNode { Value: "topic" } && value is YamlScalarNode topicScalar)
            {
                topic = topicScalar.Value;
                return true;
            }
        }
        topic = null;
        return false;
    }
}
