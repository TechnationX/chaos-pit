// CustomizationStation.cs
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

// The object inside the PrivateRoom that opens the character creator.
//
// The CharacterCreator scene is loaded ADDITIVELY, on this machine only, on top of
// the still-running Lobby. A normal (single) scene load would unload the Lobby here:
// on a client that destroys the networked player, and on the host it ends the lobby
// for everyone. To the player it still looks like going to the customization scene:
// the lobby camera and sun are switched off, the creator scene is moved far above the
// lobby so the two never overlap, and its own camera takes over. The player's lobby
// character just waits, frozen, inside the sealed room where nobody can see it.
//
// Saving (the creator's Continue button) is the only way out. It hands the new
// appearance back here via LobbyCustomizationSession; this unloads the creator,
// restores the lobby view and controls, and rebuilds/broadcasts the new appearance.
//
// Needs a Collider on the interactable layer, same as every other IInteractable prop.
public class CustomizationStation : MonoBehaviour, IInteractable
{
    [SerializeField] private PrivateRoom _room;
    [SerializeField] private string _creatorSceneName = "CharacterCreator";
    [Tooltip("Where the creator scene is moved while open. Far enough from the lobby, the minigame stations (stacked on Y), and anything else that it can't be seen or collided with.")]
    [SerializeField] private Vector3 _creatorSceneOffset = new Vector3(0f, 1000f, 0f);
    [Tooltip("Optional. Where the player is placed after saving. Leave empty to return them exactly where they were.")]
    [SerializeField] private Transform _returnPoint;
    [SerializeField] private string _promptLabel = "Customize Character";

    private const string LockSource = "customizing";

    // The station currently showing the creator on this machine, so PrivateRoom's
    // AFK eviction can close it from a TargetRpc.
    private static CustomizationStation _active;

    private bool _isOpen;
    private bool _isTransitioning;
    private PlayerObject _player;
    private Scene _creatorScene;

    // Lobby state switched off while the creator is open, restored exactly on close.
    private GameObject _lobbyCameraObject;
    private readonly List<Light> _disabledLobbySuns = new List<Light>();
    private LobbyUIManager _lobbyUI;
    private bool _lobbyUIWasVisible;

    public string PromptLabel =>
        !_isOpen && !_isTransitioning && (_room == null || _room.IsLocalPlayerInside) ? _promptLabel : "";

    public void OnInteract(PlayerObject player)
    {
        if (_isOpen || _isTransitioning || player == null || !player.IsOwner) return;
        if (_room != null && !_room.IsLocalPlayerInside) return;
        if (Application.CanStreamedLevelBeLoaded(_creatorSceneName) == false)
        {
            Debug.LogError($"[CustomizationStation] Scene '{_creatorSceneName}' isn't in Build Settings.");
            return;
        }

        StartCoroutine(OpenRoutine(player));
    }

    // Called by PrivateRoom when the server evicts this player for inactivity.
    // Closes without saving.
    public static void ForceCloseActive()
    {
        if (_active != null) _active.StartCoroutine(_active.CloseRoutine(null));
    }

    private void OnDestroy()
    {
        // Lobby unloading (disconnect, session end) while open: don't let a stale
        // session make the Main Menu's creator think it should return to a lobby.
        if (_active == this)
        {
            LobbyCustomizationSession.End();
            _active = null;
        }
        SceneManager.sceneLoaded -= HandleSceneLoaded;
    }

    // ---------------- Open ----------------

    private IEnumerator OpenRoutine(PlayerObject player)
    {
        _isTransitioning = true;
        _active = this;
        _player = player;

        // Freeze the lobby player and its UI. Pause is blocked so Escape can't open
        // the lobby pause menu behind the creator.
        _player.Camera.Unpause();
        _player.Camera.SetPauseBlocked(true);
        _player.Movement.SetMovementLocked(true, LockSource);
        _player.Interaction.SetInteractionEnabled(false);
        _player.Camera.ReleaseCursor();

        _lobbyUI = FindFirstObjectByType<LobbyUIManager>(FindObjectsInactive.Include);
        _lobbyUIWasVisible = _lobbyUI != null && _lobbyUI.gameObject.activeSelf;
        if (_lobbyUI != null) _lobbyUI.SetVisible(false);

        // Lobby camera off (its AudioListener goes with it, so the creator camera's
        // own listener is the only one). Only the active (Lobby) scene's suns are
        // touched, so GameRoomManager's per-station minigame sun bookkeeping on a
        // host is never disturbed.
        Camera lobbyCamera = Camera.main;
        _lobbyCameraObject = lobbyCamera != null ? lobbyCamera.gameObject : null;
        if (_lobbyCameraObject != null) _lobbyCameraObject.SetActive(false);

        _disabledLobbySuns.Clear();
        Scene lobbyScene = SceneManager.GetActiveScene();
        foreach (Light light in FindObjectsByType<Light>(FindObjectsSortMode.None))
        {
            if (light.type != LightType.Directional || !light.enabled) continue;
            if (light.gameObject.scene != lobbyScene) continue;
            light.enabled = false;
            _disabledLobbySuns.Add(light);
        }

        LobbyCustomizationSession.Begin(HandleSaved);

        // sceneLoaded fires right after the new scene's Awake/OnEnable and before its
        // first Start or rendered frame, so the offset and cleanup below happen before
        // anything is drawn at the lobby's coordinates.
        SceneManager.sceneLoaded -= HandleSceneLoaded;
        SceneManager.sceneLoaded += HandleSceneLoaded;

        AsyncOperation load = SceneManager.LoadSceneAsync(_creatorSceneName, LoadSceneMode.Additive);
        while (load != null && !load.isDone) yield return null;

        _isOpen = true;
        _isTransitioning = false;
    }

    private void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (mode != LoadSceneMode.Additive || scene.name != _creatorSceneName) return;
        SceneManager.sceneLoaded -= HandleSceneLoaded;

        _creatorScene = scene;

        foreach (GameObject root in scene.GetRootGameObjects())
            root.transform.position += _creatorSceneOffset;

        // One EventSystem only: keep the lobby's if it has one, otherwise the creator's.
        bool lobbyHasEventSystem = false;
        foreach (EventSystem es in FindObjectsByType<EventSystem>(FindObjectsSortMode.None))
            if (es.gameObject.scene != scene && es.isActiveAndEnabled) { lobbyHasEventSystem = true; break; }

        if (lobbyHasEventSystem)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (EventSystem es in root.GetComponentsInChildren<EventSystem>(true))
                {
                    foreach (BaseInputModule module in es.GetComponents<BaseInputModule>())
                        module.enabled = false;
                    es.enabled = false;
                }
            }
        }
    }

    // ---------------- Close ----------------

    private void HandleSaved(byte[] packedAppearance)
    {
        StartCoroutine(CloseRoutine(packedAppearance));
    }

    // packedAppearance null = close without applying (AFK eviction).
    private IEnumerator CloseRoutine(byte[] packedAppearance)
    {
        if (!_isOpen && !_isTransitioning) yield break;

        // Eviction mid-open: let the load finish so there's a scene to unload.
        while (_isTransitioning) yield return null;
        if (!_isOpen) yield break; // another close already finished while we waited
        _isTransitioning = true;

        LobbyCustomizationSession.End();
        SceneManager.sceneLoaded -= HandleSceneLoaded;

        if (_creatorScene.IsValid() && _creatorScene.isLoaded)
        {
            AsyncOperation unload = SceneManager.UnloadSceneAsync(_creatorScene);
            while (unload != null && !unload.isDone) yield return null;
        }

        foreach (Light light in _disabledLobbySuns)
            if (light != null) light.enabled = true;
        _disabledLobbySuns.Clear();

        if (_lobbyCameraObject != null) _lobbyCameraObject.SetActive(true);
        if (_lobbyUI != null && _lobbyUIWasVisible) _lobbyUI.SetVisible(true);

        if (_player != null)
        {
            if (packedAppearance != null && _returnPoint != null)
                PrivateRoom.TeleportLocal(_player, _returnPoint.position, _returnPoint.rotation);

            _player.Movement.SetMovementLocked(false, LockSource);
            _player.Interaction.SetInteractionEnabled(true);
            _player.Camera.SetPauseBlocked(false);

            // Re-locks the cursor and re-applies own-head visibility on the lobby
            // camera, which was off while the creator was showing.
            _player.Camera.SwitchToLobbyWalkMode();

            if (packedAppearance != null && _player.Appearance != null)
                _player.Appearance.ApplyNewOwnerAppearance(packedAppearance);
        }

        _player = null;
        _isOpen = false;
        _isTransitioning = false;
        if (_active == this) _active = null;
    }
}
