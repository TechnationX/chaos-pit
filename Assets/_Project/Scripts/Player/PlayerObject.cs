// PlayerObject.cs

using FishNet.Example.ColliderRollbacks;
using FishNet.Object;
using FishNet.Connection;
using UnityEngine;
using UnityEngine.Rendering;
using FishNet.Object.Synchronizing;
using Unity.Services.Vivox;

public class PlayerObject : NetworkBehaviour
{
    [Header("Sub-System References")]
    [SerializeField] private PlayerMovement _playerMovement;
    [SerializeField] private PlayerCamera _playerCamera;
    [SerializeField] private InteractionManager _interactionManager;
    [SerializeField] private PlayerAppearance _playerAppearance;

    [Header("Model Reference")]
    [SerializeField] private GameObject _characterModel;

    // Character mesh whose shadow-casting gets toggled off while a player
    // sits on a floating "elimination holder" platform (see SetShadowCasting
    // below) — those platforms already have their own geometry set to not
    // cast shadows, but the player standing on one still did.
    private SkinnedMeshRenderer _characterRenderer;

    // Purely visual, but every client renders its own local copy of every
    // other player, so this has to be synced rather than set once wherever
    // the teleport happens — same reasoning JinxedPlayerEffect documents for
    // the jinx tint, just backed by a SyncVar here instead of an ObserversRpc
    // so a late-joining/reconnecting client also picks up the correct value.
    private readonly SyncVar<bool> _castShadows = new SyncVar<bool>(true);

    [Header("Player Data")]
    private readonly SyncVar<int> _playerId = new SyncVar<int>();
    private readonly SyncVar<string> _playerName = new SyncVar<string>();
    public int PlayerId => _playerId.Value;
    public string PlayerName => _playerName.Value;
    private void OnPlayerDataChanged(int prev, int next, bool asServer) { }

    [Header("Sockets")]
    [SerializeField] private Transform _handSocket;
    public Transform HandSocket => _handSocket;

    [SerializeField] private Transform _handSocketStatic;
    public Transform HandSocketStatic => _handSocketStatic;

    [Header("Transforms")]
    [SerializeField] private Transform _cameraRoot;
    public Transform CameraRoot => _cameraRoot;

    // Sub-system accessors
    public PlayerMovement Movement => _playerMovement;
    public PlayerCamera Camera => _playerCamera;
    public InteractionManager Interaction => _interactionManager;
    public PlayerAppearance Appearance => _playerAppearance;
    public GameObject CharacterModel => _characterModel;

    [Header("Animation")]
    private Animator _animator;
    public Animator CharacterAnimator => _animator;

    private bool _initialized = false;
    private bool _hasStartedClient = false;

    private Grabbable _heldObject;
    private Grabbable _serverHeldObject;
    public bool ServerIsHoldingObject => _serverHeldObject != null;
    public bool IsHoldingObject => _heldObject != null;
    public Grabbable HeldObject => _heldObject;

    private Vector3 _lastPosition;

    private void Update()
    {
        if (transform.position != _lastPosition)
        {
            //Debug.Log($"[PlayerObject] Position changed to {transform.position} — frame: {Time.frameCount}");
            _lastPosition = transform.position;
        }

        // Reports this player's position into both persistent Vivox
        // channels every frame so positional audio falloff/panning tracks
        // movement — mirrors how _lastPosition above is already a
        // per-frame local check. Only the owner's own client needs to
        // report its own position (Vivox handles broadcasting it to
        // everyone else listening in-channel), so this is gated the same
        // way _playerMovement/_playerCamera are elsewhere in this class.
        if (IsOwner && VoiceChatManager.Instance != null && VoiceChatManager.Instance.IsLoggedIn)
        {
            VivoxService.Instance.Set3DPosition(gameObject, VoiceChatManager.LobbyProximityChannel);
            VivoxService.Instance.Set3DPosition(gameObject, VoiceChatManager.StageBroadcastChannel);
        }
    }

    public override void OnStartClient()
    {
        base.OnStartClient();

        // Diagnostic for the intermittent "client hangs on join, stuck on
        // skybox" bug — confirms whether this connection's OWN player
        // object ever actually starts on the client at all. If this never
        // logs for the local player after a hung join, the spawn message
        // itself never reached the client even though the server logged a
        // successful spawn (see LobbySpawner.TrySpawnIfReady's own log) —
        // pointing at a network delivery/channel problem rather than
        // anything in this class. See OnOwnershipClient/
        // TryInitializeAsLocalOwner below for the rest of this path.
        Debug.Log($"[PlayerObject] OnStartClient — ObjectId: {ObjectId}, IsOwner: {IsOwner}, alreadyInitialized: {_initialized}");

        _hasStartedClient = true;

        // Only default these to disabled if we haven't already initialized
        // as the local owner — see TryInitializeAsLocalOwner() below for
        // why. FishNet doesn't guarantee OnOwnershipClient fires AFTER
        // OnStartClient for an object spawned already-owned by this
        // connection (exactly how the player prefab spawns) — when
        // ownership happens to be assigned first, OnOwnershipClient
        // already enabled and initialized everything, and this
        // unconditional disable used to stomp that with nothing left to
        // ever turn it back on. That was intermittent (a network-timing
        // race, not a fixed order) and looked like "player joined but
        // their own camera/movement never came on."
        if (!_initialized)
        {
            _playerMovement.enabled = false;
            _playerCamera.enabled = false;
            _interactionManager.enabled = false;
        }

        _characterRenderer = _characterModel != null
            ? _characterModel.GetComponentInChildren<SkinnedMeshRenderer>()
            : null;

        _castShadows.OnChange += OnCastShadowsChanged;
        ApplyShadowCasting(_castShadows.Value);

        TryInitializeAsLocalOwner();
    }

    private void OnCastShadowsChanged(bool prev, bool next, bool asServer)
    {
        ApplyShadowCasting(next);
    }

    private void ApplyShadowCasting(bool castShadows)
    {
        if (_characterRenderer != null)
            _characterRenderer.shadowCastingMode = castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
    }

    // Server-only. Call when moving a player onto/off of a floating
    // elimination-holder platform (BombToss/Jinxed/LastOneStanding) whose
    // own geometry already has shadow-casting disabled.
    public void SetShadowCasting(bool enabled)
    {
        _castShadows.Value = enabled;
    }

    public override void OnOwnershipClient(NetworkConnection prevOwner)
    {
        base.OnOwnershipClient(prevOwner);

        // Diagnostic — see OnStartClient's comment. This and OnStartClient
        // are the two callbacks that gate TryInitializeAsLocalOwner, and
        // FishNet doesn't guarantee their order (that's the whole reason
        // TryInitializeAsLocalOwner exists) — logging both separately shows
        // which one actually fires for a hung client, if either does.
        Debug.Log($"[PlayerObject] OnOwnershipClient — ObjectId: {ObjectId}, IsOwner: {IsOwner}");

        TryInitializeAsLocalOwner();
    }

    // Runs the local-owner init exactly once, the first time BOTH
    // OnStartClient has fired AND ownership is confirmed ours — regardless
    // of which of the two callbacks happens to fire first. See the comment
    // on OnStartClient for why relying on a fixed order between them was
    // the actual bug.
    private void TryInitializeAsLocalOwner()
    {
        if (_initialized || !_hasStartedClient || !IsOwner)
        {
            // Only worth logging the "still waiting" case for a connection
            // that actually owns this object — every other client's copy of
            // every OTHER player also calls this and will always fail the
            // IsOwner check, which would otherwise spam the console for
            // every player in the room.
            if (IsOwner && !_initialized)
                Debug.Log($"[PlayerObject] TryInitializeAsLocalOwner — ObjectId: {ObjectId} is ours but not ready yet (hasStartedClient: {_hasStartedClient}).");
            return;
        }

        Debug.Log($"[PlayerObject] TryInitializeAsLocalOwner — ObjectId: {ObjectId} initializing local player now.");

        _initialized = true;
        _playerMovement.enabled = true;
        _playerCamera.enabled = true;
        _interactionManager.enabled = true;

        Initialize();
    }

    private void Initialize()
    {
        // Debug.Log("Initialize called on PlayerObject");
        _animator = _characterModel.GetComponentInChildren<Animator>();
        _playerMovement.Initialize(this);
        _playerCamera.Initialize(this);
        _interactionManager.Initialize(this);

        // Kicks off Vivox login + persistent channel joins for the local
        // owner only, once, right after this player is fully ready — see
        // VoiceChatManager.EnsureVoiceReadyAsync for why it's safe to fire
        // this here without worrying about double-init.
        if (VoiceChatManager.Instance != null)
            _ = VoiceChatManager.Instance.EnsureVoiceReadyAsync();
    }

    // Called by external systems to set identity data
    public void SetPlayerData(string playerName, int playerId)
    {
        _playerName.Value = playerName;
        _playerId.Value = playerId;
    }

    // Called by PlayerProfileManager.SetDisplayName whenever the player's
    // real display name becomes known/changes, so this SyncVar doesn't stay
    // stuck on the "Player_<id>" placeholder SetPlayerData was called with
    // at spawn time (see PlayerProfileManager.SetDisplayName for why that
    // placeholder is unavoidable at spawn). Leaves _playerId untouched,
    // unlike SetPlayerData.
    public void SetPlayerName(string playerName)
    {
        _playerName.Value = playerName;
    }

    public void SetHeldObject(Grabbable obj)
    {
        //Debug.Log($"SetHeldObject called: {obj?.name ?? "null"}");
        _heldObject = obj;
    }

    public void SetServerHeldObject(Grabbable obj)
    {
        _serverHeldObject = obj;
    }

    public void ReinitializeCamera()
    {
        _playerCamera.Initialize(this);
    }
}