#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Savepoint.Playnite
{
    /// <summary>Suggests which Savepoint game a Playnite game is, by exe name first, then by name.</summary>
    public static class LinkMatcher
    {
        public static AgentGame Suggest(string playniteName, IEnumerable<string> exePaths, IReadOnlyList<AgentGame> games)
        {
            if (games == null || games.Count == 0) return null;

            var exes = new HashSet<string>((exePaths ?? Enumerable.Empty<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(NormalizeExe), StringComparer.Ordinal);
            if (exes.Count > 0)
            {
                var byExe = games.Where(g => (g.ExeNames ?? new List<string>()).Any(e => exes.Contains(NormalizeExe(e)))).ToList();
                if (byExe.Count == 1) return byExe[0];
            }

            var name = NormalizeName(playniteName);
            if (name.Length == 0) return null;
            var exact = games.Where(g => NormalizeName(g.Name) == name).ToList();
            if (exact.Count == 1) return exact[0];

            // "Elden Ring" vs "ELDEN RING™ Deluxe Edition": one contains the other. Only if unambiguous.
            var partial = games.Where(g =>
            {
                var n = NormalizeName(g.Name);
                return n.Length >= 4 && (n.Contains(name) || name.Contains(n));
            }).ToList();
            return partial.Count == 1 ? partial[0] : null;
        }

        /// <summary>"{InstallDir}\bin\eldenring.exe" → "eldenring".</summary>
        public static string NormalizeExe(string path)
        {
            var name = path.Trim().Trim('"').Replace('/', '\\');
            var slash = name.LastIndexOf('\\');
            if (slash >= 0) name = name.Substring(slash + 1);
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
            return name.ToLowerInvariant();
        }

        /// <summary>Lowercase letters and digits only: "ELDEN RING™: Nightreign" → "eldenringnightreign".</summary>
        public static string NormalizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var sb = new StringBuilder(name.Length);
            // Drop trademark symbols first: compatibility normalization would turn ™ into "TM".
            name = name.Replace("\u2122", "").Replace("\u00AE", "").Replace("\u00A9", "");
            foreach (var c in name.Normalize(NormalizationForm.FormKD))
            {
                if (char.IsLetterOrDigit(c) && c < 128) sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }
    }
}
