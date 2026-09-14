// PlayerObject.cs

using FishNet.Example.ColliderRollbacks;
using FishNet.Object;
using FishNet.Connection;
using UnityEngine;
using UnityEngine.Rendering;
using FishNet.Object.Synchronizing;

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
    }

    public override void OnStartClient()
    {
        base.OnStartClient();
        Debug.Log($"[DIAGNOSTIC] OnStartClient — obj: {gameObject.name}, IsOwner: {IsOwner}, Owner.ClientId: {(Owner != null ? Owner.ClientId : -1)}, _initialized(before): {_initialized}");

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
        Debug.Log($"[DIAGNOSTIC] OnOwnershipClient — obj: {gameObject.name}, IsOwner: {IsOwner}, Owner.ClientId: {(Owner != null ? Owner.ClientId : -1)}, prevOwner.ClientId: {(prevOwner != null ? prevOwner.ClientId : -1)}, _hasStartedClient: {_hasStartedClient}, _initialized(before): {_initialized}");

        TryInitializeAsLocalOwner();
    }

    // Runs the local-owner init exactly once, the first time BOTH
    // OnStartClient has fired AND ownership is confirmed ours — regardless
    // of which of the two callbacks happens to fire first. See the comment
    // on OnStartClient for why relying on a fixed order between them was
    // the actual bug.
    private void TryInitializeAsLocalOwner()
    {
        Debug.Log($"[DIAGNOSTIC] TryInitializeAsLocalOwner called — obj: {gameObject.name}, _initialized: {_initialized}, _hasStartedClient: {_hasStartedClient}, IsOwner: {IsOwner}");

        if (_initialized || !_hasStartedClient || !IsOwner)
        {
            Debug.Log($"[DIAGNOSTIC] TryInitializeAsLocalOwner — early-return (guard failed) on obj: {gameObject.name}");
            return;
        }

        _initialized = true;
        _playerMovement.enabled = true;
        _playerCamera.enabled = true;
        _interactionManager.enabled = true;

        Debug.Log($"[DIAGNOSTIC] TryInitializeAsLocalOwner — guard passed, enabling subsystems and calling Initialize() on obj: {gameObject.name}. _playerMovement null? {_playerMovement == null}, _playerCamera null? {_playerCamera == null}, _interactionManager null? {_interactionManager == null}");

        Initialize();

        Debug.Log($"[DIAGNOSTIC] TryInitializeAsLocalOwner — Initialize() returned on obj: {gameObject.name}");
    }

    private void Initialize()
    {
        // Debug.Log("Initialize called on PlayerObject");
        _animator = _characterModel.GetComponentInChildren<Animator>();
        _playerMovement.Initialize(this);
        _playerCamera.Initialize(this);
        _interactionManager.Initialize(this);
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