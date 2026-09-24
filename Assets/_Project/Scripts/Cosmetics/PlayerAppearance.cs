// PlayerAppearance.cs
using System.Collections.Generic;
using Bozo.ModularCharacters;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

public class PlayerAppearance : NetworkBehaviour
{
    [SerializeField] private OutfitRegistry registry;
    [SerializeField] private OutfitSystem outfitSystem;

    private readonly SyncVar<byte[]> _loadoutData = new SyncVar<byte[]>();

    private void Awake()
    {
        _loadoutData.OnChange += OnLoadoutChanged;
    }

    public override void OnStartClient()
    {
        base.OnStartClient();

        if (IsOwner)
        {
            var localSlots = LoadLocalLoadout();
            if (localSlots == null) return;

            ApplyOwnerLoadout(localSlots);
        }
        else if (_loadoutData.Value is { Length: > 0 })
        {
            // Late join: remote player's loadout already replicated on spawn
            ApplyReceivedLoadout(_loadoutData.Value);
        }
    }

    // Only the owner's own build passes keepHeadPiecesSeparate: true, so only this
    // client's own copy of each slot in CharacterLoadout.KeepSeparateWhenOwned (Head,
    // hair, glasses/eyewear, facial hair — anything that would clip into the first-person
    // camera) survives merge as its own toggleable GameObject — see CharacterLoadout.Apply
    // and SetOwnHeadPiecesVisible below. Everyone else still builds and merges this player
    // fully from the synced bytes SubmitLoadout sends out.
    // async void — Unity's own event-callback pattern for a "fire and forget"
    // entry point, but that also means an unhandled exception here doesn't
    // propagate anywhere useful; it just becomes an unobserved exception
    // Unity logs on its own terms, well after the fact. Wrapped in try/catch
    // now so a failure in CharacterLoadout.Apply (which already has its own
    // internal timeout on the BSMC merge step — see that method's comment)
    // is at least logged clearly against this player, instead of silently
    // leaving them stuck with no loadout submitted and no explanation in the
    // log for why.
    private async void ApplyOwnerLoadout(List<SlotLoadout> localSlots)
    {
        try
        {
            await CharacterLoadout.Apply(outfitSystem, localSlots, registry, keepHeadPiecesSeparate: true);
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[PlayerAppearance] ApplyOwnerLoadout failed for {gameObject.name} — no loadout will be submitted for this join. {e}");
            return;
        }

        SubmitLoadout(CharacterLoadout.Pack(localSlots));

        // These outfit pieces don't exist until the await above finishes, so re-apply
        // whatever camera mode we're already in now that they do — otherwise a
        // SetOwnHeadPiecesVisible(false) call PlayerCamera fired earlier (before this
        // finished) would silently get lost and they'd show up even in first person.
        var camera = GetComponent<PlayerCamera>();
        if (camera != null)
            SetOwnHeadPiecesVisible(camera.CurrentMode != PlayerCamera.CameraMode.FirstPerson);
    }

    private const string OwnHeadLayerName = "OwnHead";

    // Called by PlayerCamera.SwitchTo whenever the local player's camera mode changes.
    // Toggles every slot in CharacterLoadout.KeepSeparateWhenOwned (Head, HairFront,
    // HairBack, UpperFace, LowerFace) the same way Head alone used to be handled — add a
    // slot name there, not here, to give another piece this same treatment. No-op per
    // slot for every non-owner copy of this player — their pieces were merged normally,
    // so GetOutfit(slotName) returns null there (see CharacterLoadout.Apply).
    //
    // targetCamera: which camera's culling mask to flip the OwnHead layer bit on.
    // Defaults to Camera.main, which is correct for first/third person — but minigames
    // render through their own scene-placed top-down camera instead of Camera.main, so
    // PlayerCamera passes that camera explicitly while in MiniGame mode. Editing
    // Camera.main's mask during a minigame was the earlier bug here: it changed a
    // camera that wasn't actually the one on screen, so the player's own head visibly
    // stayed hidden even though this method "succeeded."
    public void SetOwnHeadPiecesVisible(bool visible, Camera targetCamera = null)
    {
        int ownHeadLayer = LayerMask.NameToLayer(OwnHeadLayerName);

        foreach (var slotName in CharacterLoadout.KeepSeparateWhenOwned)
        {
            var piece = outfitSystem.GetOutfit(slotName);
            if (piece == null) continue; // slot empty (e.g. no hair/glasses equipped) — nothing to toggle

            // BSMC's merge step deactivates the source outfit root after building its
            // CombinedSkinnedMesh renderer, even when mergeMesh is false. Force it back
            // active every time — visibility is now handled entirely by the layer +
            // culling mask below, not by activation, since a mirror camera needs this
            // object active regardless of what the local player's own camera shows.
            piece.gameObject.SetActive(true);

            if (ownHeadLayer != -1 && piece.gameObject.layer != ownHeadLayer)
                SetLayerRecursively(piece.gameObject, ownHeadLayer);
        }

        Camera cam = targetCamera != null ? targetCamera : Camera.main;
        if (cam == null || ownHeadLayer == -1) return;

        int headBit = 1 << ownHeadLayer;
        cam.cullingMask = visible
            ? cam.cullingMask | headBit
            : cam.cullingMask & ~headBit;
    }

    private static void SetLayerRecursively(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform child in go.transform)
            SetLayerRecursively(child.gameObject, layer);
    }

    [ServerRpc(RequireOwnership = true)]
    private void SubmitLoadout(byte[] packedData)
    {
        _loadoutData.Value = packedData;
        PlayerProfileManager.Instance.SetLoadout(Owner, packedData);
    }

    private void OnLoadoutChanged(byte[] prev, byte[] next, bool asServer)
    {
        if (asServer) return;
        if (IsOwner) return;
        if (next is not { Length: > 0 }) return;

        ApplyReceivedLoadout(next);
    }

    private void ApplyReceivedLoadout(byte[] packed)
    {
        var slots = CharacterLoadout.Unpack(packed);
        CharacterLoadout.Apply(outfitSystem, slots, registry);
    }

    private List<SlotLoadout> LoadLocalLoadout()
    {
        var packed = LocalPlayerLoadout.Instance?.PendingLoadoutData;
        if (packed == null || packed.Length == 0)
            return CharacterLoadout.BuildDefaultLoadout(registry);

        return CharacterLoadout.Unpack(packed);
    }
}