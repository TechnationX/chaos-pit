// MirrorReflectionCamera.cs
using UnityEngine;

[RequireComponent(typeof(Camera))]
public class MirrorReflectionCamera : MonoBehaviour
{
    [SerializeField] private Camera _mirrorCamera;
    [SerializeField] private float _heightOffset = 1.5f; // roughly chest/head height to aim at

    private Transform _localPlayerRoot;

    private void Awake()
    {
        if (_mirrorCamera == null) _mirrorCamera = GetComponent<Camera>();
    }

    private void LateUpdate()
    {
        if (_localPlayerRoot == null)
        {
            TryResolveLocalPlayer();
            if (_localPlayerRoot == null) return;
        }

        Vector3 lookTarget = _localPlayerRoot.position + Vector3.up * _heightOffset;
        transform.LookAt(lookTarget);
    }

    private void TryResolveLocalPlayer()
    {
        // TODO: replace with your actual local-player resolution if you have
        // a cleaner static reference (e.g. PlayerObject.Local)
        var localConn = FishNet.InstanceFinder.ClientManager?.Connection;
        _localPlayerRoot = localConn?.FirstObject?.transform;
    }
}