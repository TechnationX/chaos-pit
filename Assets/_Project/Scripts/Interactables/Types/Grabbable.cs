// Grabbable.cs

using UnityEngine;
using FishNet.Object;
using FishNet.Connection;

public class Grabbable : NetworkBehaviour, IInteractable
{
    [Header("Grabbable Settings")]
    [SerializeField] private string _promptLabel = "Grab";
    [SerializeField] private string _dropPromptLabel = "Drop";

    protected Rigidbody _rigidbody;
    protected PlayerObject _holdingPlayer;
    protected bool _isHeld;

    private Vector3 _originalPosition;
    private Quaternion _originalRotation;

    public string PromptLabel => _isHeld && _holdingPlayer != null ? _dropPromptLabel : _promptLabel;
    public bool IsHeld => _isHeld;
    public PlayerObject HoldingPlayer => _holdingPlayer;

    protected virtual void Awake()
    {
        _rigidbody = GetComponent<Rigidbody>();
        _originalPosition = transform.position;
        _originalRotation = transform.rotation;
    }

    public virtual void OnInteract(PlayerObject player)
    {
        Debug.Log($"OnInteract called on {gameObject.name}, IsHeld: {_isHeld}");
        if (!IsServerInitialized) return;

        if (_isHeld)
        {
            if (_holdingPlayer == player)
                ServerDrop();
            return;
        }

        // Only one object held at a time — check player hand
        ServerGrab(player);
    }

    [ServerRpc(RequireOwnership = false)]
    protected virtual void ServerGrab(PlayerObject player)
    {
        Debug.Log($"ServerGrab called for {gameObject.name}");
        if (_isHeld) return;

        _isHeld = true;
        _holdingPlayer = player;

        GiveOwnership(player.Owner);
        ObserversGrab(player.NetworkObject);
    }

    [Server]
    protected virtual void ServerDrop()
    {
        if (!_isHeld) return;

        _isHeld = false;
        PlayerObject prevPlayer = _holdingPlayer;
        _holdingPlayer = null;

        // Return ownership to server
        RemoveOwnership();

        ObserversDrop(prevPlayer.NetworkObject);
    }

    [ObserversRpc]
    protected virtual void ObserversGrab(NetworkObject playerNetObj)
    {
        PlayerObject player = playerNetObj.GetComponent<PlayerObject>();
        if (player == null) return;

        _holdingPlayer = player;
        _rigidbody.isKinematic = true;

        Transform handSocket = player.HandSocket;
        if (handSocket != null)
        {
            transform.SetParent(handSocket);
            transform.localPosition = Vector3.zero;
            transform.localRotation = Quaternion.identity;
        }

        player.SetHeldObject(this);
        Debug.Log($"ObserversGrab called, SetHeldObject on {player.name}");
    }

    [ObserversRpc]
    protected virtual void ObserversDrop(NetworkObject playerNetObj)
    {
        PlayerObject player = playerNetObj.GetComponent<PlayerObject>();
        if (player == null) return;

        _rigidbody.isKinematic = false;
        transform.SetParent(null);
        player.SetHeldObject(null);
        Debug.Log($"ObserversDrop called, cleared held object");
    }

    // Called by lobby bounds system if object leaves play area
    public void ForceReset()
    {
        if (IsServerInitialized)
        {
            ServerDrop();
            transform.position = _originalPosition;
            transform.rotation = _originalRotation;
        }
    }
}