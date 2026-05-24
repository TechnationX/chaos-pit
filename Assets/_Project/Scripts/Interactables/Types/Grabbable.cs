// Grabbable.cs

using FishNet.Component.Transforming;
using FishNet.Connection;
using FishNet.Object;
using UnityEngine;

public class Grabbable : NetworkBehaviour, IInteractable
{
    [Header("Grabbable Settings")]
    [SerializeField] private string _promptLabel = "Grab";
    [SerializeField] private string _dropPromptLabel = "Drop";

    protected Rigidbody _rigidbody;
    protected PlayerObject _holdingPlayer;

    private readonly FishNet.Object.Synchronizing.SyncVar<bool> _isHeldSync = new FishNet.Object.Synchronizing.SyncVar<bool>();
    protected bool _isHeld
    {
        get => _isHeldSync.Value;
        set => _isHeldSync.Value = value;
    }

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
        Debug.Log($"[Grabbable] OnInteract — IsServer: {IsServerInitialized}, IsClient: {IsClientInitialized}, IsHeld: {_isHeld}");

        if (_isHeld)
        {
            if (player.IsHoldingObject && player.HeldObject == this)
            {
                ServerDropRpc(player);
                Debug.Log($"[Grabbable] OnInteract DropRpc");
            }
            return;
        }


        // Only one object held at a time — check player hand
        if (player.IsHoldingObject) return;
        ServerGrab(player);
    }

    [ServerRpc(RequireOwnership = false)]
    protected virtual void ServerDropRpc(PlayerObject player)
    {
        if (!_isHeld || _holdingPlayer != player) return;
        ServerDrop();
    }

    [ServerRpc(RequireOwnership = false)]
    protected virtual void ServerGrab(PlayerObject player)
    {
        Debug.Log($"[Grabbable] ServerGrab called — IsHeld: {_isHeld}, Player: {player?.name}");
        if (_isHeld) return;
        if (player.ServerIsHoldingObject) return;

        _isHeld = true;
        _holdingPlayer = player;
        player.SetServerHeldObject(this);

        foreach (var conn in NetworkObject.Observers)
        {
            Debug.Log($"[Grabbable] Observer: {conn.ClientId}");
        }
        Debug.Log($"[Grabbable] Grabbing player connection: {player.Owner?.ClientId}");

        ObserversGrab(player.NetworkObject);
    }

    [Server]
    protected virtual void ServerDrop()
    {
        Debug.Log($"[Grabbable] ServerDrop — IsHeld before: {_isHeld}");
        if (!_isHeld) return;

        _isHeld = false;
        Debug.Log($"[Grabbable] ServerDrop — IsHeld after: {_isHeld}");

        PlayerObject prevPlayer = _holdingPlayer;
        _holdingPlayer = null;
        prevPlayer.SetServerHeldObject(null);

        ObserversDrop(prevPlayer.NetworkObject);
    }

    [ObserversRpc]
    protected void ObserversGrab(NetworkObject playerNetObj)  // NOT virtual
    {
        OnObserversGrab(playerNetObj);  // call virtual hook
    }

    protected virtual void OnObserversGrab(NetworkObject playerNetObj)
    {
        Debug.Log($"[Grabbable] ObserversGrab fired on client");
        PlayerObject player = playerNetObj.GetComponent<PlayerObject>();
        if (player == null) return;

        _holdingPlayer = player;
        _rigidbody.isKinematic = true;

        var nt = GetComponent<NetworkTransform>();
        if (nt != null) nt.enabled = false;

        Transform handSocket = player.HandSocket;
        if (handSocket != null)
        {
            transform.SetParent(handSocket);
            transform.localPosition = Vector3.zero;
            transform.localRotation = Quaternion.identity;
        }

        player.SetHeldObject(this);
        //Debug.Log($"ObserversGrab called, SetHeldObject on {player.name}");
    }

    [ObserversRpc]
    protected void ObserversDrop(NetworkObject playerNetObj)  // NOT virtual
    {
        OnObserversDrop(playerNetObj);
    }

    protected virtual void OnObserversDrop(NetworkObject playerNetObj)
    {
        PlayerObject player = playerNetObj.GetComponent<PlayerObject>();
        if (player == null) return;

        _holdingPlayer = player;

        var nt = GetComponent<NetworkTransform>();
        if (nt != null) nt.enabled = true;

        _rigidbody.isKinematic = false;
        transform.SetParent(null);
        player.SetHeldObject(null);
        //Debug.Log($"ObserversDrop called, cleared held object");
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