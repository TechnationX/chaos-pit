// CharacterLoadout.cs
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Bozo.ModularCharacters;

public struct SlotLoadout
{
    public byte typeIndex;   // index into OutfitRegistry.slots
    public short outfitId;   // index into that slot's resourcePaths
    public byte swatch;
    public Color32[] colors; // only the channels actually used, no alpha
}

public static class CharacterLoadout
{
    // Build from a live OutfitSystem (called in CharacterCreator scene on "Continue")
    public static List<SlotLoadout> BuildFromOutfitSystem(OutfitSystem system, OutfitRegistry registry)
    {
        var result = new List<SlotLoadout>();

        foreach (var outfit in system.GetOutfits())
        {
            if (outfit == null) continue;

            int typeIndex = registry.GetTypeIndex(outfit.Type);
            if (typeIndex < 0) continue; // slot not in registry, skip

            var data = outfit.GetOutfitData(); // existing BSMC method
            int outfitId = registry.GetId(outfit.Type, data.outfit);
            if (outfitId < 0) continue; // outfit not registered, skip

            var colors = new Color32[data.colors.Count];
            for (int i = 0; i < data.colors.Count; i++)
                colors[i] = data.colors[i];

            result.Add(new SlotLoadout
            {
                typeIndex = (byte)typeIndex,
                outfitId = (short)outfitId,
                swatch = (byte)data.swatch,
                colors = colors
            });
        }

        return result;
    }

    public static byte[] Pack(List<SlotLoadout> slots)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        w.Write((byte)slots.Count);
        foreach (var s in slots)
        {
            w.Write(s.typeIndex);
            w.Write(s.outfitId);
            w.Write(s.swatch);
            w.Write((byte)s.colors.Length);
            foreach (var c in s.colors)
            {
                w.Write(c.r);
                w.Write(c.g);
                w.Write(c.b);
            }
        }
        return ms.ToArray();
    }

    public static List<SlotLoadout> Unpack(byte[] data)
    {
        var result = new List<SlotLoadout>();
        if (data == null || data.Length == 0) return result;

        using var ms = new MemoryStream(data);
        using var r = new BinaryReader(ms);

        int count = r.ReadByte();
        for (int i = 0; i < count; i++)
        {
            var s = new SlotLoadout
            {
                typeIndex = r.ReadByte(),
                outfitId = r.ReadInt16(),
                swatch = r.ReadByte()
            };
            int colorCount = r.ReadByte();
            s.colors = new Color32[colorCount];
            for (int c = 0; c < colorCount; c++)
                s.colors[c] = new Color32(r.ReadByte(), r.ReadByte(), r.ReadByte(), 255);

            result.Add(s);
        }
        return result;
    }

    // Applies a loadout to a target OutfitSystem (used on every client for every player)
    public static async void Apply(OutfitSystem system, List<SlotLoadout> slots, OutfitRegistry registry)
    {
        foreach (var s in slots)
        {
            var type = registry.GetTypeByIndex(s.typeIndex);
            var path = registry.GetPath(type, s.outfitId);
            if (path == null) continue;

            var prefab = Resources.Load<Outfit>(path);
            if (prefab == null) continue;

            var inst = Object.Instantiate(prefab, system.transform);
            inst.Attach(system);

            for (int c = 0; c < s.colors.Length; c++)
                inst.SetColor(s.colors[c], c + 1);
        }

        await system.MergeCharacter();
    }
}