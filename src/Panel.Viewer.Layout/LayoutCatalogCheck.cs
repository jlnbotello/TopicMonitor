namespace Panel.Viewer.Layout;

/// <summary>Result of checking a layout against the server catalog (plan section 8: "missing or unused topics are listed").</summary>
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
    /// The topic exactly as written in the YAML for <paramref name="lane"/> (a template pattern such as
    /// <c>{p}.raw</c> for template lanes, the concrete name for direct lanes), or <c>null</c> when the lane is
    /// not in <paramref name="model"/> (e.g. auto-generated) or shares its topic with a sibling lane (the
    /// YAML patch locates lanes by topic, so it would edit the wrong one). Pass to <see cref="LayoutDocument.WithLaneRenderer"/>.
    /// </summary>
    public static string? FindYamlTopic(LayoutModel model, ExpandedLane lane)
    {
        foreach (var group in model.Groups)
        {
            if (group.Name != lane.GroupName) continue;

            if (group.IsTemplateUse && group.P is not null && model.Templates.TryGetValue(group.Use!, out var templateLanes))
            {
                foreach (var spec in templateLanes)
                    if (spec.Topic.Replace("{p}", group.P) == lane.Topic)
                        return templateLanes.Count(s => s.Topic == spec.Topic) == 1 ? spec.Topic : null;
            }
            else if (group.Lanes is not null)
            {
                foreach (var spec in group.Lanes)
                    if (spec.Topic == lane.Topic)
                        return group.Lanes.Count(s => s.Topic == spec.Topic) == 1 ? spec.Topic : null;
            }
        }

        return null;
    }
}
