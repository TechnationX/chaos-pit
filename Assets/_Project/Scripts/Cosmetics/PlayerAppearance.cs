// PlayerAppearance.cs
using System.Threading.Tasks;
using Bozo.ModularCharacters;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

public class PlayerAppearance : NetworkBehaviour
{
    [SerializeField] private OutfitRegistry registry;
    [SerializeField] private OutfitSystem outfitSystem;

    private readonly SyncVar<byte[]> _loadoutData = new SyncVar<byte[]>();

    // Owner only. Set by OutfitSystem events (any re-merge, any body-shape change) so
    // the separate head pieces get re-prepared once, at end of frame, after every BSMC
    // subscriber has finished updating. Deferring to LateUpdate keeps this independent
    // of event subscriber order: outfits attached later (e.g. a lobby re-customization)
    // subscribe to OnShapeChanged after this does, and must update first.
    private bool _ownHeadPiecesDirty;
    private bool _subscribedToOutfitSystem;

    // Appearance applies run one at a time per player. A re-apply now clears and
    // rebuilds the whole character, so two arriving close together (late-join sync
    // plus a change, or two quick in-lobby edits) must not overlap mid-merge. Only
    // the newest pending bytes are kept; anything older is already superseded.
    private byte[] _pendingApply;
    private bool _pendingIsOwnerBuild;
    private bool _isApplying;

    private void Awake()
    {
        _loadoutData.OnChange += OnLoadoutChanged;
    }

    public override void OnStartClient()
    {
        base.OnStartClient();

        if (IsOwner)
        {
            QueueApply(LoadLocalLoadout(), isOwnerBuild: true);
        }
        else if (_loadoutData.Value is { Length: > 0 })
        {
            // Late join: remote player's loadout already replicated on spawn
            QueueApply(_loadoutData.Value, isOwnerBuild: false);
        }
    }

    // Called by CustomizationStation after the player saves in the in-lobby creator.
    // Rebuilds the owner's own character and broadcasts the new bytes, so every other
    // client rebuilds it too (OnLoadoutChanged).
    public void ApplyNewOwnerAppearance(byte[] packed)
    {
        if (!IsOwner || packed == null || packed.Length == 0) return;
        QueueApply(packed, isOwnerBuild: true);
    }

    private async void QueueApply(byte[] packed, bool isOwnerBuild)
    {
        if (packed == null || packed.Length == 0) return;

        _pendingApply = packed;
        _pendingIsOwnerBuild = isOwnerBuild;
        if (_isApplying) return;

        _isApplying = true;
        try
        {
            while (_pendingApply != null)
            {
                byte[] next = _pendingApply;
                bool ownerBuild = _pendingIsOwnerBuild;
                _pendingApply = null;

                if (ownerBuild) await RunOwnerApply(next);
                else await RunRemoteApply(next);

                if (this == null) return; // destroyed (disconnect/scene change) mid-apply
            }
        }
        finally
        {
            _isApplying = false;
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
    private async Task RunOwnerApply(byte[] packed)
    {
        try
        {
            await CharacterLoadout.ApplyPacked(outfitSystem, packed, registry, keepHeadPiecesSeparate: true);
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[PlayerAppearance] Owner appearance apply failed for {gameObject.name} — nothing submitted. {e}");
            return;
        }

        if (this == null) return;

        SubmitLoadout(packed);

        SubscribeToOutfitSystemChanges();

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
        int ownHeadLayer = PrepareOwnHeadPieces();

        Camera cam = targetCamera != null ? targetCamera : Camera.main;
        if (cam == null || ownHeadLayer == -1) return;

        int headBit = 1 << ownHeadLayer;
        cam.cullingMask = visible
            ? cam.cullingMask | headBit
            : cam.cullingMask & ~headBit;
    }

    // Everything SetOwnHeadPiecesVisible needs done to the pieces themselves, split out
    // so it can also re-run after any BSMC re-merge or shape change (see LateUpdate)
    // without touching the camera mask. Safe to call any number of times. Returns the
    // OwnHead layer index (-1 if the layer doesn't exist).
    private int PrepareOwnHeadPieces()
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

            // These pieces stay as their own unmerged SkinnedMeshRenderers for the
            // local owner (mergeMesh = false), and their default bounds don't follow
            // the animated head. In third person that let frustum culling drop the
            // head, hair, and headwear at certain camera angles even though they were
            // on screen. Recompute bounds every frame instead; it's only the owner's
            // handful of head pieces (every slot in KeepSeparateWhenOwned), so the
            // cost is negligible.
            foreach (var smr in piece.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                smr.updateWhenOffscreen = true;
                SyncBlendShapesWithMergedBody(smr);
            }

            if (ownHeadLayer != -1 && piece.gameObject.layer != ownHeadLayer)
                SetLayerRecursively(piece.gameObject, ownHeadLayer);
        }

        return ownHeadLayer;
    }

    // BSMC's merge (BoZoCharacterOptimizer) combines same-named blend shapes across
    // every merged piece and drives them at the MAX weight any piece had, which is how
    // the body's neck normally stretches to meet the head. Pieces kept separate for the
    // local owner (mergeMesh = false) skip that merge, so the body neck kept only its
    // own smaller weight and no longer lined up with the head. Reproduce the same
    // max-weight rule by hand for every blend shape the separate piece shares with the
    // merged body, applied to both sides.
    //
    // Not stale after appearance changes: a re-merge rebuilds the body mesh with fresh
    // body-only weights (OnRigChanged), and OutfitSystem.SetShape writes the new value
    // to both the body and every attached piece before OnShapeChanged fires, so the
    // re-sync in LateUpdate always starts from current values on both sides.
    private void SyncBlendShapesWithMergedBody(SkinnedMeshRenderer piece)
    {
        SkinnedMeshRenderer body = outfitSystem.GetCharacterBody();
        if (body == null || body.sharedMesh == null || piece.sharedMesh == null) return;

        Mesh pieceMesh = piece.sharedMesh;
        Mesh bodyMesh = body.sharedMesh;

        for (int i = 0; i < pieceMesh.blendShapeCount; i++)
        {
            // Source meshes name shapes "MeshName.Shape_X"; the merged mesh drops the
            // prefix (same stripping the optimizer does), so match on the stripped name.
            string shapeName = pieceMesh.GetBlendShapeName(i);
            int dot = shapeName.IndexOf('.');
            if (dot >= 0) shapeName = shapeName.Substring(dot + 1);

            int bodyIndex = bodyMesh.GetBlendShapeIndex(shapeName);
            if (bodyIndex < 0) continue;

            float weight = Mathf.Max(body.GetBlendShapeWeight(bodyIndex), piece.GetBlendShapeWeight(i));
            body.SetBlendShapeWeight(bodyIndex, weight);
            piece.SetBlendShapeWeight(i, weight);
        }
    }

    // Owner only, once. After this, any BSMC re-merge (outfit added/removed/swapped,
    // including a future in-lobby customization screen re-running CharacterLoadout.Apply)
    // or body-shape change re-prepares the separate head pieces automatically: the
    // merge deactivates them and rebuilds the body mesh, so without this they'd vanish
    // or lose the neck sync until the next camera mode switch.
    private void SubscribeToOutfitSystemChanges()
    {
        if (_subscribedToOutfitSystem || outfitSystem == null) return;
        outfitSystem.OnRigChanged += HandleRigChanged;
        outfitSystem.OnShapeChanged += HandleShapeChanged;
        _subscribedToOutfitSystem = true;
    }

    // Named methods, not lambdas, so the -= in OnDestroy actually unsubscribes.
    private void HandleRigChanged(SkinnedMeshRenderer _) => _ownHeadPiecesDirty = true;
    private void HandleShapeChanged(string _, float __) => _ownHeadPiecesDirty = true;

    private void LateUpdate()
    {
        if (!_ownHeadPiecesDirty) return;
        _ownHeadPiecesDirty = false;
        PrepareOwnHeadPieces();
    }

    private void OnDestroy()
    {
        if (!_subscribedToOutfitSystem || outfitSystem == null) return;
        outfitSystem.OnRigChanged -= HandleRigChanged;
        outfitSystem.OnShapeChanged -= HandleShapeChanged;
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

        QueueApply(next, isOwnerBuild: false);
    }

    private async Task RunRemoteApply(byte[] packed)
    {
        try
        {
            await CharacterLoadout.ApplyPacked(outfitSystem, packed, registry);
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[PlayerAppearance] Remote appearance apply failed for {gameObject.name}. {e}");
        }
    }

    // Bytes staged by the Main Menu / Character Creator (either format), or the
    // registry default for a first-launch player with no saved character.
    private byte[] LoadLocalLoadout()
    {
        var packed = LocalPlayerLoadout.Instance?.PendingLoadoutData;
        if (packed == null || packed.Length == 0)
            return CharacterLoadout.Pack(CharacterLoadout.BuildDefaultLoadout(registry));

        return packed;
    }
}