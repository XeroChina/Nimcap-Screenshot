using System.Text.Json;
using System.Text.Json.Nodes;
using NimCap.Screenshot.Localization;

namespace NimCap.Screenshot.Services;

/// <summary>Changes storage only at startup, before models, history or plugins are opened.</summary>
public sealed class DataDirectoryService
{
    private static readonly string[] ManagedNames =
    [
        "settings.json", "History", "Diagnostics", "Plugins", "models", "downloads",
        "startup-error.log", "capture-error.log", "hotkey-status.log"
    ];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _locationFile;
    public string DefaultDirectory { get; }
    public string Root { get; private set; }
    public string SelectedDirectory { get; private set; }

    public DataDirectoryService(string executableDirectory, string legacyDirectory, string? portableDirectory = null)
    {
        DefaultDirectory = Normalize(executableDirectory);
        _locationFile = Path.Combine(DefaultDirectory, "data-location.json");
        if (portableDirectory is not null)
        {
            // Portable mode pins every user file to <package root>\Data and ignores per-install metadata.
            Root = SelectedDirectory = Normalize(portableDirectory);
            return;
        }
        if (File.Exists(_locationFile))
        {
            var location = JsonSerializer.Deserialize<Location>(File.ReadAllText(_locationFile))
                ?? throw new InvalidDataException(UiText.Get("InvalidDataLocation"));
            Root = Normalize(ModelPathService.Resolve(location.CurrentDirectory, DefaultDirectory));
            SelectedDirectory = location.NextDirectory is null ? Root : Normalize(ModelPathService.Resolve(location.NextDirectory, DefaultDirectory));
        }
        else
        {
            // Existing installations keep their data until the user chooses to migrate it.
            Root = !HasData(DefaultDirectory) && HasData(legacyDirectory) ? Normalize(legacyDirectory) : DefaultDirectory;
            SelectedDirectory = Root;
        }
    }

    public static bool SameDirectory(string left, string right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

    public static bool HasData(string directory) => ManagedNames.Any(name =>
        File.Exists(Path.Combine(directory, name)) || Directory.Exists(Path.Combine(directory, name)));

    public string ValidateChange(string directory)
    {
        var target = Normalize(directory);
        if (SameDirectory(target, Root)) return target;
        if (IsWithin(target, Root) || IsWithin(Root, target))
            throw new IOException(UiText.Get("DataFoldersMustNotOverlap"));
        RejectLinks(Root);
        RejectLinks(target);
        foreach (var name in ManagedNames)
        {
            var path = Path.Combine(target, name);
            if (File.Exists(path) || Directory.Exists(path))
                throw new IOException(UiText.Format("DataFolderConflict", path));
        }
        Directory.CreateDirectory(target);
        var probe = Path.Combine(target, ".nimcap-screenshot-write-" + Guid.NewGuid().ToString("N"));
        using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
        return target;
    }

    public void ScheduleChange(string directory)
    {
        var target = ValidateChange(directory);
        WriteLocation(Root, SameDirectory(target, Root) ? null : target);
        SelectedDirectory = target;
    }

    public void ApplyPendingChange()
    {
        if (SameDirectory(Root, SelectedDirectory))
        {
            // Upgrade existing location metadata before a portable folder is moved.
            if (File.Exists(_locationFile))
            {
                var location = JsonSerializer.Deserialize<Location>(File.ReadAllText(_locationFile))!;
                if (location.CurrentDirectory != ModelPathService.ForStorage(Root, DefaultDirectory)) WriteLocation(Root, null);
            }
            return;
        }
        var source = Root;
        var target = ValidateChange(SelectedDirectory);
        var staging = Path.Combine(target, ".nimcap-screenshot-migration-" + Guid.NewGuid().ToString("N"));
        var published = new List<string>();
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var name in ManagedNames)
            {
                var from = Path.Combine(source, name);
                var to = Path.Combine(staging, name);
                if (Directory.Exists(from)) CopyDirectory(from, to);
                else if (File.Exists(from)) { RejectLinks(from); File.Copy(from, to); }
            }
            RemapJson(Path.Combine(staging, "settings.json"), source, target, modelPaths: true);
            RemapJson(Path.Combine(staging, "History", "index.json"), source, target);
            foreach (var path in Directory.EnumerateFileSystemEntries(staging))
            {
                var destination = Path.Combine(target, Path.GetFileName(path));
                if (Directory.Exists(path)) Directory.Move(path, destination);
                else File.Move(path, destination);
                published.Add(destination);
            }
            // Commit only once all files and rewritten indexes are in place.
            WriteLocation(target, null);
            Root = SelectedDirectory = target;
        }
        catch
        {
            foreach (var path in published) TryDelete(path);
            throw;
        }
        finally { TryDelete(staging); }

        // The old data is removed only after the new location has been committed.
        // A locked source file may remain as a harmless backup.
        foreach (var name in ManagedNames) TryDelete(Path.Combine(source, name));
    }

    private void WriteLocation(string current, string? next)
    {
        var temporary = _locationFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Location(
                ModelPathService.ForStorage(current, DefaultDirectory),
                next is null ? null : ModelPathService.ForStorage(next, DefaultDirectory)), JsonOptions));
            File.Move(temporary, _locationFile, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string Normalize(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory.Trim()))
            throw new ArgumentException(UiText.Get("DataFolderMustBeAbsolute"));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory.Trim()));
    }

    private static bool IsWithin(string path, string parent) =>
        path.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void RejectLinks(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException(UiText.Get("DataFolderLinksUnsupported"));
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException(UiText.Get("DataFolderLinksUnsupported"));
    }

    private static void CopyDirectory(string source, string target)
    {
        RejectLinks(source);
        Directory.CreateDirectory(target);
        foreach (var path in Directory.EnumerateFileSystemEntries(source))
        {
            RejectLinks(path);
            var destination = Path.Combine(target, Path.GetFileName(path));
            if (Directory.Exists(path)) CopyDirectory(path, destination);
            else File.Copy(path, destination);
        }
    }

    private void RemapJson(string file, string source, string target, bool modelPaths = false)
    {
        if (!File.Exists(file)) return;
        var node = JsonNode.Parse(File.ReadAllText(file));
        // Resolve portable paths before relocating managed files; preserve unknown settings.
        if (modelPaths && node is JsonObject settings)
            ModelPathService.Transform(settings, path => ModelPathService.Resolve(path, DefaultDirectory));
        Rewrite(node);
        if (modelPaths && node is JsonObject rewritten)
            ModelPathService.Transform(rewritten, path => ModelPathService.ForStorage(path, DefaultDirectory));
        File.WriteAllText(file, node?.ToJsonString(JsonOptions) ?? "null");

        void Rewrite(JsonNode? current)
        {
            if (current is JsonObject obj)
                foreach (var key in obj.Select(pair => pair.Key).ToArray())
                {
                    var replacement = Value(obj[key]);
                    if (!ReferenceEquals(replacement, obj[key])) obj[key] = replacement;
                }
            else if (current is JsonArray array)
                for (var i = 0; i < array.Count; i++)
                {
                    var replacement = Value(array[i]);
                    if (!ReferenceEquals(replacement, array[i])) array[i] = replacement;
                }
        }
        JsonNode? Value(JsonNode? value)
        {
            if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text) && Path.IsPathFullyQualified(text))
            {
                if (SameDirectory(text, source)) return JsonValue.Create(target);
                var full = Path.GetFullPath(text);
                if (IsWithin(full, source))
                {
                    var relative = Path.GetRelativePath(source, full);
                    var first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                    if (ManagedNames.Contains(first, StringComparer.OrdinalIgnoreCase))
                        return JsonValue.Create(Path.Combine(target, relative));
                }
            }
            Rewrite(value);
            return value;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record Location(string CurrentDirectory, string? NextDirectory);
}
