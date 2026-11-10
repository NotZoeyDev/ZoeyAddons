using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace UnusedAssetFinder
{
    public enum UnusedAssetKind { Material, Texture }

    public sealed class UnusedAsset
    {
        public string Guid;
        public string Path;
        public UnusedAssetKind Kind;
        public string TypeName;
        public long SizeBytes;
    }

    public sealed class ScanOptions
    {
        /// <summary>Scenes under Assets/ listed here (by GUID) do not count as using anything.</summary>
        public ICollection<string> ExcludedSceneGuids = new List<string>();
        /// <summary>Every prefab under Assets/ counts as used, placed in a scene or not.</summary>
        public bool PrefabsAreRoots;
        /// <summary>Every ScriptableObject .asset under Assets/ (menus, presets, tool settings) counts as used.</summary>
        public bool ScriptableAssetsAreRoots = true;
        /// <summary>Asset path prefixes (Assets/...) that are never listed.</summary>
        public IList<string> ExtraProtectedPaths = new List<string>();
        /// <summary>Unused assets with these GUIDs are reported in <see cref="ScanResult.Kept"/> instead of <see cref="ScanResult.Unused"/>.</summary>
        public ICollection<string> KeptGuids = new List<string>();
        public Action<string, float> Progress;
    }

    public sealed class ScanResult
    {
        public readonly List<UnusedAsset> Unused = new List<UnusedAsset>();
        public readonly List<UnusedAsset> Kept = new List<UnusedAsset>();
        public readonly List<string> RootScenes = new List<string>();
        public readonly Dictionary<string, int> ProtectedByReason = new Dictionary<string, int>();
        public int CandidateCount;
        public int UsedCount;
        public int ProtectedCount => ProtectedByReason.Values.Sum();
    }

    /// <summary>
    /// Finds materials and textures under Assets/ that nothing counted as used depends on.
    /// "Used" is the recursive AssetDatabase dependency closure of the included scenes plus the
    /// optional prefab / ScriptableObject roots, every protected asset, and assets scripts name.
    /// </summary>
    public static class UnusedAssetScanner
    {
        public const string ScriptReferenceReason = "Named by a script in Assets/ (path or GUID literal)";
        public const string ShaderDefaultReason = "Default texture of a shader";

        private static readonly Regex PathLiteral = new Regex("\"(Assets/[^\"\\\\\\r\\n]+)\"", RegexOptions.Compiled);
        private static readonly Regex GuidLiteral = new Regex("\"([0-9a-fA-F]{32})\"", RegexOptions.Compiled);

        public static ScanResult Scan(ScanOptions options)
        {
            Action<string, float> progress = options.Progress ?? ((_, __) => { });
            var result = new ScanResult();
            var rules = new ProtectionRules(options.ExtraProtectedPaths);
            var excludedScenes = new HashSet<string>(options.ExcludedSceneGuids ?? Array.Empty<string>());
            var kept = new HashSet<string>(options.KeptGuids ?? Array.Empty<string>());

            progress("Collecting materials and textures", 0.05f);
            Dictionary<string, UnusedAsset> candidates = CollectCandidates();
            result.CandidateCount = candidates.Count;

            progress("Reading scripts for asset references", 0.2f);
            CollectScriptReferences(out List<string> scriptPrefixes, out HashSet<string> scriptGuids);

            progress("Collecting roots", 0.4f);
            var roots = new HashSet<string>(StringComparer.Ordinal);
            var shaders = new List<string>();
            var protectedReason = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (string path in AssetDatabase.GetAllAssetPaths())
            {
                if (!path.StartsWith("Assets/", StringComparison.Ordinal)) continue;

                if (path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                {
                    if (!excludedScenes.Contains(AssetDatabase.AssetPathToGUID(path)))
                    {
                        roots.Add(path);
                        result.RootScenes.Add(path);
                    }
                    continue;
                }

                if (path.EndsWith(".shader", StringComparison.OrdinalIgnoreCase)) shaders.Add(path);

                candidates.TryGetValue(path, out UnusedAsset candidate);
                string reason = rules.Match(path);
                if (reason == null && IsNamedByScript(path, candidate, scriptPrefixes, scriptGuids))
                    reason = ScriptReferenceReason;
                if (reason != null)
                {
                    roots.Add(path);
                    if (candidate != null) protectedReason[path] = reason;
                    continue;
                }

                if (candidate != null) continue;

                if (options.PrefabsAreRoots && path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                    roots.Add(path);
                else if (options.ScriptableAssetsAreRoots && path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)
                         && typeof(ScriptableObject).IsAssignableFrom(AssetDatabase.GetMainAssetTypeAtPath(path)))
                    roots.Add(path);
            }

            // GUID literals can point anywhere (often a prefab whose materials must stay).
            foreach (string guid in scriptGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path) && !path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) roots.Add(path);
            }

            progress("Resolving shader default textures", 0.5f);
            var shaderDefaults = new HashSet<string>(AssetDatabase.GetDependencies(shaders.ToArray(), false), StringComparer.Ordinal);

            progress("Resolving dependencies of scenes and roots", 0.6f);
            var used = new HashSet<string>(AssetDatabase.GetDependencies(roots.ToArray(), true), StringComparer.Ordinal);

            progress("Classifying", 0.9f);
            foreach (KeyValuePair<string, UnusedAsset> pair in candidates)
            {
                string path = pair.Key;
                if (!protectedReason.TryGetValue(path, out string reason) && shaderDefaults.Contains(path))
                    reason = ShaderDefaultReason;

                if (reason != null)
                {
                    result.ProtectedByReason.TryGetValue(reason, out int count);
                    result.ProtectedByReason[reason] = count + 1;
                }
                else if (used.Contains(path))
                {
                    result.UsedCount++;
                }
                else
                {
                    UnusedAsset asset = pair.Value;
                    asset.SizeBytes = File.Exists(path) ? new FileInfo(path).Length : 0;
                    (kept.Contains(asset.Guid) ? result.Kept : result.Unused).Add(asset);
                }
            }

            result.RootScenes.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        /// <summary>Every scene under Assets/, as (GUID, path) sorted by path.</summary>
        public static List<KeyValuePair<string, string>> FindScenes()
        {
            return AssetDatabase.FindAssets("t:Scene", new[] { "Assets" })
                .Select(guid => new KeyValuePair<string, string>(guid, AssetDatabase.GUIDToAssetPath(guid)))
                .Where(pair => pair.Value.StartsWith("Assets/", StringComparison.Ordinal))
                .OrderBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static Dictionary<string, UnusedAsset> CollectCandidates()
        {
            var candidates = new Dictionary<string, UnusedAsset>(StringComparer.Ordinal);
            foreach (string guid in AssetDatabase.FindAssets("t:Material", new[] { "Assets" })
                         .Concat(AssetDatabase.FindAssets("t:Texture", new[] { "Assets" })))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (candidates.ContainsKey(path) || !path.StartsWith("Assets/", StringComparison.Ordinal)) continue;

                // Main type only: FBX and font assets that merely contain a material/texture are not candidates.
                Type type = AssetDatabase.GetMainAssetTypeAtPath(path);
                UnusedAssetKind kind;
                if (type != null && typeof(Material).IsAssignableFrom(type)) kind = UnusedAssetKind.Material;
                else if (type != null && typeof(Texture).IsAssignableFrom(type)) kind = UnusedAssetKind.Texture;
                else continue;

                candidates.Add(path, new UnusedAsset { Guid = guid, Path = path, Kind = kind, TypeName = type.Name });
            }
            return candidates;
        }

        // Tools in Assets/ load their own icons and materials by "Assets/..." path or GUID literal,
        // which no dependency graph records. A path literal only counts when it points inside the
        // script's own top-level folder: literals reaching elsewhere are save locations (Thry's
        // "Assets/textures" output folder), and honoring them would shield unrelated user folders.
        private static void CollectScriptReferences(out List<string> prefixes, out HashSet<string> guids)
        {
            var prefixSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            guids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string file in Directory.EnumerateFiles("Assets", "*.cs", SearchOption.AllDirectories))
            {
                string text;
                try { text = File.ReadAllText(file); }
                catch (IOException) { continue; }

                string[] segments = file.Replace('\\', '/').Split('/');
                string ownFolder = segments.Length > 2 ? "Assets/" + segments[1] : null;

                foreach (Match match in PathLiteral.Matches(text))
                {
                    string literal = match.Groups[1].Value;
                    // "Assets/" alone (a prefix check in code) would protect everything.
                    if (literal.TrimEnd('/').IndexOf('/') < 0) continue;
                    if (ownFolder != null && !IsWithin(literal, ownFolder)) continue;
                    prefixSet.Add(literal);
                }

                foreach (Match match in GuidLiteral.Matches(text))
                    guids.Add(match.Groups[1].Value.ToLowerInvariant());
            }

            prefixes = prefixSet.ToList();
        }

        private static bool IsWithin(string path, string folder)
        {
            return path.Equals(folder, StringComparison.OrdinalIgnoreCase)
                   || path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);
        }

        // Non-candidate GUID literals are already roots, so only a candidate's own GUID needs checking here.
        private static bool IsNamedByScript(string path, UnusedAsset candidate, List<string> prefixes, HashSet<string> guids)
        {
            foreach (string prefix in prefixes)
            {
                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return candidate != null && guids.Contains(candidate.Guid);
        }
    }
}
