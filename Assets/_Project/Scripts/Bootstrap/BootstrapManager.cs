// BootstrapManager.cs
// Controls startup initialization order.
// This is NOT a singleton — it runs once and hands off to Main Menu.

using UnityEngine;
using UnityEngine.SceneManagement;
using System.Collections;

public class BootstrapManager : MonoBehaviour
{
    [Header("Scene Names")]
    [SerializeField] private string splashSceneName = "Splash";

    [Header("Startup Delay (optional)")]
    [SerializeField] private float delayBeforeMainMenu = 0.5f;

    private void Start()
    {
        StartCoroutine(Initialize());
    }

    private IEnumerator Initialize()
    {
        // Debug.Log("[BootstrapManager] Starting initialization...");

        // Step 1: PlayerDataManager loads itself in Awake via singleton.
        // Verify it's ready before proceeding.
        if (PlayerDataManager.Instance == null)
        {
            Debug.LogError("[BootstrapManager] PlayerDataManager not found. Is it in the Bootstrap scene?");
            yield break;
        }
        // Debug.Log("[BootstrapManager] PlayerDataManager ready.");
        // Debug.Log($"[BootstrapManager] Save path: {Application.persistentDataPath}");

        // Step 2: AudioManager initializes itself in Awake via singleton.
        if (AudioManager.Instance == null)
        {
            Debug.LogError("[BootstrapManager] AudioManager not found. Is it in the Bootstrap scene?");
            yield break;
        }
        //Debug.Log("[BootstrapManager] AudioManager ready.");

        // Step 3: FishNet NetworkManager initializes itself — no manual call needed.
        // It will be verified here once FishNet is imported.
        // TODO: Add FishNet NetworkManager check after import.
        Debug.Log("[BootstrapManager] NetworkManager check placeholder — add after FishNet import.");

        // Brief pause before scene transition (optional — remove if not needed)
        yield return new WaitForSeconds(delayBeforeMainMenu);

        // Debug.Log($"[BootstrapManager] Loading {mainMenuSceneName}...");
        SceneManager.LoadScene(splashSceneName);
    }
}