using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace UnusedAssetFinder
{
    internal sealed class UnusedAssetFinderWindow : EditorWindow
    {
        private const int PreviewLines = 12;

        private ScanResult result;
        private readonly List<UnusedAsset> shown = new List<UnusedAsset>();
        private readonly HashSet<string> marked = new HashSet<string>();

        private MultiColumnListView list;
        private Label summaryLabel;
        private Label footerLabel;
        private ToolbarToggle materialsToggle;
        private ToolbarToggle texturesToggle;
        private ToolbarToggle showKeptToggle;
        private ToolbarSearchField searchField;
        private Foldout scenesFoldout;
        private VisualElement scenesContainer;
        private Foldout protectedFoldout;
        private Button keepButton;
        private TextField extraPathsField;
        // Folders excluded from the list since the last Scan; assets elsewhere that only they use stay listed until a rescan.
        private bool excludedSinceScan;

        private static UnusedAssetFinderSettings Settings => UnusedAssetFinderSettings.instance;

        [MenuItem("Tools/Unused Asset Finder")]
        private static void Open()
        {
            var window = GetWindow<UnusedAssetFinderWindow>();
            window.titleContent = new GUIContent("Unused Assets");
            window.minSize = new Vector2(640, 420);
        }

        private void CreateGUI()
        {
            VisualElement root = rootVisualElement;

            var toolbar = new Toolbar();
            toolbar.Add(new ToolbarButton(Scan) { text = "Scan", tooltip = "Find materials and textures under Assets/ that nothing counted as used depends on." });
            materialsToggle = new ToolbarToggle { text = "Materials", value = true };
            texturesToggle = new ToolbarToggle { text = "Textures", value = true };
            showKeptToggle = new ToolbarToggle { text = "Show kept", tooltip = "List the unused assets you marked Keep instead of the rest." };
            searchField = new ToolbarSearchField();
            searchField.style.flexGrow = 1;
            materialsToggle.RegisterValueChangedCallback(_ => ApplyFilter());
            texturesToggle.RegisterValueChangedCallback(_ => ApplyFilter());
            showKeptToggle.RegisterValueChangedCallback(_ => { marked.Clear(); ApplyFilter(); });
            searchField.RegisterValueChangedCallback(_ => ApplyFilter());
            toolbar.Add(materialsToggle);
            toolbar.Add(texturesToggle);
            toolbar.Add(showKeptToggle);
            toolbar.Add(searchField);
            // Only the settings scroll area and the list may shrink; everything else keeps its height,
            // otherwise flex squashes foldouts and the footer to a few pixels and their children overlap.
            toolbar.style.flexShrink = 0;
            root.Add(toolbar);

            summaryLabel = new Label("Press Scan to list unused materials and textures.");
            summaryLabel.style.whiteSpace = WhiteSpace.Normal;
            summaryLabel.style.flexShrink = 0;
            summaryLabel.style.marginLeft = summaryLabel.style.marginRight = 4;
            summaryLabel.style.marginTop = summaryLabel.style.marginBottom = 4;
            root.Add(summaryLabel);

            // Expanded settings scroll instead of pushing the list off the window.
            var settingsScroll = new ScrollView(ScrollViewMode.Vertical);
            settingsScroll.style.flexGrow = 0;
            settingsScroll.style.flexShrink = 1;
            settingsScroll.style.maxHeight = Length.Percent(45);
            settingsScroll.Add(BuildScopeFoldout());
            protectedFoldout = new Foldout { text = "Protected (never listed)", value = false };
            settingsScroll.Add(protectedFoldout);
            RebuildProtectedFoldout();
            root.Add(settingsScroll);

            list = BuildList();
            root.Add(list);
            root.Add(BuildFooter());

            ApplyFilter();
        }

        private VisualElement BuildScopeFoldout()
        {
            var scope = new Foldout { text = "What counts as used", value = false };

            scenesFoldout = new Foldout { value = true };
            var sceneButtons = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            sceneButtons.Add(new Button(() => SetAllScenesIncluded(true)) { text = "All scenes" });
            sceneButtons.Add(new Button(() => SetAllScenesIncluded(false)) { text = "No scenes" });
            scenesFoldout.Add(sceneButtons);
            scenesContainer = new VisualElement();
            scenesFoldout.Add(scenesContainer);
            scope.Add(scenesFoldout);
            RebuildSceneToggles();

            var prefabs = new Toggle("Prefabs not in any scene") { value = Settings.PrefabsAreRoots, tooltip = "Count every prefab under Assets/ as used, so materials of outfits you have not placed yet are kept." };
            prefabs.RegisterValueChangedCallback(evt => { Settings.PrefabsAreRoots = evt.newValue; Settings.Persist(); });
            scope.Add(prefabs);

            var scriptables = new Toggle("ScriptableObject assets") { value = Settings.ScriptableAssetsAreRoots, tooltip = "Count every ScriptableObject .asset under Assets/ as used: expression menus and their icons, presets, tool settings such as folder-icon dictionaries." };
            scriptables.RegisterValueChangedCallback(evt => { Settings.ScriptableAssetsAreRoots = evt.newValue; Settings.Persist(); });
            scope.Add(scriptables);

            extraPathsField = new TextField("Extra protected paths") { multiline = true, isDelayed = true, value = Settings.ExtraProtectedPaths, tooltip = "One Assets/... path prefix per line. Anything under it is never listed and counts as used. Right-click a folder in a listed path to add it here." };
            extraPathsField.style.minHeight = 48;
            extraPathsField.RegisterValueChangedCallback(evt => { Settings.ExtraProtectedPaths = evt.newValue; Settings.Persist(); });
            scope.Add(extraPathsField);

            scope.Add(new Label("Changes apply on the next Scan.") { style = { unityFontStyleAndWeight = FontStyle.Italic, marginLeft = 4 } });
            return scope;
        }

        private MultiColumnListView BuildList()
        {
            var columns = new Columns
            {
                new Column
                {
                    name = "mark", title = "", width = 24, resizable = false, sortable = false,
                    makeCell = () =>
                    {
                        var toggle = new Toggle();
                        toggle.RegisterValueChangedCallback(evt =>
                        {
                            if (!(toggle.userData is UnusedAsset asset)) return;
                            if (evt.newValue) marked.Add(asset.Guid); else marked.Remove(asset.Guid);
                            UpdateFooter();
                        });
                        return toggle;
                    },
                    bindCell = (element, index) =>
                    {
                        var toggle = (Toggle)element;
                        toggle.userData = shown[index];
                        toggle.SetValueWithoutNotify(marked.Contains(shown[index].Guid));
                    },
                },
                new Column
                {
                    name = "icon", title = "", width = 22, resizable = false, sortable = false,
                    makeCell = () => new Image { scaleMode = ScaleMode.ScaleToFit },
                    bindCell = (element, index) => ((Image)element).image = AssetDatabase.GetCachedIcon(shown[index].Path),
                },
                new Column
                {
                    name = "type", title = "Type", width = 90,
                    makeCell = MakeCellLabel,
                    bindCell = (element, index) => ((Label)element).text = shown[index].TypeName,
                },
                new Column
                {
                    name = "path", title = "Path", width = 420, stretchable = true,
                    makeCell = () => new VisualElement { style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, overflow = Overflow.Hidden } },
                    bindCell = BindPathCell,
                },
                new Column
                {
                    name = "size", title = "Size", width = 80,
                    makeCell = () =>
                    {
                        Label label = MakeCellLabel();
                        label.style.unityTextAlign = TextAnchor.MiddleRight;
                        return label;
                    },
                    bindCell = (element, index) => ((Label)element).text = EditorUtility.FormatBytes(shown[index].SizeBytes),
                },
            };

            var view = new MultiColumnListView(columns)
            {
                itemsSource = shown,
                fixedItemHeight = 20,
                selectionType = SelectionType.Multiple,
                sortingEnabled = true,
                showAlternatingRowBackgrounds = AlternatingRowBackground.ContentOnly,
            };
            view.style.flexGrow = 1;
            view.style.flexShrink = 1;
            view.style.minHeight = 160;
            view.columnSortingChanged += () => { SortShown(); list.RefreshItems(); };
            view.selectionChanged += items =>
            {
                UnusedAsset last = items.OfType<UnusedAsset>().LastOrDefault();
                if (last != null) EditorGUIUtility.PingObject(AssetDatabase.LoadMainAssetAtPath(last.Path));
            };
            view.itemsChosen += items =>
            {
                Selection.objects = items.OfType<UnusedAsset>()
                    .Select(asset => AssetDatabase.LoadMainAssetAtPath(asset.Path))
                    .Where(obj => obj != null)
                    .ToArray();
            };
            return view;
        }

        private VisualElement BuildFooter()
        {
            var footer = new VisualElement { style = { flexDirection = FlexDirection.Row, flexWrap = Wrap.Wrap, flexShrink = 0, alignItems = Align.Center, paddingTop = 2, paddingBottom = 2 } };
            footerLabel = new Label { style = { flexGrow = 1, marginLeft = 4 } };
            footer.Add(footerLabel);
            footer.Add(new Button(() => SetMarked(shown, true)) { text = "Mark all shown" });
            footer.Add(new Button(() => SetMarked(shown, false)) { text = "Unmark all shown" });
            footer.Add(new Button(() => SetMarked(list.selectedItems.OfType<UnusedAsset>(), true)) { text = "Mark highlighted", tooltip = "Mark the rows highlighted with click / Shift-click / Ctrl-click." });
            keepButton = new Button(ToggleKeepMarked);
            footer.Add(keepButton);
            var deleteFolders = new Toggle("Delete emptied folders") { value = Settings.DeleteEmptiedFolders, tooltip = "After deleting, also move folders to the trash that the deletion left with no files or subfolders at all." };
            deleteFolders.RegisterValueChangedCallback(evt => { Settings.DeleteEmptiedFolders = evt.newValue; Settings.Persist(); });
            footer.Add(deleteFolders);
            footer.Add(new Button(DeleteMarked) { text = "Delete marked…", tooltip = "Move the marked assets to the system trash." });
            return footer;
        }

        private static Label MakeCellLabel()
        {
            var label = new Label { style = { unityTextAlign = TextAnchor.MiddleLeft, overflow = Overflow.Hidden, textOverflow = TextOverflow.Ellipsis } };
            return label;
        }

        // One label per path segment (pooled per row), so a right-click lands on a specific folder.
        private void BindPathCell(VisualElement cell, int index)
        {
            UnusedAsset asset = shown[index];
            cell.userData = asset;
            cell.tooltip = asset.Path + "\nRight-click a folder name to exclude that folder.";

            string[] segments = asset.Path.Split('/');
            while (cell.childCount < segments.Length) cell.Add(MakePathSegment());
            for (int i = 0; i < cell.childCount; i++)
            {
                var label = (Label)cell[i];
                bool visible = i < segments.Length;
                label.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
                if (visible) label.text = i < segments.Length - 1 ? segments[i] + "/" : segments[i];
            }
        }

        private Label MakePathSegment()
        {
            var label = new Label { style = { unityTextAlign = TextAnchor.MiddleLeft, flexShrink = 0, paddingLeft = 0, paddingRight = 0, marginLeft = 0, marginRight = 0 } };
            label.RegisterCallback<PointerEnterEvent>(evt =>
            {
                if (ExcludableFolder(label, out _) != null)
                    label.style.color = EditorGUIUtility.isProSkin ? new Color(0.45f, 0.7f, 1f) : new Color(0.1f, 0.3f, 0.75f);
            });
            label.RegisterCallback<PointerLeaveEvent>(_ => label.style.color = StyleKeyword.Null);
            label.AddManipulator(new ContextualMenuManipulator(evt =>
            {
                string folder = ExcludableFolder(label, out string name);
                if (folder == null) return;
                evt.menu.AppendAction($"Exclude folder \"{name}\"", _ => ExcludeFolder(folder));
            }));
            return label;
        }

        // The folder a path segment names, or null for "Assets" itself and for the file name.
        private static string ExcludableFolder(Label segment, out string name)
        {
            name = null;
            if (!(segment.parent?.userData is UnusedAsset asset)) return null;
            int index = segment.parent.IndexOf(segment);
            string[] segments = asset.Path.Split('/');
            if (index <= 0 || index >= segments.Length - 1) return null;
            name = segments[index];
            return string.Join("/", segments, 0, index + 1);
        }

        private void ExcludeFolder(string folder)
        {
            string prefix = folder + "/";
            UnusedAssetFinderSettings settings = Settings;
            List<string> lines = settings.ExtraProtectedPathList;
            if (!lines.Any(line => string.Equals(line, prefix, StringComparison.OrdinalIgnoreCase)))
            {
                lines.Add(prefix);
                settings.ExtraProtectedPaths = string.Join("\n", lines);
                settings.Persist();
                extraPathsField.SetValueWithoutNotify(settings.ExtraProtectedPaths);
            }

            if (result == null) return;
            int removed = 0;
            foreach (List<UnusedAsset> source in new[] { result.Unused, result.Kept })
            {
                removed += source.RemoveAll(asset =>
                {
                    if (!asset.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
                    marked.Remove(asset.Guid);
                    return true;
                });
            }
            result.ProtectedByReason.TryGetValue(ProtectionRules.ExtraPathsReason, out int count);
            result.ProtectedByReason[ProtectionRules.ExtraPathsReason] = count + removed;
            excludedSinceScan = true;
            RebuildProtectedFoldout();
            ApplyFilter();
        }

        private void Scan()
        {
            // The scan reads files on disk; unsaved scene edits would be invisible to it. No blanket
            // AssetDatabase.SaveAssets(): it re-imports dirty RenderTextures that cameras are drawing into,
            // which crashes the editor (RenderTexture::SetActive).
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            UnusedAssetFinderSettings settings = Settings;
            try
            {
                result = UnusedAssetScanner.Scan(new ScanOptions
                {
                    ExcludedSceneGuids = settings.ExcludedSceneGuids,
                    PrefabsAreRoots = settings.PrefabsAreRoots,
                    ScriptableAssetsAreRoots = settings.ScriptableAssetsAreRoots,
                    ExtraProtectedPaths = settings.ExtraProtectedPathList,
                    KeptGuids = settings.KeptGuids,
                    Progress = (message, fraction) => EditorUtility.DisplayProgressBar("Unused Asset Finder", message, fraction),
                });
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            excludedSinceScan = false;
            marked.Clear();
            RebuildSceneToggles();
            RebuildProtectedFoldout();
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            shown.Clear();
            if (result != null)
            {
                string search = searchField.value ?? "";
                foreach (UnusedAsset asset in showKeptToggle.value ? result.Kept : result.Unused)
                {
                    if (asset.Kind == UnusedAssetKind.Material && !materialsToggle.value) continue;
                    if (asset.Kind == UnusedAssetKind.Texture && !texturesToggle.value) continue;
                    if (search.Length > 0 && asset.Path.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    shown.Add(asset);
                }
            }

            SortShown();
            list.ClearSelection();
            list.RefreshItems();
            keepButton.text = showKeptToggle.value ? "Unkeep marked" : "Keep marked";
            keepButton.tooltip = showKeptToggle.value
                ? "Return the marked assets to the unused list."
                : "Hide the marked assets from future scans of this project (stored in UserSettings/).";
            UpdateSummary();
            UpdateFooter();
        }

        private void SortShown()
        {
            SortColumnDescription sort = list?.sortedColumns?.FirstOrDefault();
            Comparison<UnusedAsset> compare = (sort?.columnName) switch
            {
                "type" => (a, b) => string.CompareOrdinal(a.TypeName, b.TypeName),
                "size" => (a, b) => a.SizeBytes.CompareTo(b.SizeBytes),
                _ => (a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase),
            };
            if (sort != null && sort.direction == SortDirection.Descending) shown.Sort((a, b) => compare(b, a));
            else shown.Sort(compare);
        }

        private void UpdateSummary()
        {
            if (result == null) return;
            List<UnusedAsset> materials = result.Unused.Where(a => a.Kind == UnusedAssetKind.Material).ToList();
            List<UnusedAsset> textures = result.Unused.Where(a => a.Kind == UnusedAssetKind.Texture).ToList();
            summaryLabel.text =
                $"Unused: {materials.Count} materials ({EditorUtility.FormatBytes(materials.Sum(a => a.SizeBytes))}), " +
                $"{textures.Count} textures ({EditorUtility.FormatBytes(textures.Sum(a => a.SizeBytes))}). " +
                $"Kept: {result.Kept.Count}. In use: {result.UsedCount}. Protected: {result.ProtectedCount}. " +
                $"Counted as used: {result.RootScenes.Count} scene(s)" +
                (Settings.PrefabsAreRoots ? ", all prefabs" : "") +
                (Settings.ScriptableAssetsAreRoots ? ", ScriptableObject assets" : "") + "." +
                (excludedSinceScan ? " Scan again to also drop assets elsewhere that only the excluded folders use." : "");
        }

        private void UpdateFooter()
        {
            List<UnusedAsset> markedAssets = MarkedAssets();
            footerLabel.text = $"{markedAssets.Count} marked · {EditorUtility.FormatBytes(markedAssets.Sum(a => a.SizeBytes))}  |  {shown.Count} shown";
        }

        private List<UnusedAsset> MarkedAssets()
        {
            if (result == null || marked.Count == 0) return new List<UnusedAsset>();
            return (showKeptToggle.value ? result.Kept : result.Unused).Where(a => marked.Contains(a.Guid)).ToList();
        }

        private void SetMarked(IEnumerable<UnusedAsset> assets, bool value)
        {
            foreach (UnusedAsset asset in assets)
            {
                if (value) marked.Add(asset.Guid); else marked.Remove(asset.Guid);
            }
            list.RefreshItems();
            UpdateFooter();
        }

        private void ToggleKeepMarked()
        {
            List<UnusedAsset> assets = MarkedAssets();
            if (assets.Count == 0) return;

            bool keep = !showKeptToggle.value;
            UnusedAssetFinderSettings settings = Settings;
            foreach (UnusedAsset asset in assets)
            {
                if (keep)
                {
                    if (!settings.KeptGuids.Contains(asset.Guid)) settings.KeptGuids.Add(asset.Guid);
                    result.Unused.Remove(asset);
                    result.Kept.Add(asset);
                }
                else
                {
                    settings.KeptGuids.Remove(asset.Guid);
                    result.Kept.Remove(asset);
                    result.Unused.Add(asset);
                }
            }
            settings.Persist();
            marked.Clear();
            ApplyFilter();
        }

        private void DeleteMarked()
        {
            List<UnusedAsset> assets = MarkedAssets();
            if (assets.Count == 0) return;

            string preview = string.Join("\n", assets.Take(PreviewLines).Select(a => a.Path));
            if (assets.Count > PreviewLines) preview += $"\n… and {assets.Count - PreviewLines} more";
            string size = EditorUtility.FormatBytes(assets.Sum(a => a.SizeBytes));
            string folderNote = Settings.DeleteEmptiedFolders ? "\n\nFolders left completely empty are moved to the trash too." : "";
            if (!EditorUtility.DisplayDialog("Delete unused assets",
                    $"Move {assets.Count} assets ({size}) to the system trash?{folderNote}\n\n{preview}", "Move to Trash", "Cancel"))
                return;

            var failed = new List<string>();
            AssetDatabase.MoveAssetsToTrash(assets.Select(a => a.Path).ToArray(), failed);
            var failedSet = new HashSet<string>(failed);
            if (failed.Count > 0) Debug.LogWarning("[Unused Asset Finder] Could not move to trash:\n" + string.Join("\n", failed));

            UnusedAssetFinderSettings settings = Settings;
            foreach (UnusedAsset asset in assets)
            {
                if (failedSet.Contains(asset.Path)) continue;
                result.Unused.Remove(asset);
                result.Kept.Remove(asset);
                settings.KeptGuids.Remove(asset.Guid);
            }
            settings.Persist();
            if (settings.DeleteEmptiedFolders)
                TrashEmptiedFolders(assets.Select(a => a.Path).Where(path => !failedSet.Contains(path)));
            marked.Clear();
            ApplyFilter();
        }

        // Trashes the folders of the deleted assets, and their ancestors below Assets/, that have nothing left
        // on disk: no files (hidden ones and stray .meta included) and no subfolders. Deepest first, so a parent
        // holding only folders this pass emptied is judged after they are gone.
        private static void TrashEmptiedFolders(IEnumerable<string> deletedPaths)
        {
            var candidates = new HashSet<string>();
            foreach (string path in deletedPaths)
            {
                for (string folder = ParentFolder(path); folder != null && folder != "Assets"; folder = ParentFolder(folder))
                    if (!candidates.Add(folder)) break;
            }

            var trashed = new List<string>();
            foreach (string folder in candidates.OrderByDescending(f => f.Count(c => c == '/')))
            {
                if (!AssetDatabase.IsValidFolder(folder) || Directory.EnumerateFileSystemEntries(folder).Any()) continue;
                if (AssetDatabase.MoveAssetToTrash(folder)) trashed.Add(folder);
                else Debug.LogWarning($"[Unused Asset Finder] Could not move emptied folder to trash: {folder}");
            }
            if (trashed.Count > 0) Debug.Log($"[Unused Asset Finder] Moved {trashed.Count} emptied folder(s) to trash:\n" + string.Join("\n", trashed));
        }

        private static string ParentFolder(string path)
        {
            int slash = path.LastIndexOf('/');
            return slash > 0 ? path.Substring(0, slash) : null;
        }

        private void RebuildSceneToggles()
        {
            scenesContainer.Clear();
            var excluded = new HashSet<string>(Settings.ExcludedSceneGuids);
            List<KeyValuePair<string, string>> scenes = UnusedAssetScanner.FindScenes();
            foreach (KeyValuePair<string, string> scene in scenes)
            {
                string guid = scene.Key;
                var toggle = new Toggle { text = scene.Value, value = !excluded.Contains(guid) };
                toggle.RegisterValueChangedCallback(evt => SetSceneIncluded(guid, evt.newValue));
                scenesContainer.Add(toggle);
            }
            UpdateScenesTitle();
        }

        private void SetSceneIncluded(string guid, bool included)
        {
            List<string> excluded = Settings.ExcludedSceneGuids;
            if (included) excluded.Remove(guid);
            else if (!excluded.Contains(guid)) excluded.Add(guid);
            Settings.Persist();
            UpdateScenesTitle();
        }

        private void SetAllScenesIncluded(bool included)
        {
            List<string> excluded = Settings.ExcludedSceneGuids;
            excluded.Clear();
            if (!included) excluded.AddRange(UnusedAssetScanner.FindScenes().Select(scene => scene.Key));
            Settings.Persist();
            RebuildSceneToggles();
        }

        private void UpdateScenesTitle()
        {
            List<Toggle> toggles = scenesContainer.Children().OfType<Toggle>().ToList();
            scenesFoldout.text = $"Scenes ({toggles.Count(t => t.value)} of {toggles.Count} counted)";
        }

        private void RebuildProtectedFoldout()
        {
            protectedFoldout.Clear();
            if (result != null)
            {
                protectedFoldout.text = $"Protected (never listed): {result.ProtectedCount}";
                foreach (KeyValuePair<string, int> pair in result.ProtectedByReason.OrderByDescending(p => p.Value))
                    protectedFoldout.Add(new Label($"{pair.Value,6}  {pair.Key}"));
            }
            var rules = new Foldout { text = "Built-in rules", value = false };
            foreach (string line in ProtectionRules.DescribeBuiltIn())
                rules.Add(new Label(line) { style = { whiteSpace = WhiteSpace.Normal } });
            rules.Add(new Label(UnusedAssetScanner.ScriptReferenceReason));
            rules.Add(new Label(UnusedAssetScanner.ShaderDefaultReason));
            protectedFoldout.Add(rules);
        }
    }
}
