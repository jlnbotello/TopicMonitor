using System.IO;

namespace TopicMonitor.Viewer.Wpf;

public sealed record ViewerOptions(string ServerAddress, string LayoutPath)
{
    public const string DefaultServerAddress = "http://localhost:5279";

    /// <summary>Recognizes <c>--server &lt;url&gt;</c> and <c>--layout &lt;path&gt;</c>.</summary>
    public static ViewerOptions Parse(IReadOnlyList<string> args)
    {
        var server = DefaultServerAddress;
        string? layout = null;

        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--server" && i + 1 < args.Count) server = args[++i];
            else if (args[i] == "--layout" && i + 1 < args.Count) layout = args[++i];
        }

        return new ViewerOptions(server, layout ?? FindDefaultLayout());
    }

    private static string FindDefaultLayout()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "examples", "layout.yaml");
            if (File.Exists(candidate)) return candidate;
        }

        return Path.Combine(Environment.CurrentDirectory, "layout.yaml");
    }
}
