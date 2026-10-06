using System.Globalization;
using Panel.Client;
using Panel.Contracts;
using Panel.Viewer.Layout;

namespace Panel.Viewer.Wpf;

/// <summary>A lane bound to its catalog topic (null when the layout names a topic the server does not offer).</summary>
public sealed record LaneModel(ExpandedLane Spec, TopicInfo? Topic, LaneValueType Type, RendererKind Renderer)
{
    public string Key => LaneModelBuilder.KeyOf(Spec);

    public IReadOnlyList<string> Components => Topic?.Components ?? (IReadOnlyList<string>)Array.Empty<string>();

    public string DisplayName => string.IsNullOrEmpty(Spec.Unit) ? Spec.Topic : $"{Spec.Topic} ({Spec.Unit})";
}

public static class LaneModelBuilder
{
    public static string KeyOf(ExpandedLane lane) => $"{lane.GroupName}/{lane.Topic}/{lane.As}";

    public static LaneValueType ToLaneType(Panel.Contracts.ValueType type) => type switch
    {
        Panel.Contracts.ValueType.Float => LaneValueType.Float,
        Panel.Contracts.ValueType.Int => LaneValueType.Int,
        Panel.Contracts.ValueType.Bool => LaneValueType.Bool,
        Panel.Contracts.ValueType.Enum => LaneValueType.Enum,
        Panel.Contracts.ValueType.Vec => LaneValueType.Vec,
        _ => LaneValueType.String,
    };

    /// <param name="overrides">Renderer choices not persisted to the layout file, keyed by <see cref="KeyOf"/>.</param>
    public static IReadOnlyList<LaneModel> Build(
        IReadOnlyList<ExpandedLane> lanes,
        TopicCatalog? catalog,
        IReadOnlyDictionary<string, RendererKind> overrides)
    {
        var byName = new Dictionary<string, TopicInfo>();
        if (catalog is not null)
            foreach (var t in catalog.Topics) byName[t.Name] = t;

        var result = new List<LaneModel>(lanes.Count);
        foreach (var lane in lanes)
        {
            byName.TryGetValue(lane.Topic, out var info);
            var type = info is null ? LaneValueType.String : ToLaneType(info.Type);
            var componentCount = info?.Components.Count ?? 0;

            RendererKind renderer;
            if (overrides.TryGetValue(KeyOf(lane), out var chosen))
            {
                renderer = chosen;
            }
            else
            {
                try { renderer = LaneResolver.ResolveRenderer(lane, type); }
                catch (FormatException) { renderer = RendererCatalog.Default(type); }
            }

            var applicable = RendererCatalog.IsApplicable(renderer, type)
                && (renderer != RendererKind.Swatch || RendererCatalog.IsSwatchApplicable(type, componentCount, lane.Space));
            if (!applicable) renderer = RendererCatalog.Default(type);

            result.Add(new LaneModel(lane, info, type, renderer));
        }

        return result;
    }

    /// <summary>Renderers the lane context menu may offer for <paramref name="lane"/>.</summary>
    public static IReadOnlyList<RendererKind> Applicable(LaneModel lane) =>
        lane.Topic is null
            ? Array.Empty<RendererKind>()
            : RendererCatalog.ApplicableRenderers(lane.Type)
                .Where(r => r != RendererKind.Swatch
                    || RendererCatalog.IsSwatchApplicable(lane.Type, lane.Components.Count, lane.Spec.Space))
                .ToList();
}

internal static class ValueFormatter
{
    public static string Format(LaneModel lane, ClientValue v)
    {
        switch (v.Kind)
        {
            case ClientValueKind.Enum:
                return EnumName(lane, v.AsEnumIndex);
            case ClientValueKind.Vec:
                var values = v.AsVec;
                var names = lane.Components;
                return "(" + string.Join(", ", values.Select((x, i) =>
                    (i < names.Count ? names[i] + "=" : string.Empty) + x.ToString("0.##", CultureInfo.InvariantCulture))) + ")";
            case ClientValueKind.Float:
                return v.AsFloat.ToString("0.###", CultureInfo.InvariantCulture);
            case ClientValueKind.Bool:
                return v.AsBool ? "true" : "false";
            case ClientValueKind.Int:
            case ClientValueKind.String:
                return v.ToString();
            default:
                return "-";
        }
    }

    /// <summary>Name looked up in the layout's <c>styles:</c> map (step 2 of style resolution).</summary>
    public static string? StyleKey(LaneModel lane, ClientValue v) => v.Kind switch
    {
        ClientValueKind.Enum => EnumName(lane, v.AsEnumIndex),
        ClientValueKind.Bool => v.AsBool ? "true" : "false",
        ClientValueKind.String => v.AsString,
        _ => null,
    };

    private static string EnumName(LaneModel lane, uint index)
    {
        var names = lane.Topic?.EnumValues;
        return names is not null && index < names.Count ? names[(int)index] : $"#{index}";
    }
}
