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

    private void Update()
    {
        if (!_isHeld || _holdingPlayer == null) return;
        //Debug.Log($"[Throwable] Update — IsOwner: {IsOwner}, holdingPlayer.IsOwner: {_holdingPlayer.IsOwner}");
        if (!_holdingPlayer.IsOwner) return;

        // Right click to throw while held
        if (Mouse.current != null && Mouse.current.rightButton.wasPressedThisFrame)
            ServerThrow(_holdingPlayer);
    }

    [ServerRpc(RequireOwnership = false)]
    protected override void ServerGrab(PlayerObject player)
    {
        if (_isHeld) return;
        if (player.ServerIsHoldingObject) return;

        _isHeld = true;
        _holdingPlayer = player;
        player.SetServerHeldObject(this);

        foreach (var conn in NetworkObject.Observers)
        {
            //Debug.Log($"[Throwable] Observer clientId: {conn.ClientId}");
        }

        ObserversGrab(player.NetworkObject);
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
        PlayerObject prevPlayer = _holdingPlayer;
        _holdingPlayer = null;
        prevPlayer.SetServerHeldObject(null);

        ObserversDrop(prevPlayer.NetworkObject); 
    }

    [ServerRpc(RequireOwnership = false)]
    private void ServerThrow(PlayerObject player)
    {
        //Debug.Log($"[Throwable] ServerThrow called on server");
        if (!_isHeld || _holdingPlayer != player) return;

        foreach (var conn in NetworkObject.Observers)
            Debug.Log($"[Throwable] Throw observer clientId: {conn.ClientId}");

        _isHeld = false;
        PlayerObject prevPlayer = _holdingPlayer;
        _holdingPlayer = null;
        prevPlayer.SetServerHeldObject(null);

        Vector3 throwDirection = player.transform.forward;

        ObserversThrow(prevPlayer.NetworkObject, throwDirection);
    }

    [ObserversRpc]
    protected void ObserversThrow(NetworkObject playerNetObj, Vector3 direction)
    {
        //Debug.Log($"[Throwable] ObserversThrow RPC received on client");
        OnObserversThrow(playerNetObj, direction);
    }

    protected void OnObserversThrow(NetworkObject playerNetObj, Vector3 direction)
    {
        PlayerObject player = playerNetObj.GetComponent<PlayerObject>();

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
        PlayerObject player = playerNetObj.GetComponent<PlayerObject>();
        if (player == null) return;

        var nt = GetComponent<NetworkTransform>();
        if (nt != null) nt.enabled = true;

        _rigidbody.isKinematic = false;
        transform.SetParent(null);
        player.SetHeldObject(null);
        _readyToThrow = false;
    }
}