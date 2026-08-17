// OutfitRegistryBuilder.cs
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Bozo.ModularCharacters;
using UnityEditor;
using UnityEngine;

public static class OutfitRegistryBuilder
{
    [MenuItem("ChaosPit/Build Outfit Registry")]
    public static void BuildRegistry()
    {
        // Find the registry asset - adjust the search filter/path if you have more than one
        var guids = AssetDatabase.FindAssets("t:OutfitRegistry");
        if (guids.Length == 0)
        {
            Debug.LogError("No OutfitRegistry asset found. Create one first: Right-click > Create > ChaosPit > Outfit Registry");
            return;
        }
        if (guids.Length > 1)
        {
            Debug.LogWarning("Multiple OutfitRegistry assets found, using the first one.");
        }

        var path = AssetDatabase.GUIDToAssetPath(guids[0]);
        var registry = AssetDatabase.LoadAssetAtPath<OutfitRegistry>(path);

        // Snapshot existing per-item data before rebuilding, keyed by resource path so it
        // survives even if array order/indices shift when new items are added.
        var oldRequiredLevelByPath = new Dictionary<(OutfitType, string), int>();
        var oldDefaultPathByType = new Dictionary<OutfitType, string>();
        var oldIncludeInRandomByType = new Dictionary<OutfitType, bool>();

        if (registry.slots != null)
        {
            foreach (var entry in registry.slots)
            {
                if (entry.type == null || entry.resourcePaths == null) continue;

                oldIncludeInRandomByType[entry.type] = entry.includeInRandomDefault;

                for (int i = 0; i < entry.resourcePaths.Length; i++)
                {
                    int level = (entry.requiredLevels != null && i < entry.requiredLevels.Length)
                        ? entry.requiredLevels[i] : 1;
                    oldRequiredLevelByPath[(entry.type, entry.resourcePaths[i])] = level;
                }

                if (entry.defaultOutfitId >= 0 && entry.defaultOutfitId < entry.resourcePaths.Length)
                    oldDefaultPathByType[entry.type] = entry.resourcePaths[entry.defaultOutfitId];
            }
        }

        // Find every prefab in the project, filter down to ones with an Outfit component
        // sitting inside a folder named "Resources"
        var prefabGuids = AssetDatabase.FindAssets("t:Prefab");
        var bucket = new Dictionary<OutfitType, List<string>>();

        int scanned = 0;
        int matched = 0;

        foreach (var prefabGuid in prefabGuids)
        {
            var assetPath = AssetDatabase.GUIDToAssetPath(prefabGuid);

            if (!assetPath.Contains("/Resources/")) continue;
            scanned++;

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefab == null) continue;

            var outfit = prefab.GetComponent<Outfit>();
            if (outfit == null) continue;

            var resourcePath = GetResourcesRelativePath(assetPath);
            if (resourcePath == null) continue;

            if (!bucket.TryGetValue(outfit.Type, out var list))
            {
                list = new List<string>();
                bucket[outfit.Type] = list;
            }

            if (!list.Contains(resourcePath))
            {
                list.Add(resourcePath);
                matched++;
            }
        }

        // Rebuild slots array, restoring preserved per-item data by matching path strings
        var slots = new List<OutfitRegistry.OutfitEntry>();
        int newItemsFound = 0;

        foreach (var kvp in bucket.OrderBy(k => k.Key.name))
        {
            var type = kvp.Key;
            var sortedPaths = kvp.Value.OrderBy(p => p).ToArray();

            var requiredLevels = new int[sortedPaths.Length];
            int defaultOutfitId = -1;

            for (int i = 0; i < sortedPaths.Length; i++)
            {
                if (oldRequiredLevelByPath.TryGetValue((type, sortedPaths[i]), out int level))
                {
                    requiredLevels[i] = level;
                }
                else
                {
                    requiredLevels[i] = 1; // new item, default to unlocked
                    newItemsFound++;
                }

                if (oldDefaultPathByType.TryGetValue(type, out string oldDefaultPath) && oldDefaultPath == sortedPaths[i])
                    defaultOutfitId = i;
            }

            bool includeInRandom = oldIncludeInRandomByType.TryGetValue(type, out bool oldVal) ? oldVal : true;

            slots.Add(new OutfitRegistry.OutfitEntry
            {
                type = type,
                resourcePaths = sortedPaths,
                includeInRandomDefault = includeInRandom,
                requiredLevels = requiredLevels,
                defaultOutfitId = defaultOutfitId
            });
        }

        registry.slots = slots.ToArray();

        EditorUtility.SetDirty(registry);
        AssetDatabase.SaveAssets();

        Debug.Log($"Outfit Registry built: scanned {scanned} prefabs under Resources folders, matched {matched} Outfit prefabs across {slots.Count} outfit types. {newItemsFound} new item(s) added at default level 1.");
    }

    private static string GetResourcesRelativePath(string assetPath)
    {
        // assetPath e.g. "Assets/BoZo_StylizedModularCharacters/Prefabs/Common/Resources/Socks/Socks_BasicSocks.prefab"
        const string marker = "/Resources/";
        int idx = assetPath.IndexOf(marker);
        if (idx < 0) return null;

        var relative = assetPath.Substring(idx + marker.Length);
        relative = Path.ChangeExtension(relative, null); // strip .prefab
        return relative.Replace('\\', '/');
    }
}