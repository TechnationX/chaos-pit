// MainMenuManager.cs
// Owns: button wiring, screen transitions, player name and level display.
// Attach to: MainMenuManager GameObject in Main Menu scene.

using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class MainMenuManager : MonoBehaviour
{
    // ─── Screen References ────────────────────────────────────────────────────

    [Header("Screens")]
    [SerializeField] private UIScreenBase mainMenuScreen;
    [SerializeField] private UIScreenBase createSessionScreen;
    [SerializeField] private UIScreenBase joinSessionScreen;
    [SerializeField] private UIScreenBase customizationScreen;  // stub
    [SerializeField] private UIScreenBase settingsScreen;       // stub

    // ─── Player Info ──────────────────────────────────────────────────────────

    [Header("Player Info")]
    [SerializeField] private TMP_Text playerNameText;
    [SerializeField] private TMP_Text playerLevelText;

    // ─── Buttons ──────────────────────────────────────────────────────────────

    [Header("Main Menu Buttons")]
    [SerializeField] private Button createSessionButton;
    [SerializeField] private Button joinSessionButton;
    [SerializeField] private Button customizationButton;
    [SerializeField] private Button settingsButton;
    [SerializeField] private Button quitButton;

    // ─── State ────────────────────────────────────────────────────────────────

    private UIScreenBase _currentScreen;

    // ─── Lifecycle ────────────────────────────────────────────────────────────

    private void Start()
    {
        WireButtons();
        LoadPlayerInfo();
        ShowScreen(mainMenuScreen);
    }

    // ─── Button Wiring ────────────────────────────────────────────────────────

    private void WireButtons()
    {
        createSessionButton.onClick.AddListener(() => ShowScreen(createSessionScreen));
        joinSessionButton.onClick.AddListener(() => ShowScreen(joinSessionScreen));
        customizationButton.onClick.AddListener(() => ShowScreen(customizationScreen));
        settingsButton.onClick.AddListener(() => ShowScreen(settingsScreen));
        quitButton.onClick.AddListener(() => QuitGame());
    }

    // ─── Player Info ──────────────────────────────────────────────────────────

    private void LoadPlayerInfo()
    {
        if (PlayerDataManager.Instance == null)
        {
            Debug.LogWarning("[MainMenuManager] PlayerDataManager not found. Is Bootstrap scene loaded?");
            return;
        }

        PlayerData data = PlayerDataManager.Instance.Data;
        playerNameText.text = data.displayName;
        playerLevelText.text = $"Level {data.level}";
    }

    // ─── Screen Management ────────────────────────────────────────────────────

    public void ShowScreen(UIScreenBase screen)
    {
        Debug.Log($"[MainMenuManager] ShowScreen called with: {(screen == null ? "NULL" : screen.gameObject.name)}");

        if (screen == null)
        {
            Debug.LogWarning("[MainMenuManager] Tried to show a null screen.");
            return;
        }

        // Hide current screen
        if (_currentScreen != null && _currentScreen != screen)
        {
            _currentScreen.OnHide();
            _currentScreen.Hide();
        }

        // Show new screen
        _currentScreen = screen;
        _currentScreen.Show();
        _currentScreen.OnShow();

        Debug.Log($"[MainMenuManager] Showing screen: {screen.gameObject.name}");
    }

    // Back button — called by child screens to return to Main Menu
    public void ShowMainMenu()
    {
        ShowScreen(mainMenuScreen);
    }

    // ─── Quit ─────────────────────────────────────────────────────────────────

    private void QuitGame()
    {
        Debug.Log("[MainMenuManager] Quitting application.");
        #if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
        #else
            Application.Quit();
        #endif
    }
}