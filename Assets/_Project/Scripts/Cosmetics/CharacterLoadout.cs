// CharacterLoadout.cs
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
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

    // Outfit slot type names that get kept as their own unmerged, always-active
    // GameObjects for the local owner (see Apply's keepHeadPiecesSeparate below) —
    // anything worn on/around the head or face that would otherwise clip into the
    // first-person camera. PlayerAppearance.SetOwnHeadPiecesVisible reads this same
    // list to toggle each piece's layer in sync with camera mode, so a mirror (or
    // third-person/minigame view) still renders it while the owner's own first-person
    // camera excludes it. Add a slot type name here — and only here — to give another
    // piece (e.g. "Hat", "HeadAcc") this same treatment.
    public static readonly string[] KeepSeparateWhenOwned =
    {
        "Head", "HairFront", "HairBack", "UpperFace", "LowerFace", "Hat", "HeadAcc"
    };

    // How long we'll wait on OutfitSystem.MergeCharacter() before giving up on
    // it and moving on anyway. See the comment below the slot loop for why
    // this exists.
    private const int MergeCharacterTimeoutMs = 5000;

    // Applies a loadout to a target OutfitSystem (used on every client for every player).
    // keepHeadPiecesSeparate: true excludes each slot in KeepSeparateWhenOwned from BSMC's
    // mesh-combine step (sets Outfit.mergeMesh = false) so it stays its own always-skinned
    // SkinnedMeshRenderer instead of being permanently baked into the single merged body
    // mesh. That's what lets PlayerAppearance.SetOwnHeadPiecesVisible toggle each one
    // afterwards without a re-merge. Only ever pass true for the local owner's own build —
    // every other client still merges normally, so nobody else's view of this player changes.
    public static async Task Apply(OutfitSystem system, List<SlotLoadout> slots, OutfitRegistry registry, bool keepHeadPiecesSeparate = false)
    {
        foreach (var s in slots)
        {
            var type = registry.GetTypeByIndex(s.typeIndex);
            var path = registry.GetPath(type, s.outfitId);
            if (path == null)
            {
                Debug.LogWarning($"[CharacterLoadout] registry.GetPath returned null for slot type index {s.typeIndex}, outfit id {s.outfitId} — this outfit piece will be missing. Check OutfitRegistry for a stale/removed entry.");
                continue;
            }

            var prefab = Resources.Load<Outfit>(path);
            if (prefab == null)
            {
                Debug.LogWarning($"[CharacterLoadout] Resources.Load<Outfit> failed for path '{path}' (slot type index {s.typeIndex}, outfit id {s.outfitId}) — this outfit piece will be missing. Check that the asset exists under a Resources folder at that exact path.");
                continue;
            }

            var inst = Object.Instantiate(prefab, system.transform);
            inst.Attach(system);

            if (keepHeadPiecesSeparate && inst.Type != null && System.Array.IndexOf(KeepSeparateWhenOwned, inst.Type.name) >= 0)
                inst.mergeMesh = false;

            for (int c = 0; c < s.colors.Length; c++)
            {
                try
                {
                    inst.SetColor(s.colors[c], c + 1);
                }
                catch (System.Exception)
                {
                    break; // outfit has fewer color channels than we tried to set — not an error worth logging
                }
            }
        }

        // Every step above (Resources.Load/Instantiate/Attach/SetColor) is
        // synchronous — MergeCharacter() is the one genuinely async step, and
        // per technical-learnings.md it's the documented source of
        // "progressive main thread stalls" in the built exe, usually tied to
        // a stale LocalPlayer.json repeatedly triggering an expensive
        // backfill path inside BSMC. Without a timeout, a joining client
        // whose own PlayerAppearance.ApplyOwnerLoadout is awaiting this call
        // would sit here indefinitely if BSMC ever hits that stall — which,
        // from that player's side, looks exactly like the game hanging on
        // join. All the outfit pieces above are already instantiated and
        // attached to the model by this point (just not yet combined into
        // one merged mesh), which is also the likely explanation for a host
        // seeing only a few pieces of a stuck joiner's character (arm, legs,
        // partial head) instead of nothing at all.
        //
        // This doesn't fix whatever's actually slow/stuck inside BSMC — we
        // don't have its source here — it just bounds how long we'll wait on
        // it, so a stall becomes "logs a warning, character stays unmerged
        // but the join keeps moving" instead of "the client is stuck
        // forever." If MergeCharacter() does eventually complete after the
        // timeout, its result is simply discarded — the outfit pieces it
        // would have combined are still there and still visible, just as
        // separate unmerged meshes instead of one combined one.
        Task mergeTask = system.MergeCharacter();
        Task firstCompleted = await Task.WhenAny(mergeTask, Task.Delay(MergeCharacterTimeoutMs));

        if (firstCompleted != mergeTask)
        {
            Debug.LogWarning($"[CharacterLoadout] system.MergeCharacter() did not complete within {MergeCharacterTimeoutMs}ms — continuing without waiting for it (see technical-learnings.md's OutfitSystem main-thread-stall note). Character may render as unmerged/partial outfit pieces instead of one combined mesh.");
            return;
        }

        // Already finished — await it (not .Wait()/.Result) purely to
        // observe/rethrow any exception it faulted with, without blocking.
        await mergeTask;
    }

    // Builds the fixed starting loadout, using each slot's registry-defined default item.
    // Used when a player has no saved appearance yet (first launch, no LocalPlayer.json).
    public static List<SlotLoadout> BuildDefaultLoadout(OutfitRegistry registry)
    {
        var result = new List<SlotLoadout>();
        if (registry.slots == null) return result;

        foreach (var entry in registry.slots)
        {
            if (entry.defaultOutfitId < 0) continue; // slot intentionally starts empty
            if (entry.resourcePaths == null || entry.defaultOutfitId >= entry.resourcePaths.Length) continue;

            int typeIndex = registry.GetTypeIndex(entry.type);
            if (typeIndex < 0) continue;

            result.Add(new SlotLoadout
            {
                typeIndex = (byte)typeIndex,
                outfitId = (short)entry.defaultOutfitId,
                swatch = 0,
                colors = new Color32[0]
            });
        }

        return result;
    }
}