namespace TopicMonitor.Viewer.Layout;

/// <summary>
/// Resolves <c>groups</c> + <c>templates</c> into a flat list of concrete lanes
/// ("GUI changes are saved back to the file", template usage example).
/// </summary>
public static class TemplateExpander
{
    private const string Placeholder = "{p}";

    public static IReadOnlyList<ExpandedLane> Expand(LayoutModel model)
    {
        var result = new List<ExpandedLane>();

        foreach (var group in model.Groups)
        {
            if (group.IsTemplateUse)
            {
                if (!model.Templates.TryGetValue(group.Use!, out var templateLanes))
                    throw new LayoutModelException($"Group '{group.Name}' uses unknown template '{group.Use}'.");

                if (group.P is null)
                    throw new LayoutModelException($"Group '{group.Name}' uses template '{group.Use}' but has no 'p' parameter.");

                foreach (var lane in templateLanes)
                {
                    result.Add(new ExpandedLane(
                        group.Name,
                        lane.Topic.Replace(Placeholder, group.P),
                        lane.As,
                        lane.Unit,
                        lane.Range,
                        lane.Space));
                }
            }
            else if (group.Lanes is not null)
            {
                foreach (var lane in group.Lanes)
                {
                    result.Add(new ExpandedLane(group.Name, lane.Topic, lane.As, lane.Unit, lane.Range, lane.Space));
                }
            }
            else
            {
                throw new LayoutModelException($"Group '{group.Name}' has neither 'lanes' nor 'use'.");
            }
        }

        return result;
    }
}

/// <summary>Thrown for structural problems in an otherwise-parsed <see cref="LayoutModel"/> (e.g. a dangling template reference).</summary>
public sealed class LayoutModelException(string message) : Exception(message);
