using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Backup;

/// A job another app asks for: `--job-from <file.json>` with { id?, name, sources }.
public sealed record JobSpec(string? Id, string Name, List<string> Sources);

// The Protagonist Labs folder, %APPDATA%\ProtagonistLabs: every app leaves its card in apps\<id>.json and
// what it shares in data\<id>.<kind>.json. No server, nothing sent anywhere. Files written by other apps
// are not trusted: missing, unreadable or odd means absent, never an error on screen.
static class Ecosystem
{
    public static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ProtagonistLabs");
    public const string ShelfJobId = "shelf-saves"; // Shelf's "Back up all saves with Backup Labs"

    /// The card, at every start, off the UI thread: the folder is a bonus and must never slow the start.
    public static void WriteCard(string version) => Task.Run(() =>
    {
        try
        {
            var file = Path.Combine(Root, "apps", "backuplabs.json");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["schema"] = 1,
                ["updated"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["id"] = "backuplabs",
                ["name"] = "Backup Labs",
                ["version"] = version,
                ["exe"] = Environment.ProcessPath!,
                ["edition"] = "free",
                ["paid"] = false,
                ["page"] = "https://protagonistlabs.app/backuplabs/",
                ["commands"] = new Dictionary<string, string[]> { ["job-from"] = new[] { "--job-from", "{file}" } },
            }, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(file + ".tmp", file, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    });

    /// The paths that can be backed up: full paths that exist, each once.
    public static List<string> Existing(IEnumerable<string> paths) =>
        paths.Where(p => Path.IsPathFullyQualified(p) && Path.Exists(p))
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    static List<string> Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : new();

    /// A job request, from the file another app wrote or from the line it left for the running instance.
    /// Null when it is not one, or names nothing that exists.
    public static JobSpec? ReadSpec(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            var id = r.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
            // the id ends up in file names (Elevated.FileFor), so only plain characters
            if (id != null && !Regex.IsMatch(id, "^[A-Za-z0-9_-]{1,64}$")) return null;
            var name = r.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()!.Trim() : "";
            if (name.Length > 100) name = name[..100];
            var sources = Existing(Strings(r, "sources"));
            return sources.Count == 0 ? null : new JobSpec(id, name, sources);
        }
        catch (JsonException) { return null; }
    }

    /// One line, so it can go through incoming.txt with the paths a second launch leaves.
    public static string Line(JobSpec s) => JsonSerializer.Serialize(s, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    /// Every save folder or file Shelf knows of (data\shelf.games.json), or null when there is no usable list.
    public static List<string>? ShelfSaves()
    {
        try { return ShelfSaves(File.ReadAllText(Path.Combine(Root, "data", "shelf.games.json"))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public static List<string>? ShelfSaves(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("schema", out var v) || v.ValueKind != JsonValueKind.Number || v.GetInt32() != 1
                || !r.TryGetProperty("games", out var games) || games.ValueKind != JsonValueKind.Array) return null;
            var saves = Existing(games.EnumerateArray().Where(g => g.ValueKind == JsonValueKind.Object).SelectMany(g => Strings(g, "saves")));
            return saves.Count == 0 ? null : saves;
        }
        catch (Exception e) when (e is JsonException or FormatException) { return null; }
    }

    /// Sister apps installed on this PC (their card exists and its exe is still there), and whether the
    /// user bought one of the paid ones.
    public static (HashSet<string> Installed, bool Bought) Sisters()
    {
        var installed = new HashSet<string>();
        var bought = false;
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(Root, "apps"), "*.json"))
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    var c = doc.RootElement;
                    if (c.ValueKind != JsonValueKind.Object || !c.TryGetProperty("schema", out var v) || v.ValueKind != JsonValueKind.Number || v.GetInt32() != 1) continue;
                    if (!c.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) continue;
                    if (!c.TryGetProperty("exe", out var exe) || exe.ValueKind != JsonValueKind.String || !File.Exists(exe.GetString())) continue;
                    installed.Add(id.GetString()!);
                    if (c.TryGetProperty("paid", out var p) && p.ValueKind == JsonValueKind.True
                        && c.TryGetProperty("edition", out var ed) && ed.ValueKind == JsonValueKind.String && ed.GetString() == "full") bought = true;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or FormatException) { }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // no folder yet
        return (installed, bought);
    }
}
