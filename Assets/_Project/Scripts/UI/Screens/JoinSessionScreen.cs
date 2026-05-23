// JoinSessionScreen.cs
// Place in: Assets/_Project/Scripts/UI/Screens/
// Owns: join code input, connect button, error state display, transition to Lobby.
// Attach to: JoinSessionScreen GameObject in Main Menu scene.

using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using TMPro;

public class JoinSessionScreen : UIScreenBase
{
    // ─── Inspector ────────────────────────────────────────────────────────────

    [Header("Input")]
    [SerializeField] private TMP_InputField joinCodeInput;
    [SerializeField] private Button connectButton;

    [Header("Error")]
    [SerializeField] private TMP_Text errorText;

    [Header("Scene")]
    [SerializeField] private string lobbySceneName = "Lobby";

    // ─── References ───────────────────────────────────────────────────────────

    private MainMenuManager _mainMenuManager;
    private bool _isConnecting = false;

    // ─── Lifecycle ────────────────────────────────────────────────────────────

    protected override void Awake()
    {
        base.Awake();
        _mainMenuManager = FindFirstObjectByType<MainMenuManager>();

        connectButton.onClick.AddListener(OnConnectPressed);

        // Allow pressing Enter to connect
        joinCodeInput.onSubmit.AddListener(_ => OnConnectPressed());
    }

    // ─── UIScreenBase Hooks ───────────────────────────────────────────────────

    public override void OnShow()
    {
        // Reset state every time screen opens
        joinCodeInput.text = string.Empty;
        HideError();
        SetConnecting(false);
        joinCodeInput.ActivateInputField();
    }

    public override void OnHide()
    {
        // Clean up listeners and reset state
        if (SessionManager.Instance != null)
        {
            SessionManager.Instance.OnSessionStateChanged -= HandleSessionStateChanged;
        }

        StopAllCoroutines();
        SetConnecting(false);
    }

    // ─── Connect ──────────────────────────────────────────────────────────────

    private void OnConnectPressed()
    {
        if (_isConnecting) return;

        string code = joinCodeInput.text.Trim();

        if (!ValidateCode(code)) return;

        if (SessionManager.Instance == null)
        {
            ShowError("Session system unavailable.");
            return;
        }

        HideError();
        SetConnecting(true);

        // Listen for state changes — success moves to Lobby, failure shows error
        SessionManager.Instance.OnSessionStateChanged += HandleSessionStateChanged;

        SessionManager.Instance.JoinSession(code);

        Debug.Log($"[JoinSessionScreen] Attempting to join session with code: {code}");
    }

    // ─── Session State ────────────────────────────────────────────────────────

    private void HandleSessionStateChanged(SessionManager.SessionState state)
    {
        switch (state)
        {
            case SessionManager.SessionState.Waiting:
                // Still connecting — keep spinner running
                break;

            case SessionManager.SessionState.Active:
                SessionManager.Instance.OnSessionStateChanged -= HandleSessionStateChanged;
                LoadLobby();
                break;

            case SessionManager.SessionState.Idle:
                // Returned to idle — connection failed
                SessionManager.Instance.OnSessionStateChanged -= HandleSessionStateChanged;
                SetConnecting(false);
                ShowError("Could not connect. Check the code and try again.");
                break;
        }
    }

    // ─── Validation ───────────────────────────────────────────────────────────

    private bool ValidateCode(string code)
    {
        if (string.IsNullOrEmpty(code))
        {
            ShowError("Please enter a join code.");
            return false;
        }

        // Placeholder validation — 6 digit numeric code
        if (code.Length < 6)
        {
            ShowError("Join code is too short.");
            return false;
        }

        return true;
    }

    // ─── UI State ─────────────────────────────────────────────────────────────

    private void ShowError(string message)
    {
        errorText.text = message;
        errorText.gameObject.SetActive(true);
        Debug.LogWarning($"[JoinSessionScreen] Error: {message}");
    }

    private void HideError()
    {
        errorText.text = string.Empty;
        errorText.gameObject.SetActive(false);
    }

    private void SetConnecting(bool connecting)
    {
        _isConnecting = connecting;

        // Disable input and button while connecting
        connectButton.interactable = !connecting;
        joinCodeInput.interactable = !connecting;
        backButton.interactable = !connecting;

        // TODO: Show/hide connecting spinner here
    }

    // ─── Navigation ───────────────────────────────────────────────────────────

    protected override void OnBackPressed()
    {
        if (_isConnecting) return;
        _mainMenuManager?.ShowMainMenu();
    }

    private void LoadLobby()
    {
        Debug.Log("[JoinSessionScreen] Connected. Loading Lobby.");
        SceneManager.LoadScene(lobbySceneName);
    }
}