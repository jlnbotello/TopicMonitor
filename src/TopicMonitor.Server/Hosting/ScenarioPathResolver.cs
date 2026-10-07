namespace TopicMonitor.Server.Hosting;

/// <summary>
/// Resolves the configured scenario file path against the host's content root, robust to the several
/// legitimate ways this server gets started: `dotnet run` from the repo root, `dotnet run` from the
/// project directory, running the built exe directly from its bin folder, or being spun up in-process by
/// a test project several directories deeper under the repo root. In every one of those cases the content
/// root differs but the repo root - and therefore `examples/demo.scn` - is always some number of parent
/// directories up, so this walks upward looking for it rather than assuming one fixed layout.
/// </summary>
public static class ScenarioPathResolver
{
    private const int MaxLevelsUp = 8;

    public static string Resolve(string contentRootPath, string configuredPath)
    {
        if (Path.IsPathRooted(configuredPath) && System.IO.File.Exists(configuredPath))
            return configuredPath;

        var direct = Path.GetFullPath(Path.Combine(contentRootPath, configuredPath));
        if (System.IO.File.Exists(direct))
            return direct;

        var dir = new DirectoryInfo(contentRootPath);
        for (var i = 0; i < MaxLevelsUp && dir is not null; i++, dir = dir.Parent)
        {
            var probe = Path.Combine(dir.FullName, configuredPath);
            if (System.IO.File.Exists(probe))
                return probe;
        }

        // Not found anywhere searched; return the direct candidate so the caller's error message points
        // at the most intuitive location even though nothing exists there.
        return direct;
    }
}
