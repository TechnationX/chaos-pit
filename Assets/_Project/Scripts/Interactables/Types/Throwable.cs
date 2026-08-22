// Throwable.cs

using FishNet.Component.Transforming;
using FishNet.Object;
using UnityEngine;
using UnityEngine.InputSystem;

public class Throwable : Grabbable
{
    [Header("Throwable Settings")]
    [SerializeField] private float _throwForce = 10f;
    [SerializeField] private string _throwPromptLabel = "Throw [RMB]";

    private bool _readyToThrow;

    public override void OnInteract(PlayerObject player)
    {
        if (_isHeld)
        {
            if (player.IsHoldingObject && player.HeldObject == this)
            {
                ServerDropRpc(player);
                //Debug.Log($"[Throwabble] OnInteract Drop");
            }
            return;
        }

        if (!_isHeld && !player.IsHoldingObject)
            ServerGrab(player);
    }

    // Was a plain `private void Update()` — changed to `protected override`
    // calling base.Update() so Grabbable's per-frame held-state polling
    // (ApplyHeldVisualState) still runs for Throwable instances instead of
    // being hidden by this override.
    protected override void Update()
    {
        base.Update();

        if (!_isHeld || _holdingPlayer == null) return;
        //Debug.Log($"[Throwable] Update — IsOwner: {IsOwner}, holdingPlayer.IsOwner: {_holdingPlayer.IsOwner}");
        if (!_holdingPlayer.IsOwner) return;

        // Right click to throw while held
        if (Mouse.current != null && Mouse.current.rightButton.wasPressedThisFrame)
        {
            Vector3 aimDirection = transform.forward; // object's current world forward, following HandSocketPivot's pitch
            ServerThrow(_holdingPlayer, aimDirection);
        }
    }

    [ServerRpc(RequireOwnership = false)]
    protected override void ServerGrab(PlayerObject player)
    {
        if (_isHeld) return;
        if (player.ServerIsHoldingObject) return;

        _isHeld = true;
        _holdingPlayer = player;
        player.SetServerHeldObject(this);

        _holdingPlayerNetObjSync.Value = player.NetworkObject;
    }

    [ServerRpc(RequireOwnership = false)]
    protected override void ServerDropRpc(PlayerObject player)
    {
        if (!_isHeld || _holdingPlayer != player) return;
        ServerDrop();
    }

    [Server]
    protected override void ServerDrop()
    {
        if (!_isHeld) return;

        _isHeld = false;

        // Deliberately NOT nulling _holdingPlayer here — see the comment on
        // Grabbable.ServerDrop(). OnObserversDrop() (called via Update()'s
        // poll, which also runs on host) still needs it.
        _holdingPlayer.SetServerHeldObject(null);

        _holdingPlayerNetObjSync.Value = null;
    }

    [ServerRpc(RequireOwnership = false)]
    private void ServerThrow(PlayerObject player, Vector3 throwDirection)
    {
        if (!_isHeld || _holdingPlayer != player) return;

        _isHeld = false;

        // Deliberately NOT nulling _holdingPlayer here — see the comment on
        // Grabbable.ServerDrop(). OnObserversThrow() (invoked via the
        // ObserversRpc below, which also runs on host) still needs
        // _holdingPlayer to resolve who to detach from; it clears the field
        // itself once done.
        _holdingPlayer.SetServerHeldObject(null);

        // Also clear the holding-player SyncVar here (same as a normal
        // drop) — a throw ends the "held" state too, so leaving it pointed
        // at the thrower would misrepresent state for a client that joins
        // later while this object is lying wherever it landed.
        _holdingPlayerNetObjSync.Value = null;

        ObserversThrow(_holdingPlayer.NetworkObject, throwDirection.normalized);
    }

    [ObserversRpc]
    protected void ObserversThrow(NetworkObject playerNetObj, Vector3 direction)
    {
        //Debug.Log($"[Throwable] ObserversThrow RPC received on client");
        OnObserversThrow(playerNetObj, direction);
    }

    protected void OnObserversThrow(NetworkObject playerNetObj, Vector3 direction)
    {
        // Use the cached _holdingPlayer instead of re-resolving playerNetObj
        // — same reasoning as OnObserversDrop below.
        PlayerObject player = _holdingPlayer;

        var nt = GetComponent<NetworkTransform>();
        if (nt != null) nt.enabled = true;

        // Detach from hand
        transform.SetParent(null);

        // Re-enable physics
        _rigidbody.isKinematic = false;

        // Apply throw force
        _rigidbody.AddForce(direction * _throwForce, ForceMode.Impulse);

        // Clear held object
        if (player != null)
            player.SetHeldObject(null);

        _holdingPlayer = null;
        _readyToThrow = false;
    }

    protected override void OnObserversGrab(NetworkObject playerNetObj)
    {
        PlayerObject player = playerNetObj.GetComponent<PlayerObject>();
        //Debug.Log($"[Throwable] OnObserversGrab — player null: {player == null}");
        if (player == null) return;

        _holdingPlayer = player;

        var nt = GetComponent<NetworkTransform>();
        if (nt != null) nt.enabled = false;

        _rigidbody.isKinematic = true;

        Transform handSocket = player.HandSocket;
        if (handSocket != null)
        {
            transform.SetParent(handSocket);
            transform.localPosition = Vector3.zero;
            transform.localRotation = Quaternion.identity;
        }

        player.SetHeldObject(this);
        _readyToThrow = true;
    }

    protected override void OnObserversDrop(NetworkObject playerNetObj)
    {
        // Use the cached _holdingPlayer instead of re-resolving playerNetObj
        // — same reasoning as Grabbable.OnObserversDrop.
        PlayerObject player = _holdingPlayer;
        if (player == null) return;

        var nt = GetComponent<NetworkTransform>();
        if (nt != null) nt.enabled = true;

        _rigidbody.isKinematic = false;
        transform.SetParent(null);
        player.SetHeldObject(null);
        _holdingPlayer = null;
        _readyToThrow = false;
    }
}