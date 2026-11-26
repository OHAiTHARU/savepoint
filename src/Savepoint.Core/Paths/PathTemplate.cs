using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Savepoint.Core.Paths;

/// <summary>Resolves the variables a path template may use (DESIGN §2).</summary>
public interface IPathEnvironment
{
    /// <summary>Returns the folder for a variable name (without %), or null if unknown/unavailable.</summary>
    string? Resolve(string variable);
}

public static partial class PathTemplate
{
    public static readonly IReadOnlyList<string> Variables = new[]
    {
        "APPDATA", "LOCALAPPDATA", "LOCALLOW", "USERPROFILE", "DOCUMENTS", "SAVEDGAMES", "PUBLIC",
    };

    [GeneratedRegex(@"%([A-Za-z_]+)%")]
    private static partial Regex VariablePattern();

    /// <summary>Expand a template to an absolute path. Throws <see cref="PathTemplateException"/>
    /// for unknown variables or a result that isn't absolute.</summary>
    public static string Resolve(string template, IPathEnvironment env)
    {
        if (string.IsNullOrWhiteSpace(template))
            throw new PathTemplateException("Path template is empty");
        var result = VariablePattern().Replace(template.Trim(), m =>
        {
            var name = m.Groups[1].Value.ToUpperInvariant();
            var value = env.Resolve(name);
            if (string.IsNullOrEmpty(value))
                throw new PathTemplateException($"Unknown or unavailable variable %{name}% in \"{template}\"");
            return value.TrimEnd('\\', '/');
        });
        // Templates are authored with backslashes on Windows; normalise for the host OS.
        result = result.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        result = result.TrimEnd(Path.DirectorySeparatorChar);
        if (!Path.IsPathFullyQualified(result) && !(result.Length == 2 && result[1] == ':'))
            throw new PathTemplateException($"\"{template}\" does not resolve to an absolute path");
        return Path.GetFullPath(result.Length == 2 ? result + Path.DirectorySeparatorChar : result);
    }

    /// <summary>Best-effort reverse: turn an absolute path into a template using the most
    /// specific matching variable. Used by "Browse…" in the Add game dialog.</summary>
    public static string ToTemplate(string absolutePath, IPathEnvironment env)
    {
        var full = Path.GetFullPath(absolutePath).TrimEnd(Path.DirectorySeparatorChar);
        string? best = null;
        string? bestValue = null;
        foreach (var v in Variables)
        {
            var value = env.Resolve(v);
            if (string.IsNullOrEmpty(value)) continue;
            value = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar);
            var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var isPrefix = full.Equals(value, cmp) ||
                           full.StartsWith(value + Path.DirectorySeparatorChar, cmp);
            if (isPrefix && (bestValue is null || value.Length > bestValue.Length))
            {
                best = v;
                bestValue = value;
            }
        }
        if (best is null) return full.Replace('/', '\\');
        var rest = full[bestValue!.Length..].TrimStart(Path.DirectorySeparatorChar);
        var template = $"%{best}%" + (rest.Length > 0 ? "\\" + rest : "");
        return template.Replace('/', '\\');
    }
}

public sealed class PathTemplateException : Exception
{
    public PathTemplateException(string message) : base(message) { }
}

/// <summary>Resolves variables for the current Windows user, using the known-folder API
/// so OneDrive-redirected Documents and moved Saved Games are found correctly.</summary>
public sealed class SystemPathEnvironment : IPathEnvironment
{
    private static readonly Guid SavedGames = new("4C5C32FF-BB9D-43b0-B5B4-2D72E54EAAA4");
    private static readonly Guid Documents = new("FDD39AD0-238F-46AF-ADB4-6C85480369C7");
    private static readonly Guid LocalLow = new("A520A1A4-1780-4FF6-BD18-167343C5AF16");
    private static readonly Guid Public = new("DFDF76A2-C82A-4D63-906A-5644AC457385");

    public string? Resolve(string variable)
    {
        switch (variable.ToUpperInvariant())
        {
            case "APPDATA": return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            case "LOCALAPPDATA": return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            case "USERPROFILE": return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            case "DOCUMENTS":
                return KnownFolder(Documents) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            case "SAVEDGAMES":
                return KnownFolder(SavedGames) ?? Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games");
            case "LOCALLOW":
                return KnownFolder(LocalLow) ?? Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow");
            case "PUBLIC":
                return KnownFolder(Public) ?? Environment.GetEnvironmentVariable("PUBLIC") ?? (OperatingSystem.IsWindows() ? @"C:\Users\Public" : null);
            default:
                return null;
        }
    }

    private static string? Combine(string root, params string[] parts) =>
        string.IsNullOrEmpty(root) ? null : Path.Combine(new[] { root }.Concat(parts).ToArray());

    private static string? KnownFolder(Guid id)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var hr = SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var ptr);
            if (hr != 0) return null;
            try { return Marshal.PtrToStringUni(ptr); }
            finally { Marshal.FreeCoTaskMem(ptr); }
        }
        catch (Exception) { return null; }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);
}

/// <summary>Fixed mapping, for tests and for previewing templates.</summary>
public sealed class DictionaryPathEnvironment : IPathEnvironment
{
    private readonly Dictionary<string, string> _values;
    public DictionaryPathEnvironment(IDictionary<string, string> values) =>
        _values = new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
    public string? Resolve(string variable) => _values.TryGetValue(variable, out var v) ? v : null;
}
