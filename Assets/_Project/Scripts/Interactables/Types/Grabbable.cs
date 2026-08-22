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

    [Header("Impact SFX")]
    [SerializeField] private AudioClip[] _impactClips;
    [SerializeField] private float _minImpactVelocity = 2f;
    [SerializeField] private float _impactCooldown = 0.2f;

    private float _lastImpactTime = -999f;

    protected Rigidbody _rigidbody;
    protected PlayerObject _holdingPlayer;

    private readonly FishNet.Object.Synchronizing.SyncVar<bool> _isHeldSync = new FishNet.Object.Synchronizing.SyncVar<bool>();
    protected bool _isHeld
    {
        get => _isHeldSync.Value;
        set => _isHeldSync.Value = value;
    }

    // Carries WHO is holding this object to every peer. Deliberately a
    // SyncVar rather than an ObserversRpc parameter — passing a
    // NetworkObject reference as an RPC argument doesn't reliably resolve
    // on remote clients (no guarantee the target is already known locally
    // at the moment the RPC is processed), whereas SyncVars holding
    // NetworkObject/NetworkBehaviour references are resolved by FishNet
    // itself, including deferring delivery until the reference is
    // resolvable. Same reasoning that makes _isHeldSync above reliable.
    protected readonly FishNet.Object.Synchronizing.SyncVar<NetworkObject> _holdingPlayerNetObjSync = new FishNet.Object.Synchronizing.SyncVar<NetworkObject>();

    // Tracks what we've already visually applied, so ApplyHeldVisualState()
    // below can skip redundant re-application when called more than once
    // for the same underlying state.
    private NetworkObject _lastAppliedHolder;

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

    // Polling instead of reacting to _holdingPlayerNetObjSync.OnChange — the
    // event-driven version went through three iterations and still didn't
    // apply reliably on the host's own observer pass (grab visuals applied,
    // drop visuals never did, confirmed via debug logging). Every peer just
    // checks once a frame whether the synced value matches what it's
    // currently showing and self-corrects if not — same "read the SyncVar's
    // current value directly" principle _isHeld already relies on at
    // interaction time, just running continuously instead of on-demand.
    // Negligible cost for the small number of grabbable props in this game.
    protected virtual void Update()
    {
        ApplyHeldVisualState();
    }

    public virtual void OnInteract(PlayerObject player)
    {
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
        if (_isHeld) return;
        if (player.ServerIsHoldingObject) return;

        _isHeld = true;
        _holdingPlayer = player;
        player.SetServerHeldObject(this);

        _holdingPlayerNetObjSync.Value = player.NetworkObject;
    }

    [Server]
    protected virtual void ServerDrop()
    {
        if (!_isHeld) return;

        _isHeld = false;

        // Deliberately NOT nulling _holdingPlayer here (used to). On host,
        // this [Server]-only method and the observer-side OnObserversDrop()
        // run in the same process against the same object — OnObserversDrop()
        // runs later (once Update()'s poll below detects the SyncVar change)
        // and needs _holdingPlayer to still be valid to know what to detach
        // from. Nulling it here was clearing it out from under host's own
        // drop visuals before they ever ran, which is why drop looked correct
        // on remote clients but silently no-op'd on host. OnObserversDrop()
        // clears _holdingPlayer itself once it's done using it — that's the
        // only place this should happen now.
        _holdingPlayer.SetServerHeldObject(null);

        _holdingPlayerNetObjSync.Value = null;
    }

    // Reads _holdingPlayerNetObjSync's CURRENT value directly every frame
    // (called from Update() above) rather than reacting to an OnChange
    // event, with the dedupe guard so we only actually apply a transition
    // once per real change.
    private void ApplyHeldVisualState()
    {
        NetworkObject currentHolder = _holdingPlayerNetObjSync.Value;
        if (currentHolder == _lastAppliedHolder) return;

        _lastAppliedHolder = currentHolder;

        if (currentHolder != null)
            OnObserversGrab(currentHolder);
        else
            OnObserversDrop(null);
    }

    protected virtual void OnObserversGrab(NetworkObject playerNetObj)
    {
        //Debug.Log($"[Grabbable] ObserversGrab fired on client");
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

    protected virtual void OnObserversDrop(NetworkObject playerNetObj)
    {
        // Use the already-cached _holdingPlayer (set in OnObserversGrab)
        // instead of re-resolving playerNetObj.GetComponent<PlayerObject>()
        // — that's the same kind of NetworkObject-reference lookup that
        // wasn't reliable for grab, so don't repeat it here when we already
        // have a known-good reference from when this was grabbed.
        PlayerObject player = _holdingPlayer;
        if (player == null) return;

        var nt = GetComponent<NetworkTransform>();
        if (nt != null) nt.enabled = true;

        _rigidbody.isKinematic = false;
        transform.SetParent(null);
        player.SetHeldObject(null);
        _holdingPlayer = null;
        //Debug.Log($"ObserversDrop called, cleared held object");
    }

    // Called by lobby bounds system if object leaves play area, and by the
    // pool table's reset buttons (LobbySpawner.SwitchPoolPattern) to put the
    // rack back at its spawn point. Setting transform.position/rotation here
    // only updates the server's own copy of the Transform — relying on
    // NetworkTransform to auto-sync a big instant jump like this to clients
    // isn't reliable (same gap as the "Enable Teleport" note for ball
    // NetworkTransforms), so this also explicitly broadcasts the new
    // transform to every client, same pattern as
    // CueBall.ServerResetTo/ObserversResetTo.
    public void ForceReset()
    {
        if (IsServerInitialized)
        {
            ServerDrop();
            transform.position = _originalPosition;
            transform.rotation = _originalRotation;
            ObserversForceReset(_originalPosition, _originalRotation);
        }
    }

    [ObserversRpc]
    private void ObserversForceReset(Vector3 position, Quaternion rotation)
    {
        transform.position = position;
        transform.rotation = rotation;
    }

    protected virtual void OnCollisionEnter(Collision collision)
    {
        if (_isHeld) return; // don't play impact SFX while being carried (bumping walls etc.)
        if (_impactClips == null || _impactClips.Length == 0) return;
        if (Time.time - _lastImpactTime < _impactCooldown) return;

        float impactSpeed = collision.relativeVelocity.magnitude;
        if (impactSpeed < _minImpactVelocity) return;

        _lastImpactTime = Time.time;

        AudioClip clip = _impactClips[Random.Range(0, _impactClips.Length)];
        float volume = Mathf.Clamp01(impactSpeed / 10f); // harder hits = louder, capped at 1

        AudioManager.Instance?.PlaySFXVaried(clip, 0.05f);
    }
}