using System.Globalization;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace Panel.Viewer.Layout;

/// <summary>Builds a <see cref="LayoutModel"/> from an already-parsed YAML representation-model root.</summary>
internal static class LayoutModelParser
{
    private static readonly Regex DurationPattern = new(@"^\s*(\d+(?:\.\d+)?)\s*(ms|s|m|h)\s*$", RegexOptions.Compiled);

    public static LayoutModel Parse(YamlMappingNode root)
    {
        var window = TryGetScalar(root, "window", out var windowNode)
            ? ParseDuration(windowNode)
            : TimeSpan.FromSeconds(10);

        var styles = ParseStyles(root);
        var templates = ParseTemplates(root);
        var groups = ParseGroups(root);

        return new LayoutModel(window, styles, templates, groups);
    }

    private static TimeSpan ParseDuration(YamlScalarNode node)
    {
        var match = DurationPattern.Match(node.Value ?? string.Empty);
        if (!match.Success)
            throw Error(node, $"Invalid duration '{node.Value}' (expected e.g. '10s', '500ms').");

        var amount = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        return match.Groups[2].Value switch
        {
            "ms" => TimeSpan.FromMilliseconds(amount),
            "s" => TimeSpan.FromSeconds(amount),
            "m" => TimeSpan.FromMinutes(amount),
            "h" => TimeSpan.FromHours(amount),
            _ => throw Error(node, $"Invalid duration unit in '{node.Value}'."),
        };
    }

    private static Dictionary<string, LaneStyle> ParseStyles(YamlMappingNode root)
    {
        var result = new Dictionary<string, LaneStyle>();
        if (!TryGetMapping(root, "styles", out var stylesMap))
            return result;

        foreach (var (keyNode, valueNode) in stylesMap.Children)
        {
            var name = ((YamlScalarNode)keyNode).Value ?? throw Error(keyNode, "Style name must be a scalar.");
            var map = AsMapping(valueNode, $"Style '{name}'");
            result[name] = ParseStyle(map);
        }

        return result;
    }

    private static LaneStyle ParseStyle(YamlMappingNode map)
    {
        string? fill = TryGetScalar(map, "fill", out var fillNode) ? fillNode.Value : null;
        string? border = TryGetScalar(map, "border", out var borderNode) ? borderNode.Value : null;
        bool? hatch = TryGetScalar(map, "hatch", out var hatchNode) ? ParseBool(hatchNode) : null;
        return new LaneStyle(fill, border, hatch);
    }

    private static bool ParseBool(YamlScalarNode node) =>
        node.Value is "true" or "True" or "TRUE"
            ? true
            : node.Value is "false" or "False" or "FALSE"
                ? false
                : throw Error(node, $"Invalid boolean '{node.Value}'.");

    private static Dictionary<string, IReadOnlyList<LaneSpec>> ParseTemplates(YamlMappingNode root)
    {
        var result = new Dictionary<string, IReadOnlyList<LaneSpec>>();
        if (!TryGetMapping(root, "templates", out var templatesMap))
            return result;

        foreach (var (keyNode, valueNode) in templatesMap.Children)
        {
            var name = ((YamlScalarNode)keyNode).Value ?? throw Error(keyNode, "Template name must be a scalar.");
            var seq = AsSequence(valueNode, $"Template '{name}'");
            result[name] = seq.Children.Select(n => ParseLaneSpec(AsMapping(n, $"Template '{name}' entry"))).ToList();
        }

        return result;
    }

    private static List<LayoutGroup> ParseGroups(YamlMappingNode root)
    {
        var result = new List<LayoutGroup>();
        if (!TryGetSequence(root, "groups", out var groupsSeq))
            return result;

        foreach (var item in groupsSeq.Children)
        {
            var map = AsMapping(item, "Group entry");
            var name = TryGetScalar(map, "name", out var nameNode)
                ? nameNode.Value!
                : throw Error(map, "Group entry is missing 'name'.");

            if (TryGetSequence(map, "lanes", out var lanesSeq))
            {
                var lanes = lanesSeq.Children.Select(n => ParseLaneSpec(AsMapping(n, $"Group '{name}' lane"))).ToList();
                result.Add(new LayoutGroup(name, Lanes: lanes));
            }
            else if (TryGetScalar(map, "use", out var useNode))
            {
                var p = TryGetScalar(map, "p", out var pNode) ? pNode.Value : null;
                result.Add(new LayoutGroup(name, Use: useNode.Value, P: p));
            }
            else
            {
                throw Error(map, $"Group '{name}' has neither 'lanes' nor 'use'.");
            }
        }

        return result;
    }

    private static LaneSpec ParseLaneSpec(YamlMappingNode map)
    {
        var topic = TryGetScalar(map, "topic", out var topicNode)
            ? topicNode.Value!
            : throw Error(map, "Lane entry is missing 'topic'.");

        var asValue = TryGetScalar(map, "as", out var asNode) ? asNode.Value : null;
        var unit = TryGetScalar(map, "unit", out var unitNode) ? unitNode.Value : null;
        var space = TryGetScalar(map, "space", out var spaceNode) ? spaceNode.Value : null;

        IReadOnlyList<double>? range = null;
        if (TryGetSequence(map, "range", out var rangeSeq))
        {
            range = rangeSeq.Children
                .Select(n => double.Parse(((YamlScalarNode)n).Value!, CultureInfo.InvariantCulture))
                .ToList();
        }

        return new LaneSpec(topic, asValue, unit, range, space);
    }

    // --- node helpers -------------------------------------------------

    private static bool TryGetMapping(YamlMappingNode map, string key, out YamlMappingNode result)
    {
        if (TryGetChild(map, key, out var node) && node is YamlMappingNode m)
        {
            result = m;
            return true;
        }
        result = null!;
        return false;
    }

    private static bool TryGetSequence(YamlMappingNode map, string key, out YamlSequenceNode result)
    {
        if (TryGetChild(map, key, out var node) && node is YamlSequenceNode s)
        {
            result = s;
            return true;
        }
        result = null!;
        return false;
    }

    private static bool TryGetScalar(YamlMappingNode map, string key, out YamlScalarNode result)
    {
        if (TryGetChild(map, key, out var node) && node is YamlScalarNode s)
        {
            result = s;
            return true;
        }
        result = null!;
        return false;
    }

    private static bool TryGetChild(YamlMappingNode map, string key, out YamlNode result)
    {
        foreach (var (k, v) in map.Children)
        {
            if (k is YamlScalarNode sk && sk.Value == key)
            {
                result = v;
                return true;
            }
        }
        result = null!;
        return false;
    }

    private static YamlMappingNode AsMapping(YamlNode node, string context) =>
        node as YamlMappingNode ?? throw Error(node, $"{context} must be a mapping.");

    private static YamlSequenceNode AsSequence(YamlNode node, string context) =>
        node as YamlSequenceNode ?? throw Error(node, $"{context} must be a sequence.");

    private static LayoutParseException Error(YamlNode node, string message) =>
        new((int)node.Start.Line, (int)node.Start.Column, message);
}
