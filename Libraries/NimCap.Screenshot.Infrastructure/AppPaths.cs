namespace NimCap.Screenshot.Services;

public static class AppPaths
{
    // SnapSharp is the pre-rename data location; DataDirectoryService migrates it when selected.
    public static string ApplicationDirectory => AppContext.BaseDirectory;

    // A "portable" marker in the package root (next to the Program folder) pins all user data to <package root>\Data.
    public static string? PortableRoot { get; } = DetectPortableRoot();

    // Model paths are stored relative to the package root so a moved portable folder keeps resolving them.
    public static string ContentRoot => PortableRoot ?? ApplicationDirectory;

    public static DataDirectoryService DataLocation { get; } = new(ApplicationDirectory,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SnapSharp"),
        PortableRoot is null ? null : Path.Combine(PortableRoot, "Data"));

    public static string Root => DataLocation.Root;
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string HistoryRoot => Path.Combine(Root, "History");
    public static string HistoryIndex => Path.Combine(HistoryRoot, "index.json");
    public static string DiagnosticsRoot => Path.Combine(Root, "Diagnostics");
    public static string RenderBenchmark => Path.Combine(DiagnosticsRoot, "render-benchmark.txt");
    public static string PluginsRoot => Path.Combine(Root, "Plugins");

    private static string? DetectPortableRoot()
    {
        var application = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        if (File.Exists(Path.Combine(application, "portable"))) return application;
        var parent = Directory.GetParent(application)?.FullName;
        return parent is not null && File.Exists(Path.Combine(parent, "portable")) ? parent : null;
    }
}
