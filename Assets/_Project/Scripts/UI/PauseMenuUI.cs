// PauseMenuUI.cs

using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

public class PauseMenuUI : MonoBehaviour
{
    public static PauseMenuUI Instance { get; private set; }

    [Header("UI")]
    [SerializeField] private GameObject _panel;
    [SerializeField] private Button _resumeButton;
    [SerializeField] private Button _quitToMenuButton;

    private void Awake()
    {
        Instance = this;
        _panel.SetActive(false);

        _resumeButton.onClick.AddListener(OnResume);
        _quitToMenuButton.onClick.AddListener(OnQuitToMenu);
    }

    private void OnDestroy()
    {
        Instance = null;
    }

    public void SetVisible(bool visible)
    {
        _panel.SetActive(visible);
    }

    private void OnResume()
    {
        var player = FindFirstObjectByType<PlayerCamera>();
        player?.Unpause();
    }

    private void OnQuitToMenu()
    {
        SessionManager.Instance?.EndSession();
        DisconnectReason.Instance?.Set(string.Empty);
        SceneManager.LoadScene("MainMenu");
    }
}