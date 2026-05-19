// Throwable.cs

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
        if (!IsServerInitialized) return;

        if (_isHeld && _holdingPlayer == player)
        {
            // Already held — drop on left click
            ServerDrop();
            return;
        }

        if (!_isHeld)
            ServerGrab(player);
    }

    private void Update()
    {
        if (!_isHeld || _holdingPlayer == null) return;
        if (!_holdingPlayer.IsOwner) return;

        // Right click to throw while held
        if (Mouse.current != null && Mouse.current.rightButton.wasPressedThisFrame)
            ServerThrow(_holdingPlayer);
    }

    [ServerRpc(RequireOwnership = false)]
    private void ServerThrow(PlayerObject player)
    {
        if (!_isHeld || _holdingPlayer != player) return;

        _isHeld = false;
        PlayerObject prevPlayer = _holdingPlayer;
        _holdingPlayer = null;

        Vector3 throwDirection = player.transform.forward;

        RemoveOwnership();
        ObserversThrow(prevPlayer.NetworkObject, throwDirection);
    }

    [ObserversRpc]
    private void ObserversThrow(NetworkObject playerNetObj, Vector3 direction)
    {
        PlayerObject player = playerNetObj.GetComponent<PlayerObject>();

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

    protected override void ObserversGrab(NetworkObject playerNetObj)
    {
        PlayerObject player = playerNetObj.GetComponent<PlayerObject>();
        if (player == null) return;

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

    protected override void ObserversDrop(NetworkObject playerNetObj)
    {
        PlayerObject player = playerNetObj.GetComponent<PlayerObject>();
        if (player == null) return;

        _rigidbody.isKinematic = false;
        transform.SetParent(null);
        player.SetHeldObject(null);
        _readyToThrow = false;
    }
}