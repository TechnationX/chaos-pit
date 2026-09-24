// PlayerCamera.cs

using FishNet.Object;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.InputSystem;
using FishNet.Object.Synchronizing;

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

    [Header("Hand Socket")]
    [SerializeField] private Transform _handSocketPivot;

    private PlayerObject _player;
    private CameraMode _currentMode;
    private float _verticalRotation;
    private CinemachineCamera _activeMiniGameCam;

    // The minigame scene's own physical Camera (the one its CinemachineBrain
    // actually renders through) — a separate object from the CinemachineCamera
    // vcam above. SetOwnHeadPiecesVisible needs to edit THIS camera's culling
    // mask while in MiniGame mode, since it's the one actually on screen during
    // minigames, not Camera.main. See SetMiniGameCamera/SwitchTo below.
    private Camera _activeMiniGameRenderCamera;

    // Whichever minigame render Camera SwitchTo has actually turned ON, if any —
    // tracked separately from _activeMiniGameRenderCamera above (which just holds
    // "the camera to switch TO next," and gets overwritten by SetMiniGameCamera
    // before SwitchTo runs). SwitchTo uses this to know what to turn back OFF.
    //
    // Every minigame scene's own Camera+CinemachineBrain now starts disabled by
    // default in the scene file (see the scene's "Camera" GameObject) instead of
    // always-on. It used to be always-on the instant the scene loaded, for ANY
    // process that had that scene loaded at all — and since a host's process is
    // also the server, the host has every active station's scene loaded
    // regardless of which station they're actually at, so that always-on camera
    // was rendering over the host's own view for a game they were never in. Now
    // it's off by default and only SwitchTo(MiniGame), running locally on the
    // actual participant's own process, turns it on — and turns it back off the
    // moment that same process leaves MiniGame mode.
    private Camera _enabledMiniGameRenderCamera;

    // Minigames use a fixed, scene-placed top-down camera (see
    // GameRoomManager.FindActiveMinigameCamera) rather than any
    // player-controlled look — this just exposes its transform so
    // PlayerMovement can read a ground-plane forward/right basis from it
    // for camera-relative movement and auto-turn.
    public Transform ActiveMiniGameCameraTransform =>
        _activeMiniGameCam != null ? _activeMiniGameCam.transform : null;

    private readonly SyncVar<float> _syncedPitch = new SyncVar<float>(
        new SyncTypeSettings(WritePermission.ClientUnsynchronized));
    private float _sensitivityMultiplier = 1f;
    private bool _invertY = false;
    private void HandleSensitivityChanged(float value) => _sensitivityMultiplier = value;
    private void HandleInvertYChanged(bool inverted) => _invertY = inverted;

    private const int PRIORITY_ACTIVE = 20;
    private const int PRIORITY_INACTIVE = 0;

    public CameraMode CurrentMode => _currentMode;
    private bool _isPaused = false;
    private bool _hasActivatedCameraOnce = false;

    // Runs for EVERY peer's copy of EVERY PlayerCamera, unlike Initialize()
    // below, which is owner-gated and — per PlayerObject.TryInitializeAsLocalOwner
    // — never even called for a remote client's view of another player's
    // PlayerCamera. Without this, OnSyncedPitchChanged's subscription (added
    // inside Initialize()) never happened on remote observers, so the cue/
    // hand-socket pitch synced correctly over the network but nobody's client
    // ever applied it visually except the owner's own. OnSyncedPitchChanged
    // already guards `if (IsOwner) return;`, so subscribing unconditionally
    // here is safe — it simply no-ops for the owner, who's already covered by
    // the identical subscribe call inside Initialize().
    public override void OnStartClient()
    {
        base.OnStartClient();

        _syncedPitch.OnChange -= OnSyncedPitchChanged;
        _syncedPitch.OnChange += OnSyncedPitchChanged;
    }

    public void Initialize(PlayerObject player)
    {
        _player = player;

        // Diagnostic for the intermittent "client hangs on join, stuck on
        // skybox" bug — see PlayerObject.TryInitializeAsLocalOwner, which is
        // the only caller of this for the local player. If OnStartClient/
        // OnOwnershipClient logged there but THIS never logs "setting up
        // local camera", something between those two points broke; if this
        // DOES log but the view still never leaves the skybox, the problem
        // is downstream of here (missing CinemachineBrain, vcam references,
        // or the scene's Main Camera itself).
        if (!IsOwner)
        {
            Debug.Log("[PlayerCamera] Initialize called on a non-owner copy — skipping (expected for every other player in the room).");
            return;
        }
        Debug.Log("[PlayerCamera] Initialize — setting up local camera (owner).");

        // Defensive reset — Initialize() runs on every minigame-entry and
        // lobby-return round-trip. If pause state was ever left stuck true
        // (e.g. a path that requests a scene change without going through
        // the normal Resume/Unpause flow — see PauseMenuUI.OnQuitToLobby),
        // this guarantees it can never carry over stale into the next scene.
        _isPaused = false;
        PauseMenuUI.Instance?.SetVisible(false);

        // Reset stale look pitch — otherwise whatever angle you were looking at
        // right before entering a minigame gets silently reapplied by
        // HandleFirstPersonLook() the next frame after ReinitializeCamera().
        _verticalRotation = 0f;

        SetupFirstPersonCam();
        SetupThirdPersonCam();

        // The very first activation ever for this player would otherwise blend
        // from wherever the Main Camera's raw Transform sits in the scene (a
        // stale level-editing vantage point high overhead) down to the player,
        // since CinemachineBrain treats that transform as the outgoing camera
        // state until some vcam has ever gone live. Cut just this one
        // activation instantly; every later mode switch (first-person <->
        // third-person, minigame entry/exit round-trips) keeps its normal
        // blend, since this guard only fires once per player.
        if (!_hasActivatedCameraOnce)
        {
            _hasActivatedCameraOnce = true;
            var brain = Camera.main != null ? Camera.main.GetComponent<CinemachineBrain>() : null;
            if (brain != null)
                StartCoroutine(CutInitialActivation(brain));
        }

        SwitchTo(_startingMode);

        Debug.Log($"[PlayerCamera] SwitchTo({_startingMode}) complete — Camera.main: {(Camera.main != null ? Camera.main.name : "NULL — no scene camera found")}, brain: {(Camera.main != null && Camera.main.GetComponent<CinemachineBrain>() != null ? "present" : "MISSING")}.");

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;

        // Unsubscribe before subscribing — Initialize() runs again every time
        // ReinitializeCamera() fires (every minigame round-trip), and without
        // this guard these handlers stack up and fire multiple times per event.
        _syncedPitch.OnChange -= OnSyncedPitchChanged;
        _syncedPitch.OnChange += OnSyncedPitchChanged;

        if (SettingsManager.Instance != null)
        {
            _sensitivityMultiplier = SettingsManager.Instance.Current.mouseSensitivity;
            _invertY = SettingsManager.Instance.Current.invertYAxis;
            ApplyFOV(SettingsManager.Instance.Current.fieldOfView);
        }

        SettingsManager.OnSensitivityChanged -= HandleSensitivityChanged;
        SettingsManager.OnInvertYChanged -= HandleInvertYChanged;
        SettingsManager.OnFOVChanged -= ApplyFOV;

        SettingsManager.OnSensitivityChanged += HandleSensitivityChanged;
        SettingsManager.OnInvertYChanged += HandleInvertYChanged;
        SettingsManager.OnFOVChanged += ApplyFOV;
    }

    // Zeroes the brain's blend time for a couple frames so the very first vcam
    // activation snaps instead of blending, then restores it so every later
    // transition (mode switches, minigame round-trips) blends normally again.
    // Only touches Time — never assumes a specific Style enum name/value, so
    // this stays correct across Cinemachine package versions.
    private System.Collections.IEnumerator CutInitialActivation(CinemachineBrain brain)
    {
        var originalBlend = brain.DefaultBlend;
        var instantBlend = originalBlend;
        instantBlend.Time = 0f;
        brain.DefaultBlend = instantBlend;

        yield return null;
        yield return null;

        brain.DefaultBlend = originalBlend;
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
        if (Cursor.lockState != CursorLockMode.Locked) return;

        var mouse = Mouse.current;
        if (mouse == null) return;

        Vector2 delta = mouse.delta.ReadValue();

        float mouseX = delta.x * _mouseSensitivity * _sensitivityMultiplier * 0.1f;
        float mouseY = delta.y * _mouseSensitivity * _sensitivityMultiplier * 0.1f;

        // Rotate player body horizontally
        _player.transform.Rotate(Vector3.up * mouseX);

        // Rotate vertical directly on the VCam
        float ySign = _invertY ? 1f : -1f;
        _verticalRotation += mouseY * ySign;
        _verticalRotation = Mathf.Clamp(_verticalRotation, _verticalClampMin, _verticalClampMax);
        _vcamFirstPerson.transform.localRotation = Quaternion.Euler(_verticalRotation, 0f, 0f);

        if (_handSocketPivot != null)
            _handSocketPivot.localRotation = Quaternion.Euler(_verticalRotation, 0f, 0f);

        _syncedPitch.Value = _verticalRotation;
    }

    private void SetupFirstPersonCam()
    {
        if (_vcamFirstPerson == null) return;
        _vcamFirstPerson.Follow = _firstPersonAnchor;
        _vcamFirstPerson.transform.position = _firstPersonAnchor.position;
        _vcamFirstPerson.transform.rotation = _firstPersonAnchor.rotation;
    }

    private void ApplyFOV(float fov)
    {
        if (_vcamFirstPerson != null)
        {
            var lens = _vcamFirstPerson.Lens;
            lens.FieldOfView = fov;
            _vcamFirstPerson.Lens = lens;
        }
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

        // Local-only: hides the player's own head, hair, and face-worn pieces (glasses,
        // facial hair) while looking through them in first person (so the anchor can sit
        // at true eye level without seeing the inside of the head), and shows them again
        // in third person/minigame view. Only does anything on the owner's own build —
        // see PlayerAppearance.SetOwnHeadPiecesVisible.
        //
        // MiniGame mode renders through the minigame scene's own top-down camera
        // (_activeMiniGameRenderCamera), not the persistent Camera.main this trick
        // otherwise targets, so pass that camera explicitly — this was the actual bug
        // behind "head invisible in minigames": the culling mask was being flipped on
        // the wrong camera entirely.
        Camera headVisibilityCamera = mode == CameraMode.MiniGame ? _activeMiniGameRenderCamera : null;
        _player.Appearance?.SetOwnHeadPiecesVisible(mode != CameraMode.FirstPerson, headVisibilityCamera);

        // Turn off whatever minigame render Camera this process previously turned
        // on, unconditionally, before possibly turning a (new) one on below — this
        // runs on every mode switch, not just ones leaving MiniGame, so a
        // minigame-to-minigame round trip (finish one, immediately reinitialize
        // into another) can't leave the old scene's camera stuck enabled. See
        // _enabledMiniGameRenderCamera's comment for why this needs to exist at all.
        if (_enabledMiniGameRenderCamera != null)
        {
            _enabledMiniGameRenderCamera.enabled = false;
            CinemachineBrain oldBrain = _enabledMiniGameRenderCamera.GetComponent<CinemachineBrain>();
            if (oldBrain != null) oldBrain.enabled = false;
            _enabledMiniGameRenderCamera = null;
        }

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

                // The vcam priority above only matters if something is actually
                // rendering through this scene's Brain — that Brain (and its
                // Camera) starts disabled by default now, so turn it on here,
                // specifically, only on the process that's actually entering
                // MiniGame mode. See _enabledMiniGameRenderCamera's comment.
                if (_activeMiniGameRenderCamera != null)
                {
                    _activeMiniGameRenderCamera.enabled = true;
                    CinemachineBrain brain = _activeMiniGameRenderCamera.GetComponent<CinemachineBrain>();
                    if (brain != null) brain.enabled = true;
                    _enabledMiniGameRenderCamera = _activeMiniGameRenderCamera;
                }

                // This case didn't set cursor state before (nothing used
                // MiniGame mode yet) — locking/hiding it here matches the
                // other two active modes, since there's no cursor-driven
                // aiming during minigames, just directional movement.
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
                break;
        }

        // Debug.Log($"After switch — FP: {_vcamFirstPerson.Priority.Value}, TP: {_vcamThirdPerson.Priority.Value}");
    }

    // renderCamera: the minigame scene's own physical Camera (see
    // GameRoomManager.FindActiveMinigameRenderCamera) — stored so SwitchTo can point
    // SetOwnHeadPiecesVisible's culling-mask trick at the camera actually on screen
    // during minigames instead of Camera.main.
    public void SetMiniGameCamera(CinemachineCamera vcam, Camera renderCamera)
    {
        _activeMiniGameCam = vcam;
        _activeMiniGameRenderCamera = renderCamera;
    }

    public void ClearMiniGameCamera()
    {
        _activeMiniGameCam = null;
        _activeMiniGameRenderCamera = null;
        SwitchTo(CameraMode.FirstPerson);
    }

    private void TogglePause()
    {
        _isPaused = !_isPaused;

        if (_isPaused)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            _player.Movement.SetMovementLocked(true, "pause_menu");
        }
        else
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            _player.Movement.SetMovementLocked(false, "pause_menu");
        }

        // Tell the pause menu to show/hide
        PauseMenuUI.Instance?.SetVisible(_isPaused);
    }

    public void Unpause()
    {
        if (_isPaused) TogglePause();
    }

    public void ReleaseCursor()
    {
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    public void LockCursor()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    private void OnSyncedPitchChanged(float prev, float next, bool asServer)
    {
        if (IsOwner) return; // owner already drives this locally, avoid double-applying
        if (_handSocketPivot == null) return;
        _handSocketPivot.localRotation = Quaternion.Euler(next, 0f, 0f);
    }

    private void OnDestroy()
    {
        if (!IsOwner) return;

        SettingsManager.OnSensitivityChanged -= HandleSensitivityChanged;
        SettingsManager.OnInvertYChanged -= HandleInvertYChanged;
        SettingsManager.OnFOVChanged -= ApplyFOV;
    }
}