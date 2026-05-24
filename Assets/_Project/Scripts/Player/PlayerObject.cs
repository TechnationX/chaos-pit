// PlayerObject.cs

using FishNet.Example.ColliderRollbacks;
using FishNet.Object;
using FishNet.Connection;
using UnityEngine;

public class PlayerObject : NetworkBehaviour
{
    [Header("Sub-System References")]
    [SerializeField] private PlayerMovement _playerMovement;
    [SerializeField] private PlayerCamera _playerCamera;
    [SerializeField] private InteractionManager _interactionManager;

    [Header("Model Reference")]
    [SerializeField] private GameObject _characterModel;

    [Header("Player Data")]
    public string PlayerName { get; private set; }
    public int PlayerId { get; private set; }

    [Header("Sockets")]
    [SerializeField] private Transform _handSocket;
    public Transform HandSocket => _handSocket;

    [Header("Transforms")]
    [SerializeField] private Transform _cameraRoot;
    public Transform CameraRoot => _cameraRoot;

    // Sub-system accessors
    public PlayerMovement Movement => _playerMovement;
    public PlayerCamera Camera => _playerCamera;
    public InteractionManager Interaction => _interactionManager;
    public GameObject CharacterModel => _characterModel;

    [Header("Animation")]
    private Animator _animator;
    public Animator CharacterAnimator => _animator;

    private bool _initialized = false;

    private Grabbable _heldObject;
    private Grabbable _serverHeldObject;
    public bool ServerIsHoldingObject => _serverHeldObject != null;
    public bool IsHoldingObject => _heldObject != null;
    public Grabbable HeldObject => _heldObject;

    public override void OnStartClient()
    {
        base.OnStartClient();
        //Debug.Log($"OnStartClient fired. IsOwner: {IsOwner}");

        _playerMovement.enabled = false;
        _playerCamera.enabled = false;
        _interactionManager.enabled = false;
    }

    public override void OnOwnershipClient(NetworkConnection prevOwner)
    {
        base.OnOwnershipClient(prevOwner);
        //Debug.Log($"OnOwnershipClient fired. IsOwner: {IsOwner}");

        if (!IsOwner || _initialized) return;

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
    }

    // Called by external systems to set identity data
    public void SetPlayerData(string playerName, int playerId)
    {
        PlayerName = playerName;
        PlayerId = playerId;
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
}