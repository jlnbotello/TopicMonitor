using System.Text.RegularExpressions;

namespace Panel.Core;

/// <summary>Matches topic names against glob patterns such as "led.*.state" or "display.line*.text".</summary>
public sealed class TopicFilter
{
    public IReadOnlyList<string> Patterns { get; }

    private readonly List<Regex> _regexes;

    public static readonly TopicFilter All = new(new[] { "*" });

    public TopicFilter(IReadOnlyList<string> patterns)
    {
        if (patterns is null || patterns.Count == 0)
            throw new ArgumentException("At least one pattern is required.", nameof(patterns));
        Patterns = patterns;
        _regexes = patterns.Select(ToRegex).ToList();
    }

    public bool Matches(string topicName) => _regexes.Any(r => r.IsMatch(topicName));

    private static Regex ToRegex(string glob)
    {
        var pattern = "^" + string.Join("", glob.Select(c => c switch
        {
            '*' => ".*",
            '.' => "\\.",
            _ when "\\^$+?()[]{}|".IndexOf(c) >= 0 => "\\" + c,
            _ => c.ToString(),
        })) + "$";
        return new Regex(pattern, RegexOptions.Compiled);
    }
}
