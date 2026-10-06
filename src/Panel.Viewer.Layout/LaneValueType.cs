namespace Panel.Viewer.Layout;

/// <summary>
/// The value types a topic can have (mirrors <c>Panel.Core.TopicType</c>, but this project has
/// no dependency on Core so the layout model stays usable without the rest of the stack).
/// </summary>
public enum LaneValueType
{
    Float,
    Int,
    Bool,
    String,
    Enum,
    Vec,
}
