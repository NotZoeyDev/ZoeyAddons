using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace UnusedAssetFinder
{
    /// <summary>Per-project, per-user choices; lives in UserSettings/ so it never travels with the project's assets.</summary>
    [FilePath("UserSettings/UnusedAssetFinder.asset", FilePathAttribute.Location.ProjectFolder)]
    internal sealed class UnusedAssetFinderSettings : ScriptableSingleton<UnusedAssetFinderSettings>
    {
        public List<string> KeptGuids = new List<string>();
        public List<string> ExcludedSceneGuids = new List<string>();
        public bool PrefabsAreRoots;
        public bool ScriptableAssetsAreRoots = true;
        public string ExtraProtectedPaths = "";
        public bool DeleteEmptiedFolders = true;

        public List<string> ExtraProtectedPathList =>
            ExtraProtectedPaths.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToList();

        public void Persist() => Save(true);
    }
}
