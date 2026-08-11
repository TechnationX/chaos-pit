// MirrorActivator.cs
using UnityEngine;

[RequireComponent(typeof(Camera))]
public class MirrorActivator : MonoBehaviour
{
    [SerializeField] private float _activationDistance = 6f;
    [SerializeField] private Transform _mirrorPlane;

    [Header("Blank State")]
    [SerializeField] private Renderer _mirrorSurfaceRenderer;
    [SerializeField] private Texture _activeTexture;   // the MirrorRenderTexture
    [SerializeField] private Texture _blankTexture;    // solid black, or a "dusty glass" texture

    private Camera _mirrorCamera;
    private Transform _localPlayerTransform;
    private bool _isActive;

    private void Awake()
    {
        _mirrorCamera = GetComponent<Camera>();
        if (_mirrorPlane == null) _mirrorPlane = transform;

        SetMirrorState(false); // start blank until proximity check runs
    }

    private void Update()
    {
        if (_localPlayerTransform == null)
        {
            TryResolveLocalPlayer();
            if (_localPlayerTransform == null) return;
        }

        float dist = Vector3.Distance(_localPlayerTransform.position, _mirrorPlane.position);
        bool shouldBeActive = dist <= _activationDistance;

        if (shouldBeActive != _isActive)
            SetMirrorState(shouldBeActive);
    }

    private void SetMirrorState(bool active)
    {
        _isActive = active;
        _mirrorCamera.enabled = active;

        if (!active && _mirrorCamera.targetTexture != null)
        {
            RenderTexture prevActive = RenderTexture.active;
            RenderTexture.active = _mirrorCamera.targetTexture;
            GL.Clear(true, true, Color.black);
            RenderTexture.active = prevActive;
        }
    }

    private void TryResolveLocalPlayer()
    {
        var localConn = FishNet.InstanceFinder.ClientManager?.Connection;
        _localPlayerTransform = localConn?.FirstObject?.transform;
    }
}