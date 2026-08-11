// OutfitRegistry.cs
using System.Collections.Generic;
using UnityEngine;
using Bozo.ModularCharacters;

[CreateAssetMenu(fileName = "OutfitRegistry", menuName = "ChaosPit/Outfit Registry")]
public class OutfitRegistry : ScriptableObject
{
    [System.Serializable]
    public class OutfitEntry
    {
        public OutfitType type;
        // Resources-relative paths, e.g. "Top/Top_Hoodie" - matches BSMC's own load convention
        public string[] resourcePaths;
        public bool includeInRandomDefault = true;
        // Parallel array to resourcePaths — required level to unlock each item. 1 = available from the start.
        public int[] requiredLevels;
        // Index into resourcePaths for this slot's fixed starting item. -1 = this slot starts empty (e.g. bald head, bare feet).
        public int defaultOutfitId = -1;
    }

    public OutfitEntry[] slots;

    private Dictionary<OutfitType, string[]> _lookup;

    private void BuildLookupIfNeeded()
    {
        if (_lookup != null) return;
        _lookup = new Dictionary<OutfitType, string[]>();
        foreach (var entry in slots)
            _lookup[entry.type] = entry.resourcePaths;
    }

    public string GetPath(OutfitType type, int id)
    {
        BuildLookupIfNeeded();
        if (id < 0) return null;
        if (!_lookup.TryGetValue(type, out var arr)) return null;
        if (id >= arr.Length) return null;
        return arr[id];
    }

    public int GetId(OutfitType type, string resourcePath)
    {
        BuildLookupIfNeeded();
        if (!_lookup.TryGetValue(type, out var arr)) return -1;
        for (int i = 0; i < arr.Length; i++)
            if (arr[i] == resourcePath) return i;
        return -1;
    }

    public int GetTypeIndex(OutfitType type)
    {
        for (int i = 0; i < slots.Length; i++)
            if (slots[i].type == type) return i;
        return -1;
    }

    public OutfitType GetTypeByIndex(int index) => slots[index].type;

    public int GetRequiredLevel(OutfitType type, int id)
    {
        BuildLookupIfNeeded();
        foreach (var entry in slots)
        {
            if (entry.type != type) continue;
            if (entry.requiredLevels == null || id < 0 || id >= entry.requiredLevels.Length) return 1;
            return entry.requiredLevels[id];
        }
        return 1;
    }

    public int GetRequiredLevelForOutfit(Outfit outfit)
    {
        BuildLookupIfNeeded();
        if (outfit.Type == null) return 1;
        if (!_lookup.TryGetValue(outfit.Type, out var paths)) return 1;

        for (int i = 0; i < paths.Length; i++)
        {
            string leaf = System.IO.Path.GetFileName(paths[i]);
            if (leaf == outfit.name) return GetRequiredLevel(outfit.Type, i);
        }
        return 1;
    }
}