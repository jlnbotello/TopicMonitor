namespace Panel.Viewer.Layout;

/// <summary>
/// Open point 2 (plan.md section 11, "Open points — resolved"): any catalog topic with no
/// matching lane gets one appended, in-memory only, using its type's default renderer.
/// Persisting the result to <c>layout.yaml</c> is a separate, explicit save call
/// (see <see cref="LayoutDocument"/>) — this class never touches disk.
/// </summary>
public static class LayoutDefaults
{
    /// <summary>Name of the synthetic group that auto-generated lanes are appended to.</summary>
    public const string FillGroupName = "Unassigned";

    /// <summary>
    /// Returns a new <see cref="LayoutModel"/> with one lane appended per catalog topic that has
    /// no lane anywhere in <paramref name="model"/> (direct group lanes or template expansions).
    /// Returns <paramref name="model"/> unchanged (same reference) if nothing is missing.
    /// </summary>
    public static LayoutModel FillMissingLanes(LayoutModel model, IEnumerable<CatalogTopic> catalogTopics)
    {
        var existingTopics = TemplateExpander.Expand(model).Select(l => l.Topic).ToHashSet();

        var missingLanes = catalogTopics
            .Where(t => !existingTopics.Contains(t.Name))
            .Select(t => new LaneSpec(t.Name, As: RendererCatalog.Default(t.Type).ToYamlString()))
            .ToList();

        if (missingLanes.Count == 0)
            return model;

        var groups = model.Groups.ToList();
        var fillGroupIndex = groups.FindIndex(g => g.Name == FillGroupName && g.Lanes is not null);

        if (fillGroupIndex >= 0)
        {
            var fillGroup = groups[fillGroupIndex];
            groups[fillGroupIndex] = fillGroup with { Lanes = fillGroup.Lanes!.Concat(missingLanes).ToList() };
        }
        else
        {
            groups.Add(new LayoutGroup(FillGroupName, Lanes: missingLanes));
        }

        return model with { Groups = groups };
    }
}
