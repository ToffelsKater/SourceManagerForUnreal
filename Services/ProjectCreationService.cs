using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using UnrealManager.Models;

namespace UnrealManager.Services;

/// <summary>One project template shipped in an engine's Templates folder (TP_ThirdPerson, TP_BlankBP, …).</summary>
public sealed record ProjectTemplate(string Folder, string Name, string DisplayName, string Description, bool IsCode)
{
    public string Display => $"{DisplayName}  ({(IsCode ? "C++" : "Blueprint")})";
}

/// <summary>
/// Creates a project from one of the engine's templates the way the editor's New Project dialog
/// does (GameProjectUtils::CreateProjectFromTemplate): copy the template minus what its
/// TemplateDefs.ini ignores, put the project's name in place of the template's in paths and code,
/// pull in the shared content packs it names, and redirect the old module and class names so the
/// template's Blueprints still find their C++ parents.
/// </summary>
public static partial class ProjectCreationService
{
    /// <summary>The editor's own rule: a letter first, then letters, digits or underscores — 20 at most.</summary>
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,19}$")]
    public static partial Regex ValidName();

    /// <summary>The templates in the engine's Templates folder, Blueprint before C++ for the same template.</summary>
    public static List<ProjectTemplate> FindTemplates(string engineRoot)
    {
        var templatesDir = Path.Combine(engineRoot, "Templates");
        if (string.IsNullOrWhiteSpace(engineRoot) || !Directory.Exists(templatesDir)) return [];

        var templates = new List<ProjectTemplate>();
        foreach (var folder in Directory.EnumerateDirectories(templatesDir))
        {
            var name = Path.GetFileName(folder);
            var defsFile = DefsFile(folder);
            if (!File.Exists(Path.Combine(folder, name + ".uproject")) || !File.Exists(defsFile)) continue;

            var defs = ReadDefs(defsFile);
            templates.Add(new ProjectTemplate(folder, name,
                English(defs, "LocalizedDisplayNames") ?? name,
                English(defs, "LocalizedDescriptions") ?? "",
                Directory.Exists(Path.Combine(folder, "Source"))));
        }

        return templates
            .OrderBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.IsCode)
            .ToList();
    }

    /// <summary>
    /// Creates &lt;location&gt;\&lt;name&gt;\&lt;name&gt;.uproject and returns its path. Slow (it copies the
    /// template's content), so call it off the UI thread. On failure or cancellation the half-made
    /// folder is removed again, so the same name can simply be retried.
    /// </summary>
    public static string Create(
        ProjectTemplate template, string location, string name, string engineRoot,
        Action<string> log, CancellationToken ct)
    {
        var dest = Path.Combine(location, name);
        if (Directory.Exists(dest) && Directory.EnumerateFileSystemEntries(dest).Any())
            throw new InvalidOperationException($"{dest} already exists and is not empty.");

        var defs = ReadDefs(DefsFile(template.Folder));
        List<string> Values(string key) => defs
            .Where(d => d.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            .Select(d => d.Value
                .Replace("%TEMPLATENAME%", template.Name)
                .Replace("%TEMPLATENAME_UPPERCASE%", template.Name.ToUpperInvariant())
                .Replace("%TEMPLATENAME_LOWERCASE%", template.Name.ToLowerInvariant())
                .Replace("%PROJECTNAME%", name)
                .Replace("%PROJECTNAME_UPPERCASE%", name.ToUpperInvariant())
                .Replace("%PROJECTNAME_LOWERCASE%", name.ToLowerInvariant()))
            .ToList();

        var filesToIgnore = Values("FilesToIgnore").Select(v => v.Trim('"'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var foldersToIgnore = Values("FoldersToIgnore").Select(v => v.Trim('"')).ToList();
        var folderRenames = Values("FolderRenames").Select(v => (From: Field(v, "From"), To: Field(v, "To"))).ToList();
        var filenameReplacements = Values("FilenameReplacements").Select(Replacement.Parse).ToList();
        var contentReplacements = Values("ReplacementsInFiles").Select(Replacement.Parse).ToList();

        // Resolved before anything is written: a missing pack would leave the template's levels
        // pointing at characters and meshes that are not there.
        var packs = Values("SharedContentPacks").Select(v => ResolvePack(engineRoot, v)).ToList();

        try
        {
            log($"Copying template {template.Name} to {dest}…");
            Directory.CreateDirectory(Path.Combine(dest, "Content")); // blank templates ship none
            var toFix = new List<string>();
            var classRenames = new List<(string Old, string New)>();
            var copied = 0;

            foreach (var src in Directory.EnumerateFiles(template.Folder, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();

                var rel = Path.GetRelativePath(template.Folder, src).Replace('\\', '/');
                if (filesToIgnore.Contains(rel) ||
                    foldersToIgnore.Any(f => rel.StartsWith(f + "/", StringComparison.OrdinalIgnoreCase)))
                    continue;

                var dir = Path.GetDirectoryName(rel)?.Replace('\\', '/') ?? "";
                foreach (var (from, to) in folderRenames)
                    if (rel.StartsWith(from + "/", StringComparison.OrdinalIgnoreCase))
                        dir = to + dir[from.Length..];

                var ext = Path.GetExtension(rel).TrimStart('.');
                var oldName = Path.GetFileNameWithoutExtension(rel);
                var newName = oldName;
                foreach (var r in filenameReplacements.Where(r => r.AppliesTo(ext)))
                    newName = newName.Replace(r.From, r.To, r.Comparison);

                var target = Path.Combine(dest, dir, newName + Path.GetExtension(rel));
                CopyFile(src, target);
                copied++;

                if (contentReplacements.Any(r => r.AppliesTo(ext))) toFix.Add(target);

                // A renamed UObject header renames its class too; Blueprints made from the template
                // still name the old class, so the engine has to be told where it went.
                if (newName != oldName && ext.Equals("h", StringComparison.OrdinalIgnoreCase) &&
                    File.ReadAllText(target).Contains(".generated.h\"", StringComparison.OrdinalIgnoreCase))
                    classRenames.Add((oldName, newName));
            }

            foreach (var (mount, packDir) in packs)
            {
                log($"Adding shared content pack {mount}…");
                foreach (var sub in new[] { "Content", "__ExternalActors__", "__ExternalObjects__" })
                {
                    var from = Path.Combine(packDir, sub);
                    if (!Directory.Exists(from)) continue;
                    var to = sub == "Content"
                        ? Path.Combine(dest, "Content", mount)
                        : Path.Combine(dest, "Content", sub, mount);

                    foreach (var src in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
                    {
                        ct.ThrowIfCancellationRequested();
                        CopyFile(src, Path.Combine(to, Path.GetRelativePath(from, src)));
                        copied++;
                    }
                }
            }
            log($"Copied {copied} files.");

            log($"Renaming {template.Name} to {name} in {toFix.Count} source and config files…");
            foreach (var file in toFix) ReplaceInFile(file, contentReplacements);

            var engineIni = Path.Combine(dest, "Config", "DefaultEngine.ini");
            const string engineSection = "/Script/Engine.Engine";
            SetIni(engineIni, engineSection, "+ActiveGameNameRedirects",
                $"(OldGameName=\"/Script/{template.Name}\",NewGameName=\"/Script/{name}\")", replace: false);
            SetIni(engineIni, engineSection, "+ActiveGameNameRedirects",
                $"(OldGameName=\"{template.Name}\",NewGameName=\"/Script/{name}\")", replace: false);
            foreach (var (oldClass, newClass) in classRenames)
                SetIni(engineIni, engineSection, "+ActiveClassRedirects",
                    $"(OldClassName=\"{oldClass}\",NewClassName=\"{newClass}\")", replace: false);

            SetIni(Path.Combine(dest, "Config", "DefaultGame.ini"), "/Script/EngineSettings.GeneralProjectSettings",
                "ProjectID", Guid.NewGuid().ToString("N").ToUpperInvariant(), replace: true);

            var projectFile = Path.Combine(dest, name + ".uproject");
            WriteProjectFile(Path.Combine(template.Folder, template.Name + ".uproject"), projectFile,
                template.Name, name, engineRoot);
            log($"Wrote {projectFile}");
            return projectFile;
        }
        catch
        {
            try { Directory.Delete(dest, recursive: true); } catch { /* best effort — the real error is rethrown */ }
            throw;
        }
    }

    private static string DefsFile(string templateFolder) => Path.Combine(templateFolder, "Config", "TemplateDefs.ini");

    /// <summary>The Key=Value lines of a TemplateDefs.ini, in order; keys repeat, so this is a list, not a map.</summary>
    private static List<(string Key, string Value)> ReadDefs(string path) =>
        File.ReadAllLines(path)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && l[0] is not (';' or '['))
            .Select(l => l.Split('=', 2))
            .Where(p => p.Length == 2)
            .Select(p => (p[0].Trim(), p[1].Trim()))
            .ToList();

    /// <summary>The English text of a (Language="en",Text="…") entry.</summary>
    private static string? English(List<(string Key, string Value)> defs, string key) => defs
        .Where(d => d.Key == key)
        .Select(d => Regex.Match(d.Value, "Language=\"en\",\\s*Text=\"(.*)\"\\)\\s*$"))
        .FirstOrDefault(m => m.Success)?.Groups[1].Value;

    /// <summary>A Name="value" field of an Unreal struct literal such as (From="a",To="b").</summary>
    private static string Field(string value, string name) =>
        Regex.Match(value, $"(?<!\\w){name}=\"([^\"]*)\"").Groups[1].Value;

    /// <summary>A Name=("a","b") list field — the quotes are optional, Unreal writes it both ways.</summary>
    private static string[] ListField(string value, string name) =>
        Regex.Match(value, $"(?<!\\w){name}=\\(([^)]*)\\)").Groups[1].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.Trim('"'))
            .ToArray();

    private sealed record Replacement(string[] Extensions, string From, string To, StringComparison Comparison)
    {
        public static Replacement Parse(string value) => new(
            ListField(value, "Extensions"), Field(value, "From"), Field(value, "To"),
            value.Contains("bCaseSensitive=true", StringComparison.OrdinalIgnoreCase)
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase);

        public bool AppliesTo(string extension) =>
            From.Length > 0 && Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A shared pack lives in Templates/TemplateResources/&lt;level&gt;/&lt;mount&gt;. Desktop projects take
    /// the High version when the pack has one, as the editor does by default.
    /// </summary>
    private static (string Mount, string Dir) ResolvePack(string engineRoot, string value)
    {
        var mount = Field(value, "MountName");
        var levels = ListField(value, "DetailLevels");
        var level = levels.Contains("High", StringComparer.OrdinalIgnoreCase) ? "High" : levels.FirstOrDefault() ?? "High";
        var dir = Path.Combine(engineRoot, "Templates", "TemplateResources", level, mount);
        if (!Directory.Exists(dir))
            throw new InvalidOperationException(
                $"The template needs the shared content pack '{mount}', which is missing from {dir}.");
        return (mount, dir);
    }

    private static void CopyFile(string src, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(src, target, overwrite: true);
        new FileInfo(target).IsReadOnly = false; // an engine synced from Perforce ships read-only files
    }

    private static (string Text, Encoding Encoding) ReadText(string path)
    {
        // BOM-less unless the file has one, so an ASCII file is written back as it was.
        using var reader = new StreamReader(path, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();
        return (text, reader.CurrentEncoding);
    }

    /// <summary>
    /// Applies every replacement in two passes through placeholders, as the editor does, so a
    /// replacement can never match text an earlier one just wrote.
    /// </summary>
    private static void ReplaceInFile(string file, List<Replacement> replacements)
    {
        var ext = Path.GetExtension(file).TrimStart('.');
        var (text, encoding) = ReadText(file);
        var applicable = replacements.Select((r, i) => (r, Placeholder: $"{{{{{{REPLACE:{i}}}}}}}"))
            .Where(x => x.r.AppliesTo(ext))
            .ToList();

        foreach (var (r, placeholder) in applicable) text = text.Replace(r.From, placeholder, r.Comparison);
        foreach (var (r, placeholder) in applicable) text = text.Replace(placeholder, r.To);
        File.WriteAllText(file, text, encoding);
    }

    /// <summary>
    /// Sets one key in one section of an .ini, with the editor's rules: a replacing key overwrites
    /// the first line with that key, an adding one (the "+Key" arrays) goes in beside it, and a
    /// missing section is appended.
    /// </summary>
    private static void SetIni(string path, string section, string key, string value, bool replace)
    {
        var (text, encoding) = File.Exists(path) ? ReadText(path) : ("", new UTF8Encoding(false));
        var lines = text.Length == 0 ? [] : text.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
        var output = new List<string>();
        var current = "";
        var done = false;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (!done && trimmed.StartsWith('['))
            {
                if (current.Equals(section, StringComparison.OrdinalIgnoreCase))
                {
                    output.Add($"{key}={value}");
                    output.Add("");
                    done = true;
                }
                current = trimmed.Trim('[', ']');
            }
            else if (!done && current.Equals(section, StringComparison.OrdinalIgnoreCase) &&
                     trimmed.Split('=', 2) is [var existing, _] &&
                     existing.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                output.Add($"{key}={value}");
                done = true;
                if (replace) continue;
            }
            output.Add(line);
        }

        if (!done)
        {
            if (!current.Equals(section, StringComparison.OrdinalIgnoreCase))
            {
                if (output.Count > 0) output.Add("");
                output.Add($"[{section}]");
            }
            output.Add($"{key}={value}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join("\r\n", output) + "\r\n", encoding);
    }

    /// <summary>The template's .uproject with the project's module names and this engine's association.</summary>
    private static void WriteProjectFile(string templateFile, string projectFile, string templateName, string name, string engineRoot)
    {
        var project = JsonNode.Parse(File.ReadAllText(templateFile), documentOptions: new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        })!.AsObject();

        project["EngineAssociation"] = EngineAssociation(engineRoot);
        project.Remove("EpicSampleNameHash");

        if (project["Modules"] is JsonArray modules)
            foreach (var module in modules.OfType<JsonObject>())
                if (module["Name"]?.GetValue<string>() is { } moduleName)
                    module["Name"] = moduleName.Replace(templateName, name, StringComparison.OrdinalIgnoreCase);

        File.WriteAllText(projectFile, project.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            IndentCharacter = '\t',
            IndentSize = 1,
        }));
    }

    /// <summary>
    /// How the .uproject names its engine, as UnrealVersionSelector reads it: a launcher engine by
    /// its "5.8" version, a source build by the {GUID} it is registered under (Setup.bat registers
    /// it). An unregistered source build falls back to its version.
    /// </summary>
    private static string EngineAssociation(string engineRoot)
    {
        if (EngineService.IsSourceBuild(engineRoot))
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Epic Games\Unreal Engine\Builds");
                foreach (var id in key?.GetValueNames() ?? [])
                    if (key!.GetValue(id) is string root &&
                        string.Equals(EngineInstall.Normalize(root), EngineInstall.Normalize(engineRoot),
                            StringComparison.OrdinalIgnoreCase))
                        return id;
            }
            catch
            {
                // Unreadable registry — fall back to the version.
            }
        }

        var version = EngineService.ReadVersion(engineRoot) ?? "";
        return string.Join('.', version.Split('.').Take(2));
    }
}
