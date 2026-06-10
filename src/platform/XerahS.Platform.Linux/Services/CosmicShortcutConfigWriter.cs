#region License Information (GPL v3)

/*
    XerahS - The Avalonia UI implementation of ShareX
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using XerahS.Common;

namespace XerahS.Platform.Linux.Services;

/// <summary>
/// A single COSMIC keyboard-shortcut binding (the map key in the cosmic-config "custom" RON map).
/// Identity is (modifiers set, key) only — matching cosmic's Binding equality; the optional
/// description is used to tag XerahS-owned entries and does not affect identity.
/// See XIP0079 and cosmic-settings-daemon config/src/shortcuts/binding.rs.
/// </summary>
internal sealed class CosmicBinding
{
    private static readonly string[] CanonicalOrder = { "Super", "Ctrl", "Alt", "Shift" };

    public IReadOnlyList<string> Modifiers { get; }
    public string Key { get; }
    public string? Description { get; }

    public CosmicBinding(IEnumerable<string> modifiers, string key, string? description = null)
    {
        Modifiers = (modifiers ?? Enumerable.Empty<string>())
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m.Trim())
            .ToList();
        Key = key ?? string.Empty;
        Description = description;
    }

    public bool IsXerahsOwned =>
        string.Equals(Description, CosmicShortcutConfigWriter.ManagedDescription, StringComparison.Ordinal);

    /// <summary>Modifier tokens in cosmic's canonical serialization order (Super, Ctrl, Alt, Shift).</summary>
    public IReadOnlyList<string> OrderedModifiers()
    {
        var set = new HashSet<string>(Modifiers, StringComparer.Ordinal);
        var ordered = CanonicalOrder.Where(set.Contains).ToList();
        // Preserve any unrecognized modifier tokens (forward-compat) after the known ones.
        ordered.AddRange(Modifiers.Where(m => !CanonicalOrder.Contains(m)).Distinct());
        return ordered;
    }

    /// <summary>True when both bindings target the same (modifiers, key) — ignoring description.</summary>
    public bool SameBindingAs(CosmicBinding other)
    {
        if (other is null) return false;
        var a = new HashSet<string>(Modifiers, StringComparer.Ordinal);
        var b = new HashSet<string>(other.Modifiers, StringComparer.Ordinal);
        return a.SetEquals(b) && string.Equals(Key, other.Key, StringComparison.Ordinal);
    }
}

/// <summary>One entry of the cosmic "custom" map: the raw RON key/value text plus the parsed binding.</summary>
internal sealed class CosmicShortcutEntry
{
    public string KeyText { get; }
    public string ActionText { get; }
    public CosmicBinding Binding { get; }

    public CosmicShortcutEntry(string keyText, string actionText, CosmicBinding binding)
    {
        KeyText = keyText;
        ActionText = actionText;
        Binding = binding;
    }
}

/// <summary>
/// Minimal, structure-aware reader/writer for the COSMIC custom-shortcut RON map
/// (<c>{ (modifiers: [..], key: "..."): &lt;Action&gt;, }</c>). Foreign entries are preserved
/// verbatim (key/value text kept raw); only the binding key of each entry is parsed so XerahS can
/// match, dedupe, and tag its own entries. Quote/escape/bracket aware so commas inside strings or
/// nested actions never corrupt the merge. See XIP0079.
/// </summary>
internal static class CosmicShortcutsRon
{
    public static List<CosmicShortcutEntry> Parse(string? content)
    {
        var result = new List<CosmicShortcutEntry>();
        if (string.IsNullOrWhiteSpace(content)) return result;

        string body = ExtractMapBody(StripComments(content!));
        int i = 0, n = body.Length;
        while (i < n)
        {
            while (i < n && (char.IsWhiteSpace(body[i]) || body[i] == ',')) i++;
            if (i >= n) break;

            if (body[i] != '(')
            {
                throw new FormatException($"COSMIC shortcuts RON: expected '(' starting a binding key at offset {i}.");
            }

            int keyStart = i;
            i = SkipBalancedParen(body, i);
            string keyText = body.Substring(keyStart, i - keyStart).Trim();

            while (i < n && char.IsWhiteSpace(body[i])) i++;
            if (i >= n || body[i] != ':')
            {
                throw new FormatException($"COSMIC shortcuts RON: expected ':' after binding key at offset {i}.");
            }
            i++; // consume ':'

            int valStart = i;
            i = SkipValue(body, i);
            string valText = body.Substring(valStart, i - valStart).Trim();

            result.Add(new CosmicShortcutEntry(keyText, valText, ParseBinding(keyText)));
        }

        return result;
    }

    public static string Serialize(IEnumerable<CosmicShortcutEntry> entries)
    {
        var list = entries.ToList();
        if (list.Count == 0) return "{}\n";

        var sb = new StringBuilder();
        sb.Append("{\n");
        foreach (var e in list)
        {
            sb.Append("    ").Append(e.KeyText).Append(": ").Append(e.ActionText).Append(",\n");
        }
        sb.Append("}\n");
        return sb.ToString();
    }

    internal static string EmitBindingText(CosmicBinding binding)
    {
        var sb = new StringBuilder();
        sb.Append("(modifiers: [")
          .Append(string.Join(", ", binding.OrderedModifiers()))
          .Append("], key: \"")
          .Append(EscapeForString(binding.Key))
          .Append('"');
        if (!string.IsNullOrEmpty(binding.Description))
        {
            // cosmic's Binding.description is Option<String> (no implicit_some), so it must be
            // serialized as Some("..."); a bare "..." makes cosmic reject the whole file. See XIP0079.
            sb.Append(", description: Some(\"").Append(EscapeForString(binding.Description!)).Append("\")");
        }
        sb.Append(')');
        return sb.ToString();
    }

    internal static string EscapeForString(string value) =>
        (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string ExtractMapBody(string content)
    {
        int open = content.IndexOf('{');
        int close = content.LastIndexOf('}');
        return (open < 0 || close <= open) ? string.Empty : content.Substring(open + 1, close - open - 1);
    }

    /// <summary>
    /// Remove RON line (<c>//</c>) and block (<c>/* */</c>) comments while preserving string literals,
    /// so a brace inside a comment cannot corrupt map extraction or silently drop foreign entries.
    /// See XIP0079.
    /// </summary>
    internal static string StripComments(string content)
    {
        var sb = new StringBuilder(content.Length);
        bool inString = false;
        for (int i = 0; i < content.Length; i++)
        {
            char c = content[i];
            if (inString)
            {
                sb.Append(c);
                if (c == '\\' && i + 1 < content.Length) { sb.Append(content[i + 1]); i++; }
                else if (c == '"') { inString = false; }
                continue;
            }

            if (c == '"') { inString = true; sb.Append(c); continue; }

            if (c == '/' && i + 1 < content.Length && content[i + 1] == '/')
            {
                i += 2;
                while (i < content.Length && content[i] != '\n') i++;
                if (i < content.Length) sb.Append('\n'); // keep the line break
                continue;
            }

            if (c == '/' && i + 1 < content.Length && content[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < content.Length && !(content[i] == '*' && content[i + 1] == '/')) i++;
                i++; // skip the closing '*'; the loop's i++ skips the '/'
                continue;
            }

            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Given s[start]=='(', return the index just past the matching ')'.</summary>
    private static int SkipBalancedParen(string s, int start)
    {
        int depth = 0;
        bool inString = false;
        for (int i = start; i < s.Length; i++)
        {
            char c = s[i];
            if (inString)
            {
                if (c == '\\') { i++; continue; }
                if (c == '"') inString = false;
                continue;
            }

            if (c == '"') inString = true;
            else if (c == '(' || c == '[') depth++;
            else if (c == ']') depth--;
            else if (c == ')')
            {
                depth--;
                if (depth == 0) return i + 1;
            }
        }
        throw new FormatException("COSMIC shortcuts RON: unbalanced '(' in binding key.");
    }

    /// <summary>Read a value starting at <paramref name="start"/> up to the next top-level comma (or end).</summary>
    private static int SkipValue(string s, int start)
    {
        int depth = 0;
        bool inString = false;
        int i = start;
        for (; i < s.Length; i++)
        {
            char c = s[i];
            if (inString)
            {
                if (c == '\\') { i++; continue; }
                if (c == '"') inString = false;
                continue;
            }

            if (c == '"') inString = true;
            else if (c == '(' || c == '[' || c == '{') depth++;
            else if (c == ')' || c == ']' || c == '}') depth--;
            else if (c == ',' && depth == 0) return i;
        }
        return i;
    }

    private static CosmicBinding ParseBinding(string keyText)
    {
        var fields = ParseStructFields(keyText);
        var modifiers = fields.TryGetValue("modifiers", out var modsRaw) ? ParseList(modsRaw) : new List<string>();
        string key = fields.TryGetValue("key", out var keyRaw) ? ParseRonString(keyRaw) : string.Empty;
        string? description = fields.TryGetValue("description", out var descRaw) ? ParseOptionalRonString(descRaw) : null;
        return new CosmicBinding(modifiers, key, description);
    }

    /// <summary>
    /// Parse a RON <c>Option&lt;String&gt;</c> field value: unwraps <c>Some("...")</c> (cosmic's
    /// serialized form for description) and treats <c>None</c> as absent. Also accepts a bare
    /// <c>"..."</c> for backward compatibility with files written before the Some(...) fix. See
    /// cosmic-settings-daemon shortcuts/binding.rs — description is Option&lt;String&gt;, no implicit_some.
    /// </summary>
    private static string? ParseOptionalRonString(string raw)
    {
        raw = raw.Trim();
        if (raw == "None") return null;
        if (raw.StartsWith("Some(", StringComparison.Ordinal) && raw.EndsWith(")", StringComparison.Ordinal))
        {
            raw = raw.Substring(5, raw.Length - 6).Trim();
        }
        return ParseRonString(raw);
    }

    /// <summary>Parse the top-level fields of a RON struct <c>(name: value, name: value)</c>.</summary>
    private static Dictionary<string, string> ParseStructFields(string structText)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        int open = structText.IndexOf('(');
        int close = structText.LastIndexOf(')');
        string body = (open >= 0 && close > open) ? structText.Substring(open + 1, close - open - 1) : structText;

        int i = 0, n = body.Length;
        while (i < n)
        {
            while (i < n && (char.IsWhiteSpace(body[i]) || body[i] == ',')) i++;
            if (i >= n) break;

            int nameStart = i;
            while (i < n && (char.IsLetterOrDigit(body[i]) || body[i] == '_')) i++;
            string name = body.Substring(nameStart, i - nameStart);

            while (i < n && char.IsWhiteSpace(body[i])) i++;
            if (i >= n || body[i] != ':') break; // malformed; stop tolerantly
            i++; // ':'

            int valStart = i;
            i = SkipValue(body, i);
            string val = body.Substring(valStart, i - valStart).Trim();

            if (name.Length > 0) fields[name] = val;
        }

        return fields;
    }

    private static List<string> ParseList(string raw)
    {
        raw = raw.Trim();
        int open = raw.IndexOf('[');
        int close = raw.LastIndexOf(']');
        if (open < 0 || close <= open) return new List<string>();
        return raw.Substring(open + 1, close - open - 1)
            .Split(',')
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .ToList();
    }

    private static string ParseRonString(string raw)
    {
        raw = raw.Trim();
        if (raw.Length < 1 || raw[0] != '"') return raw.Trim('"');
        var sb = new StringBuilder();
        for (int i = 1; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c == '\\')
            {
                if (i + 1 < raw.Length) { sb.Append(raw[i + 1]); i++; }
                continue;
            }
            if (c == '"') break;
            sb.Append(c);
        }
        return sb.ToString();
    }
}

/// <summary>
/// Writes XerahS-owned global-hotkey bindings into the COSMIC compositor's custom-shortcut config
/// (<c>$XDG_CONFIG_HOME/cosmic/com.system76.CosmicSettings.Shortcuts/v1/custom</c>) so cosmic-comp
/// dispatches them (it watches and live-reloads the file). Read-merge-write preserves every foreign
/// entry; XerahS entries are tagged with a <c>description</c> so they can be matched and removed.
/// See XIP0079.
/// </summary>
internal sealed class CosmicShortcutConfigWriter
{
    internal const string ManagedDescription = "Managed by XerahS";

    private readonly string _customFilePath;

    public CosmicShortcutConfigWriter(string? customFilePath = null)
    {
        _customFilePath = customFilePath ?? ResolveDefaultPath();
    }

    public static string ResolveDefaultPath()
    {
        string? baseDir = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(baseDir))
        {
            baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }
        return Path.Combine(baseDir, "cosmic", "com.system76.CosmicSettings.Shortcuts", "v1", "custom");
    }

    /// <summary>How claiming a combo affects whatever is currently bound to it.</summary>
    internal enum ComboReplacementKind
    {
        /// <summary>Nothing on this combo, or an identical XerahS entry (a harmless no-op rewrite).</summary>
        None,

        /// <summary>A user's own (non-XerahS) binding is being destroyed — unrecoverable on unregister.</summary>
        ForeignReplaced,

        /// <summary>The combo was bound to a different XerahS action and is being reassigned.</summary>
        XerahsReassigned,
    }

    /// <summary>
    /// Classifies what claiming <paramref name="binding"/> with <paramref name="newActionText"/> does to the
    /// existing <paramref name="entries"/> on the same combo. cosmic's custom map holds one action per
    /// (modifiers,key), so taking a combo necessarily replaces whatever is on it. Pure/ testable.
    /// </summary>
    internal static ComboReplacementKind ClassifyComboReplacement(
        IReadOnlyList<CosmicShortcutEntry> entries, CosmicBinding binding, string newActionText)
    {
        bool foreignReplaced = false;
        bool xerahsReassigned = false;

        foreach (var e in entries)
        {
            if (!e.Binding.SameBindingAs(binding))
            {
                continue;
            }

            if (!e.Binding.IsXerahsOwned)
            {
                foreignReplaced = true;
            }
            else if (!string.Equals(e.ActionText, newActionText, StringComparison.Ordinal))
            {
                xerahsReassigned = true;
            }
        }

        // Destroying a foreign binding is the more serious (unrecoverable) case, so report it first.
        if (foreignReplaced)
        {
            return ComboReplacementKind.ForeignReplaced;
        }

        return xerahsReassigned ? ComboReplacementKind.XerahsReassigned : ComboReplacementKind.None;
    }

    /// <summary>Insert or replace the XerahS-owned binding -&gt; <c>Spawn(spawnCommand)</c>, preserving all foreign entries.</summary>
    public void Upsert(CosmicBinding binding, string spawnCommand)
    {
        var entries = ReadEntries();

        var owned = new CosmicBinding(binding.Modifiers, binding.Key, ManagedDescription);
        string keyText = CosmicShortcutsRon.EmitBindingText(owned);
        string actionText = $"Spawn(\"{CosmicShortcutsRon.EscapeForString(spawnCommand)}\")";

        // cosmic's custom map holds one action per (modifiers,key), so claiming this combo replaces
        // whatever is bound to it — surface destructive replacements instead of doing them silently.
        // Remove()/UnregisterAll() will not restore the prior binding later. See XIP0079.
        string combo = string.Join("+", binding.OrderedModifiers().Append(binding.Key));
        switch (ClassifyComboReplacement(entries, binding, actionText))
        {
            case ComboReplacementKind.ForeignReplaced:
                DebugHelper.WriteLine($"CosmicShortcutConfigWriter: replacing a non-XerahS custom shortcut on {combo}; it will not be restored on unregister.");
                break;
            case ComboReplacementKind.XerahsReassigned:
                DebugHelper.WriteLine($"CosmicShortcutConfigWriter: reassigning {combo} from a previous XerahS shortcut to '{spawnCommand}'.");
                break;
        }

        entries.RemoveAll(e => e.Binding.SameBindingAs(binding));
        entries.Add(new CosmicShortcutEntry(keyText, actionText, owned));

        Write(entries);
    }

    /// <summary>Remove the XerahS-owned entry for this binding (foreign entries with the same binding are left intact).</summary>
    public void Remove(CosmicBinding binding)
    {
        var entries = ReadEntries();
        int removed = entries.RemoveAll(e => e.Binding.IsXerahsOwned && e.Binding.SameBindingAs(binding));
        if (removed > 0) Write(entries);
    }

    /// <summary>Remove every XerahS-owned entry, leaving the user's own custom shortcuts untouched.</summary>
    public void RemoveAllXerahsOwned()
    {
        var entries = ReadEntries();
        int removed = entries.RemoveAll(e => e.Binding.IsXerahsOwned);
        if (removed > 0) Write(entries);
    }

    public bool ContainsXerahsBinding(CosmicBinding binding) =>
        ReadEntries().Any(e => e.Binding.IsXerahsOwned && e.Binding.SameBindingAs(binding));

    private List<CosmicShortcutEntry> ReadEntries()
    {
        if (!File.Exists(_customFilePath)) return new List<CosmicShortcutEntry>();
        return CosmicShortcutsRon.Parse(File.ReadAllText(_customFilePath));
    }

    private void Write(List<CosmicShortcutEntry> entries)
    {
        string dir = Path.GetDirectoryName(_customFilePath)!;
        Directory.CreateDirectory(dir);

        string content = CosmicShortcutsRon.Serialize(entries);
        // Atomic replace: cosmic-comp watches and live-reloads this file, so never leave it half-written.
        // Flush the temp file's bytes to disk before the rename so a crash can't leave cosmic-comp
        // reloading a renamed-but-empty file. The rename itself is atomic (same directory/filesystem).
        string tmp = _customFilePath + ".xerahs-tmp-" + Guid.NewGuid().ToString("N");
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, _customFilePath, overwrite: true);
    }
}
