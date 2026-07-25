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
}