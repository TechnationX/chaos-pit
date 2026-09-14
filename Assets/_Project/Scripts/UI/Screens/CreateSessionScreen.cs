// CreateSessionScreen.cs
// Owns: loading state while Relay allocates, join code display, auto-transition to Lobby.
// Attach to: CreateSessionScreen GameObject in Main Menu scene.

using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using TMPro;
using FishNet;

public class CreateSessionScreen : UIScreenBase
{
    // ─── Inspector ────────────────────────────────────────────────────────────

    [Header("Loading State")]
    [SerializeField] private GameObject loadingState;
    [SerializeField] private TMP_Text loadingText;

    [Header("Code State")]
    [SerializeField] private GameObject codeState;
    [SerializeField] private TMP_Text joinCodeText;
    [SerializeField] private Button copyCodeButton;

    [Header("Scene")]
    [SerializeField] private string lobbySceneName = "Lobby";

    // ─── References ───────────────────────────────────────────────────────────

    private MainMenuManager _mainMenuManager;

    // ─── Lifecycle ────────────────────────────────────────────────────────────

    protected override void Awake()
    {
        base.Awake();
        _mainMenuManager = FindFirstObjectByType<MainMenuManager>();

        copyCodeButton.onClick.AddListener(CopyCodeToClipboard);
    }

    // ─── UIScreenBase Hooks ───────────────────────────────────────────────────

    public override void OnShow()
    {
        // Always start in loading state when screen opens
        //Debug.Log("[CreateSessionScreen] OnShow fired.");
        ShowLoadingState();
        StartSession();
    }

    public override void OnHide()
    {
        // Clean up if player backs out before session is ready
        StopAllCoroutines();
    }

    // ─── Session Start ────────────────────────────────────────────────────────

    private void StartSession()
    {
        if (SessionManager.Instance == null)
        {
            Debug.LogError("[CreateSessionScreen] SessionManager not found.");
            ShowLoadingState("Error — SessionManager missing.");
            return;
        }

        // Listen for join code once Relay allocates
        SessionManager.Instance.OnJoinCodeReady += HandleJoinCodeReady;

        // Start host session — SessionManager fires OnJoinCodeReady when ready
        SessionManager.Instance.StartHostSession();
    }

    // ─── Join Code Ready ──────────────────────────────────────────────────────

    private void HandleJoinCodeReady(string joinCode)
    {
        // Unsubscribe immediately — only need this once
        SessionManager.Instance.OnJoinCodeReady -= HandleJoinCodeReady;

        //Debug.Log($"[CreateSessionScreen] Join code received: {joinCode}");

        joinCodeText.text = joinCode;
        ShowCodeState();

        // Check if already active before subscribing
        if (SessionManager.Instance.CurrentState == SessionManager.SessionState.Active)
        {
            LoadLobby();
            return;
        }

        SessionManager.Instance.OnSessionStateChanged += HandleSessionStateChanged;
    }

    private void HandleSessionStateChanged(SessionManager.SessionState state)
    {
        if (state == SessionManager.SessionState.Active)
        {
            SessionManager.Instance.OnSessionStateChanged -= HandleSessionStateChanged;
            LoadLobby();
        }
    }

    // ─── UI State ─────────────────────────────────────────────────────────────

    private void ShowLoadingState(string message = "Creating session...")
    {
        loadingState.SetActive(true);
        codeState.SetActive(false);
        loadingText.text = message;
    }

    private void ShowCodeState()
    {
        loadingState.SetActive(false);
        codeState.SetActive(true);
    }

    // ─── Actions ──────────────────────────────────────────────────────────────

    private void CopyCodeToClipboard()
    {
        if (string.IsNullOrEmpty(joinCodeText.text)) return;
        GUIUtility.systemCopyBuffer = joinCodeText.text;
        //Debug.Log($"[CreateSessionScreen] Join code copied: {joinCodeText.text}");

        // TODO: Show brief "Copied!" confirmation tooltip
    }

    protected override void OnBackPressed()
    {
        // Clean up session if player backs out before anyone joins
        if (SessionManager.Instance != null)
        {
            SessionManager.Instance.OnJoinCodeReady -= HandleJoinCodeReady;
            SessionManager.Instance.OnSessionStateChanged -= HandleSessionStateChanged;
            SessionManager.Instance.EndSession();
        }

        _mainMenuManager?.ShowMainMenu();
    }

    private void LoadLobby()
    {
        //Debug.Log("[CreateSessionScreen] Session active. Loading Lobby.");

        // Same loading screen JoinSessionScreen uses — host's own catch-up
        // cost is normally negligible (props trickle in via the server's
        // own spawn coroutines), but this keeps behavior consistent and
        // covers the host if that ever changes.
        if (LobbyLoadingScreen.HasInstance)
            LobbyLoadingScreen.Instance.ShowAndWaitForSettled();

        // Async, not the plain synchronous LoadScene — see JoinSessionScreen's
        // LoadLobby() for why the synchronous version freezes everything,
        // including this very loading screen, for the duration of the load.
        AsyncOperation op = SceneManager.LoadSceneAsync(lobbySceneName);
        if (op != null)
        {
            // Host is its own local client too, and LobbySpawner now gates
            // every spawn (including the host's own player) on this signal
            // instead of FishNet's OnClientLoadedStartScenes — see
            // LobbyReadyBroadcast.cs and JoinSessionScreen.LoadLobby for why.
            op.completed += _ => InstanceFinder.ClientManager.Broadcast(new LobbyReadyBroadcast());
        }
    }
}