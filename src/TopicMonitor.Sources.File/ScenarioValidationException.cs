namespace TopicMonitor.Sources.File;

/// <summary>
/// A scenario that parsed correctly but failed semantic validation: an undeclared topic, a value that doesn't
/// match its topic's declared type, or a topic name already owned by another producer on the bus (plan section 4:
/// "a file topic that a processor also publishes is rejected at load").
/// </summary>
public sealed class ScenarioValidationException : Exception
{
    public ScenarioValidationException(string message) : base(message)
    {
    }
}
