using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace UnusedAssetFinder
{
    /// <summary>
    /// Path rules for assets that are never offered for deletion, whatever the scenes reference.
    /// </summary>
    public sealed class ProtectionRules
    {
        public const string ExtraPathsReason = "Your protected paths";

        private readonly struct PackageRule
        {
            public readonly string Reason;
            public readonly string[] Patterns;

            public PackageRule(string reason, params string[] patterns)
            {
                Reason = reason;
                Patterns = patterns;
            }
        }

        // Shader and tool packages (plus the agent workspace), matched against the top-level folder under
        // Assets/ only, so a vendor outfit's own "Materials/lilToon" variants stay scannable. Names compare
        // lowercased with everything but letters and digits stripped; a trailing '*' accepts any suffix.
        private static readonly PackageRule[] TopLevelFolders =
        {
            new PackageRule("Poiyomi / Thry shaders", "poiyomi*", "lockedshadercache", "thry*"),
            new PackageRule("lilToon", "liltoon*", "lilxyzw*"),
            new PackageRule("UnlitWF", "unlitwf*"),
            new PackageRule("UnityChan Toon Shader", "unitychantoonshader*", "uts2*", "toon"),
            new PackageRule("Sunao Shader", "sunao*"),
            new PackageRule("ArktoonShaders", "arktoon*"),
            new PackageRule("Mochie shaders", "mochie*"),
            new PackageRule("Silent's shaders", "silentcelshading*", "silentscelshading*", "filamented*"),
            new PackageRule("orels1 shaders", "orels*"),
            new PackageRule("Xiexe shaders (XSToon / XSFur)", "xstoon*", "xiexe*", "xsfur*"),
            new PackageRule("RealToon", "realtoon*"),
            new PackageRule("VRM / MToon", "vrm", "vrmshaders*", "mtoon*", "unigltf*"),
            new PackageRule("NoeNoe Toon", "noenoe*"),
            new PackageRule("Fast Fur", "fastfur*", "warrensfastfur*"),
            new PackageRule("AudioLink", "audiolink*"),
            new PackageRule("LTCGI", "ltcgi*", "pi"),
            new PackageRule("TextMesh Pro", "textmeshpro*"),
            new PackageRule("VRChat SDK (Assets install)", "vrcsdk*"),
            new PackageRule("Agent workspace (Assets/Agent)", "agent"),
        };

        // Unity loads from these by convention (Resources.Load, EditorGUIUtility.Load, gizmo icons, plugins).
        private static readonly string[] SpecialFolders =
        {
            "Editor", "Editor Default Resources", "Gizmos", "Resources", "StreamingAssets", "Plugins",
        };

        private readonly List<string> extraPrefixes;

        public ProtectionRules(IEnumerable<string> extraProtectedPaths)
        {
            extraPrefixes = (extraProtectedPaths ?? Enumerable.Empty<string>())
                .Select(p => p.Trim().Replace('\\', '/'))
                .Where(p => p.Length > 0)
                .ToList();
        }

        /// <summary>The reason <paramref name="assetPath"/> is protected, or null when it is not.</summary>
        public string Match(string assetPath)
        {
            string[] segments = assetPath.Split('/');

            if (segments.Length > 2)
            {
                string top = Normalize(segments[1]);
                foreach (PackageRule rule in TopLevelFolders)
                {
                    foreach (string pattern in rule.Patterns)
                    {
                        bool prefix = pattern.EndsWith("*", StringComparison.Ordinal);
                        string stem = prefix ? pattern.Substring(0, pattern.Length - 1) : pattern;
                        if (prefix ? top.StartsWith(stem, StringComparison.Ordinal) : top == stem)
                            return rule.Reason;
                    }
                }
            }

            for (int i = 1; i < segments.Length - 1; i++)
            {
                foreach (string special in SpecialFolders)
                {
                    if (string.Equals(segments[i], special, StringComparison.OrdinalIgnoreCase))
                        return $"Unity special folder ({special})";
                }
            }

            foreach (string prefix in extraPrefixes)
            {
                if (assetPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return ExtraPathsReason;
            }

            return null;
        }

        /// <summary>One line per built-in rule, for display.</summary>
        public static IEnumerable<string> DescribeBuiltIn()
        {
            foreach (PackageRule rule in TopLevelFolders)
                yield return $"{rule.Reason}: Assets/ top-level folder matching {string.Join(", ", rule.Patterns)}";
            yield return $"Unity special folders anywhere: {string.Join(", ", SpecialFolders)}";
        }

        private static string Normalize(string folderName)
        {
            var sb = new StringBuilder(folderName.Length);
            foreach (char c in folderName)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }
    }
}
