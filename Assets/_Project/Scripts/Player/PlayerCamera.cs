// PlayerCamera.cs

using FishNet.Object;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerCamera : NetworkBehaviour
{
    public enum CameraMode { FirstPerson, ThirdPerson, MiniGame }

    [Header("Camera Mode")]
    [SerializeField] private CameraMode _startingMode = CameraMode.FirstPerson;

    [Header("Virtual Cameras")]
    [SerializeField] private CinemachineCamera _vcamFirstPerson;
    [SerializeField] private CinemachineCamera _vcamThirdPerson;

    [Header("First Person Settings")]
    [SerializeField] private Transform _firstPersonAnchor;
    [SerializeField] private float _mouseSensitivity = 2f;
    [SerializeField] private float _verticalClampMin = -80f;
    [SerializeField] private float _verticalClampMax = 80f;

    [Header("Third Person Settings")]
    [SerializeField] private float _followDistance = 4f;

    private PlayerObject _player;
    private CameraMode _currentMode;
    private float _verticalRotation;
    private CinemachineCamera _activeMiniGameCam;

    private const int PRIORITY_ACTIVE = 20;
    private const int PRIORITY_INACTIVE = 0;

    public CameraMode CurrentMode => _currentMode;
    private bool _isPaused = false;

    public void Initialize(PlayerObject player)
    {
        _player = player;

        if (!IsOwner) return;

        SetupFirstPersonCam();
        SetupThirdPersonCam();
        SwitchTo(_startingMode);

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    private void Update()
    {
        if (!IsOwner) return;
        // Debug.Log($"Camera mode: {_currentMode}, FP Priority: {_vcamFirstPerson.Priority}, TP Priority: {_vcamThirdPerson.Priority}");

        if (Keyboard.current.escapeKey.wasPressedThisFrame)
            TogglePause();

        if (_currentMode == CameraMode.FirstPerson)
            HandleFirstPersonLook();
    }

    private void HandleFirstPersonLook()
    {
        var mouse = Mouse.current;
        if (mouse == null) return;

        Vector2 delta = mouse.delta.ReadValue();

        float mouseX = delta.x * _mouseSensitivity * 0.1f;
        float mouseY = delta.y * _mouseSensitivity * 0.1f;

        // Rotate player body horizontally
        _player.transform.Rotate(Vector3.up * mouseX);

        // Rotate vertical directly on the VCam
        _verticalRotation -= mouseY;
        _verticalRotation = Mathf.Clamp(_verticalRotation, _verticalClampMin, _verticalClampMax);
        _vcamFirstPerson.transform.localRotation = Quaternion.Euler(_verticalRotation, 0f, 0f);
    }

    private void SetupFirstPersonCam()
    {
        if (_vcamFirstPerson == null) return;
        _vcamFirstPerson.Follow = _firstPersonAnchor;
        _vcamFirstPerson.transform.position = _firstPersonAnchor.position;
        _vcamFirstPerson.transform.rotation = _firstPersonAnchor.rotation;
    }

    private void SetupThirdPersonCam()
    {
        if (_vcamThirdPerson == null) return;
        _vcamThirdPerson.Follow = _firstPersonAnchor;
        _vcamThirdPerson.LookAt = _firstPersonAnchor;

        var orbitalFollow = _vcamThirdPerson.GetComponent<CinemachineOrbitalFollow>();
        if (orbitalFollow != null)
        {
            orbitalFollow.Radius = _followDistance;
            orbitalFollow.TargetOffset = new Vector3(0, 0.5f, 0);
        }
    }

    private void SetPriority(CinemachineCamera vcam, int value)
    {
        if (vcam == null) return;
        var priority = vcam.Priority;
        priority.Value = value;
        vcam.Priority = priority;
    }

    public void SwitchTo(CameraMode mode)
    {
        // Debug.Log($"SwitchTo: {mode}, FP Priority before: {_vcamFirstPerson.Priority.Value}, TP Priority before: {_vcamThirdPerson.Priority.Value}");
        _currentMode = mode;

        SetPriority(_vcamFirstPerson, PRIORITY_INACTIVE);
        SetPriority(_vcamThirdPerson, PRIORITY_INACTIVE);

        // Debug.Log($"After inactive — FP: {_vcamFirstPerson.Priority.Value}, TP: {_vcamThirdPerson.Priority.Value}");

        if (_activeMiniGameCam != null)
            SetPriority(_activeMiniGameCam, PRIORITY_INACTIVE);

        switch (mode)
        {
            case CameraMode.FirstPerson:
                SetPriority(_vcamFirstPerson, PRIORITY_ACTIVE);
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
                break;

            case CameraMode.ThirdPerson:
                SetPriority(_vcamThirdPerson, PRIORITY_ACTIVE);
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
                break;

            case CameraMode.MiniGame:
                if (_activeMiniGameCam != null)
                    SetPriority(_activeMiniGameCam, PRIORITY_ACTIVE);
                break;
        }

        // Debug.Log($"After switch — FP: {_vcamFirstPerson.Priority.Value}, TP: {_vcamThirdPerson.Priority.Value}");
    }

    public void SetMiniGameCamera(CinemachineCamera vcam)
    {
        _activeMiniGameCam = vcam;
    }

    public void ClearMiniGameCamera()
    {
        _activeMiniGameCam = null;
        SwitchTo(CameraMode.FirstPerson);
    }

    private void TogglePause()
    {
        _isPaused = !_isPaused;

        if (_isPaused)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            _player.Movement.SetMovementLocked(true);
        }
        else
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            _player.Movement.SetMovementLocked(false);
        }

        // Tell the pause menu to show/hide
        PauseMenuUI.Instance?.SetVisible(_isPaused);
    }

    public void Unpause()
    {
        if (_isPaused) TogglePause();
    }
}