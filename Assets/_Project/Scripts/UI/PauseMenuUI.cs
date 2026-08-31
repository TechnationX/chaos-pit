// PauseMenuUI.cs

using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

public class PauseMenuUI : MonoBehaviour
{
    public static PauseMenuUI Instance { get; private set; }

    // Picks what the Quit button does — one script, a flag per instance,
    // same convention as this project's other multi-context UI (e.g. BowlingPanelButton).
    public enum PauseContext { Lobby, Minigame }

    [Header("Context")]
    [SerializeField] private PauseContext _context = PauseContext.Lobby;

    [Header("UI")]
    [SerializeField] private GameObject _panel;
    [SerializeField] private GameObject _mainButtonsGroup;
    [SerializeField] private Button _resumeButton;
    [SerializeField] private Button _settingsButton;
    [SerializeField] private Button _quitToMenuButton;

    [Header("Settings")]
    [SerializeField] private GameObject _settingsPanel;
    [SerializeField] private Button _settingsBackButton;

    private void Awake()
    {
        Instance = this;
        _panel.SetActive(false);

        if (_settingsPanel != null) _settingsPanel.SetActive(false);
        if (_settingsBackButton != null) _settingsBackButton.gameObject.SetActive(false);
        if (_mainButtonsGroup != null) _mainButtonsGroup.SetActive(true);

        _resumeButton.onClick.AddListener(OnResume);
        _settingsButton.onClick.AddListener(OnOpenSettings);
        _quitToMenuButton.onClick.AddListener(OnQuitPressed);

        if (_settingsBackButton != null)
            _settingsBackButton.onClick.AddListener(OnCloseSettings);
    }

    // The real fix for the "pause menu goes dead after a minigame round-trip"
    // bug lives in OnEnable() below — this just covers the moment of
    // destruction itself, before OnEnable() elsewhere gets a chance to fire.
    private void OnDestroy()
    {
        if (Instance != this) return; // some other instance already replaced us — leave it alone
        Instance = null;
    }

    // Re-claims the singleton every time this component's GameObject goes
    // inactive -> active, not just on first load like Awake(). This is the
    // actual fix: the lobby's own PauseMenuUI lives on the same GameObject
    // as LobbyUIManager (see Lobby.unity), and LobbyUIManager.SetVisible()
    // does gameObject.SetActive() on that shared object — so entering a
    // minigame (RpcSetLobbyCanvasVisible(false)) deactivates the lobby's
    // PauseMenuUI without destroying it. While a minigame is loaded, its own
    // PauseMenuUI (context=Minigame) correctly owns Instance. But when that
    // minigame scene unloads, its PauseMenuUI.OnDestroy() fires while the
    // lobby's copy is STILL inactive — a FindObjectByType search at that
    // exact moment finds nothing, so Instance was stuck null even after the
    // lobby canvas reactivated a moment later, since Awake() never re-fires.
    // OnEnable() does re-fire on every reactivation, which is exactly the
    // moment Instance needs to be reclaimed.
    private void OnEnable()
    {
        Instance = this;
    }

    public void SetVisible(bool visible)
    {
        _panel.SetActive(visible);

        // Always land back on the main button row next time the pause menu opens,
        // regardless of which view (main / settings) it was left on.
        if (!visible)
        {
            if (_settingsPanel != null) _settingsPanel.SetActive(false);
            if (_settingsBackButton != null) _settingsBackButton.gameObject.SetActive(false);
            if (_mainButtonsGroup != null) _mainButtonsGroup.SetActive(true);
        }
    }

    private void OnResume()
    {
        // Was FindFirstObjectByType<PlayerCamera>() — not owner-filtered, so
        // in multiplayer it could grab a different player's camera instead
        // of the local one. FindLocalPlayer() (used just below by
        // OnQuitToLobby already) is the correct, owner-filtered lookup.
        PlayerObject localPlayer = FindLocalPlayer();
        localPlayer?.Camera.Unpause();
    }

    private void OnOpenSettings()
    {
        if (_mainButtonsGroup != null) _mainButtonsGroup.SetActive(false);
        if (_settingsPanel != null) _settingsPanel.SetActive(true);
        if (_settingsBackButton != null) _settingsBackButton.gameObject.SetActive(true);
    }

    private void OnCloseSettings()
    {
        if (_settingsPanel != null) _settingsPanel.SetActive(false);
        if (_settingsBackButton != null) _settingsBackButton.gameObject.SetActive(false);
        if (_mainButtonsGroup != null) _mainButtonsGroup.SetActive(true);
    }

    // Behavior depends on _context — see the enum above.
    private void OnQuitPressed()
    {
        switch (_context)
        {
            case PauseContext.Minigame:
                OnQuitToLobby();
                break;

            case PauseContext.Lobby:
            default:
                OnQuitToMenu();
                break;
        }
    }

    private void OnQuitToMenu()
    {
        SessionManager.Instance?.EndSession();
        DisconnectReason.Instance?.Set(string.Empty);
        SceneManager.LoadScene("MainMenu");
    }

    private void OnQuitToLobby()
    {
        PlayerObject localPlayer = FindLocalPlayer();
        if (localPlayer == null)
        {
            Debug.LogWarning("[PauseMenuUI] OnQuitToLobby — no local PlayerObject found.");
            return;
        }

        // This used to skip straight to RequestLeaveMinigame without ever
        // un-pausing — _isPaused stayed stuck true and this panel stayed
        // SetActive(true), neither of which anything else resets. Next
        // Escape press back in the lobby would just flip _isPaused true→false
        // and hide a menu nobody saw, i.e. "pause menu does nothing." Explicit
        // Unpause() here fixes it at the source (PlayerCamera.Initialize() —
        // called during the return-to-lobby camera reinit — now also resets
        // this defensively, so it can't get stuck this way again).
        localPlayer.Camera.Unpause();

        GameRoomManager.Instance.RequestLeaveMinigame(localPlayer);
    }

    private PlayerObject FindLocalPlayer()
    {
        foreach (var p in FindObjectsByType<PlayerObject>(FindObjectsSortMode.None))
            if (p.IsOwner) return p;
        return null;
    }
}
