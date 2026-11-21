using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using NimCap.Screenshot.Services;

namespace NimCap.Screenshot.RegressionChecks;

internal static partial class Program
{
    private static void DataDirectoryChecks()
    {
        var root = Path.Combine(Root, "data-directory");
        var executable = Path.Combine(root, "exe");
        var legacy = Path.Combine(root, "legacy");
        var target = Path.Combine(root, "new-data");
        Directory.CreateDirectory(executable);
        var location = new DataDirectoryService(executable, legacy);
        Require(DataDirectoryService.SameDirectory(location.Root, executable), "New installs must default to the exe folder.");
        var model = Path.Combine(executable, "models", "ocr", "model.onnx");
        Directory.CreateDirectory(Path.GetDirectoryName(model)!);
        var bytes = Enumerable.Range(0, 8192).Select(i => (byte)(i % 251)).ToArray();
        File.WriteAllBytes(model, bytes);
        File.WriteAllText(model + ".part", "partial download");
        File.WriteAllText(Path.Combine(executable, "NimCap.Screenshot.exe"), "application, never migrate");
        File.WriteAllText(Path.Combine(executable, "notes.txt"), "unrelated file");
        var external = Path.Combine(root, "external", "model.onnx");
        var neighbor = executable + "-other\\model.onnx";
        File.WriteAllText(Path.Combine(executable, "settings.json"), JsonSerializer.Serialize(new
        {
            OcrDetectionModel = model, ExternalModel = external, Neighbor = neighbor,
            UnmanagedFile = Path.Combine(executable, "notes.txt"),
            CutoutModels = new { custom = new { ModelPath = model } },
            Other = new[] { model, "plain text" }
        }));
        var history = Path.Combine(executable, "History");
        Directory.CreateDirectory(history);
        File.WriteAllText(Path.Combine(history, "image.png"), "history image");
        File.WriteAllText(Path.Combine(history, "index.json"), JsonSerializer.Serialize(new[]
        { new { ImagePath = Path.Combine(history, "image.png"), ThumbnailPath = Path.Combine(history, "image.png") } }));
        location.ScheduleChange(target);
        Require(location.Root == executable && File.Exists(model), "Scheduling must preserve the running session's data.");
        var restarted = new DataDirectoryService(executable, legacy);
        Require(restarted.Root == executable && restarted.SelectedDirectory == target, "Pending changes must survive restart.");
        restarted.ScheduleChange(executable);
        Require(new DataDirectoryService(executable, legacy).SelectedDirectory == executable, "The user must be able to cancel a pending change.");
        restarted.ScheduleChange(target);

        foreach (var bad in new[] { "relative", Path.Combine(executable, "nested"), root })
            ExpectMigrationFailure(() => restarted.ValidateChange(bad));

        // A destination conflict introduced after scheduling must not overwrite either folder.
        Directory.CreateDirectory(Path.Combine(target, "models"));
        File.WriteAllText(Path.Combine(target, "models", "existing"), "destination");
        ExpectMigrationFailure(restarted.ApplyPendingChange);
        Require(File.Exists(model) && File.ReadAllText(Path.Combine(target, "models", "existing")) == "destination",
            "Conflicting destination data was changed.");
        File.Delete(Path.Combine(target, "models", "existing"));
        Directory.Delete(Path.Combine(target, "models"));
        File.WriteAllText(Path.Combine(target, "keep.txt"), "unrelated destination file");

        // A failure after publishing the copied files must roll them back and retain the source.
        using (var lockedConfiguration = new FileStream(Path.Combine(executable, "data-location.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            ExpectMigrationFailure(restarted.ApplyPendingChange);
        Require(File.Exists(model) && !Directory.Exists(Path.Combine(target, "models")) &&
            File.Exists(Path.Combine(target, "keep.txt")), "Failed commit did not roll back safely.");
        Require(new DataDirectoryService(executable, legacy).Root == executable, "A failed migration changed the active root.");

        restarted.ApplyPendingChange();
        var movedModel = Path.Combine(target, "models", "ocr", "model.onnx");
        Require(File.ReadAllBytes(movedModel).SequenceEqual(bytes) && File.Exists(movedModel + ".part"), "Model bytes or resumable downloads were lost.");
        Require(!File.Exists(model) && !File.Exists(Path.Combine(executable, "settings.json")), "Successful migration did not remove old managed data.");
        Require(File.Exists(Path.Combine(executable, "NimCap.Screenshot.exe")) && File.Exists(Path.Combine(executable, "notes.txt")),
            "Migration moved unrelated files from the executable folder.");
        var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(target, "settings.json")))!;
        Require(settings["OcrDetectionModel"]!.GetValue<string>() == movedModel &&
            settings["CutoutModels"]!["custom"]!["ModelPath"]!.GetValue<string>() == movedModel &&
            settings["Other"]![0]!.GetValue<string>() == movedModel, "Nested model paths were not updated.");
        Require(settings["ExternalModel"]!.GetValue<string>() == external && settings["Neighbor"]!.GetValue<string>() == neighbor &&
            settings["UnmanagedFile"]!.GetValue<string>() == Path.Combine(executable, "notes.txt"),
            "External paths were changed.");
        var index = JsonNode.Parse(File.ReadAllText(Path.Combine(target, "History", "index.json")))!;
        Require(File.Exists(index[0]!["ImagePath"]!.GetValue<string>()), "Migrated history paths no longer resolve.");
        Require(new DataDirectoryService(executable, legacy).Root == target, "The committed folder was not persisted.");

        // Legacy data must remain discoverable without silently creating an empty portable installation.
        var upgradeExe = Path.Combine(root, "upgrade-exe");
        Directory.CreateDirectory(upgradeExe);
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "{}");
        var upgrade = new DataDirectoryService(upgradeExe, legacy);
        Require(upgrade.Root == legacy && upgrade.DefaultDirectory == upgradeExe, "Existing installations lost their old data.");
        upgrade.ScheduleChange(upgradeExe);
        upgrade.ApplyPendingChange();
        Require(File.Exists(Path.Combine(upgradeExe, "settings.json")), "Migrating legacy data to the exe folder failed.");

        // Invalid indexes must not result in a partial move or silently discarded history.
        var brokenExe = Path.Combine(root, "broken-exe");
        Directory.CreateDirectory(brokenExe);
        File.WriteAllText(Path.Combine(brokenExe, "settings.json"), "invalid JSON");
        var broken = new DataDirectoryService(brokenExe, legacy);
        var brokenTarget = Path.Combine(root, "broken-target");
        broken.ScheduleChange(brokenTarget);
        ExpectMigrationFailure(broken.ApplyPendingChange);
        Require(File.ReadAllText(Path.Combine(brokenExe, "settings.json")) == "invalid JSON" &&
            !Directory.EnumerateFileSystemEntries(brokenTarget).Any(), "Invalid metadata lost source data or left partial destination data.");

        // Portable mode pins every user file to <package root>\Data and ignores per-install location metadata.
        var portableExe = Path.Combine(root, "portable-exe");
        var portableData = Path.Combine(root, "portable-data");
        Directory.CreateDirectory(portableExe);
        File.WriteAllText(Path.Combine(portableExe, "data-location.json"),
            JsonSerializer.Serialize(new { CurrentDirectory = Path.Combine(root, "ignored"), NextDirectory = (string?)null }));
        var portable = new DataDirectoryService(portableExe, legacy, portableData);
        Require(portable.Root == portableData && portable.SelectedDirectory == portableData, "Portable mode must pin the Data folder.");
        Require(new DataDirectoryService(portableExe, legacy, portableData).Root == portableData, "Portable mode must ignore location metadata.");
        Console.WriteLine("PASS: exe default, legacy discovery, pending changes, model/history path migration, conflicts, failed-commit rollback and portable Data folder.");
    }

    private static void ExpectMigrationFailure(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or ArgumentException or JsonException or UnauthorizedAccessException) { return; }
        throw new InvalidOperationException("Expected data migration to reject an invalid destination or failed write.");
    }
}
