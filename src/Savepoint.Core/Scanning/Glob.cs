using System.Text;
using System.Text.RegularExpressions;

namespace Savepoint.Core.Scanning;

/// <summary>
/// Exclude patterns, matched against forward-slash paths relative to a save root
/// (and against the "alias/relative" manifest path, so either style works).
/// <c>*</c> matches within one segment, <c>**</c> across segments, <c>?</c> one character.
/// A pattern with no slash matches the file name in any folder (like .gitignore).
/// Matching is case-insensitive, as Windows paths are.
/// </summary>
public sealed class ExcludeMatcher
{
    private readonly List<Regex> _patterns;

    public ExcludeMatcher(IEnumerable<string> patterns)
    {
        _patterns = patterns
            .Select(p => p.Trim().Replace('\\', '/'))
            .Where(p => p.Length > 0 && !p.StartsWith('#'))
            .Select(Compile)
            .ToList();
    }

    public static readonly ExcludeMatcher None = new(Array.Empty<string>());

    public bool IsExcluded(string alias, string relativePath)
    {
        if (_patterns.Count == 0) return false;
        var withAlias = alias + "/" + relativePath;
        foreach (var re in _patterns)
        {
            if (re.IsMatch(relativePath) || re.IsMatch(withAlias)) return true;
        }
        return false;
    }

    internal static Regex Compile(string pattern)
    {
        pattern = pattern.TrimStart('/');
        // "foo/" means the folder foo and everything in it.
        if (pattern.EndsWith('/')) pattern += "**";
        // No slash: match the name at any depth.
        if (!pattern.Contains('/')) pattern = "**/" + pattern;

        var sb = new StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '*')
            {
                var isDouble = i + 1 < pattern.Length && pattern[i + 1] == '*';
                if (isDouble)
                {
                    i++;
                    var followedBySlash = i + 1 < pattern.Length && pattern[i + 1] == '/';
                    if (followedBySlash)
                    {
                        i++;
                        sb.Append("(?:.*/)?"); // zero or more whole segments
                    }
                    else
                    {
                        sb.Append(".*");
                    }
                }
                else
                {
                    sb.Append("[^/]*");
                }
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }
}
