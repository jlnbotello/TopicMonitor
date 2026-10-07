namespace TopicMonitor.Viewer.Layout;

/// <summary>Result of checking a layout against the server catalog ("missing or unused topics are listed").</summary>
public sealed record LayoutCatalogCheckResult(IReadOnlyList<string> MissingInCatalog, IReadOnlyList<string> UnusedInLayout)
{
    public bool IsClean => MissingInCatalog.Count == 0 && UnusedInLayout.Count == 0;
}

public static class LayoutCatalogCheck
{
    /// <summary>Lane topics that the catalog does not offer, and catalog topics that no lane shows.</summary>
    public static LayoutCatalogCheckResult Check(LayoutModel model, IEnumerable<string> catalogTopicNames)
    {
        var catalog = catalogTopicNames.ToHashSet();
        var laneTopics = TemplateExpander.Expand(model).Select(l => l.Topic).ToList();

        var missing = laneTopics.Where(t => !catalog.Contains(t)).Distinct().OrderBy(t => t, StringComparer.Ordinal).ToList();
        var used = laneTopics.ToHashSet();
        var unused = catalog.Where(t => !used.Contains(t)).OrderBy(t => t, StringComparer.Ordinal).ToList();

        return new LayoutCatalogCheckResult(missing, unused);
    }

    /// <summary>
    /// Position of the lane entry in the file that produced <paramref name="lane"/> (template entries first,
    /// then direct group lanes, each in file order), or <c>null</c> when it is not in <paramref name="model"/>
    /// (e.g. auto-generated) or two entries are identical. Pass to <see cref="LayoutDocument.WithLaneRenderer(int, RendererKind)"/>.
    /// </summary>
    public static int? FindLaneIndex(LayoutModel model, ExpandedLane lane)
    {
        var index = 0;
        var matches = new List<int>();

        foreach (var (templateName, specs) in model.Templates)
        {
            foreach (var spec in specs)
            {
                var produces = model.Groups.Any(g =>
                    g.Name == lane.GroupName && g.Use == templateName && g.P is not null
                    && spec.Topic.Replace("{p}", g.P) == lane.Topic);
                if (produces && Describes(spec, lane)) matches.Add(index);
                index++;
            }
        }

        foreach (var group in model.Groups)
        {
            if (group.Lanes is null) continue;
            foreach (var spec in group.Lanes)
            {
                if (group.Name == lane.GroupName && spec.Topic == lane.Topic && Describes(spec, lane)) matches.Add(index);
                index++;
            }
        }

        return matches.Count == 1 ? matches[0] : null;
    }

    private static bool Describes(LaneSpec spec, ExpandedLane lane) =>
        spec.As == lane.As && spec.Unit == lane.Unit && spec.Space == lane.Space
        && (spec.Range is null ? lane.Range is null : lane.Range is not null && spec.Range.SequenceEqual(lane.Range));
}
