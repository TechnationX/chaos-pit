// Pushable.cs

using UnityEngine;
using FishNet.Object;
using FishNet.Object.Synchronizing;

public class Pushable : NetworkBehaviour, IInteractable
{
    [Header("Pushable Settings")]
    [SerializeField] private float _pushForce = 5f;
    [SerializeField] private float _resetDelay = 5f;
    [SerializeField] private string _promptLabel = "Push";

    private Rigidbody _rigidbody;
    private Vector3 _originalPosition;
    private Quaternion _originalRotation;
    private float _resetTimer;
    private bool _isMoved;

    public string PromptLabel => _promptLabel;

    private void Awake()
    {
        _rigidbody = GetComponent<Rigidbody>();
        _originalPosition = transform.position;
        _originalRotation = transform.rotation;
    }

    private void Update()
    {
        if (!IsServerInitialized || !_isMoved) return;

        _resetTimer -= Time.deltaTime;
        if (_resetTimer <= 0f)
            ServerReset();
    }

    public void OnInteract(PlayerObject player)
    {
        if (!IsServerInitialized) return;
        ServerPush(player);
    }

    [ServerRpc(RequireOwnership = false)]
    private void ServerPush(PlayerObject player)
    {
        // Get push direction from player forward, no vertical component
        Vector3 pushDirection = player.transform.forward;
        pushDirection.y = 0f;
        pushDirection.Normalize();

        _rigidbody.AddForce(pushDirection * _pushForce, ForceMode.Impulse);

        _isMoved = true;
        _resetTimer = _resetDelay;
    }

    [Server]
    private void ServerReset()
    {
        _isMoved = false;
        _rigidbody.linearVelocity = Vector3.zero;
        _rigidbody.angularVelocity = Vector3.zero;
        transform.position = _originalPosition;
        transform.rotation = _originalRotation;
    }
}