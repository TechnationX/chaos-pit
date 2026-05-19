// SplashScreen.cs
// Controls studio logo and game logo fade sequence.
// Auto-transitions to Main Menu when sequence completes.
// Attach to: SplashScreen GameObject in Splash scene.

using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using System.Collections;

public class SplashScreen : MonoBehaviour
{
    // ─── Inspector ────────────────────────────────────────────────────────────

    [Header("Logo References")]
    [SerializeField] private Image studioLogo;
    [SerializeField] private Image gameLogo;

    [Header("Timing (seconds)")]
    [SerializeField] private float fadeInDuration = 1.0f;
    [SerializeField] private float holdDuration = 1.5f;
    [SerializeField] private float fadeOutDuration = 1.0f;
    [SerializeField] private float gapBetweenLogos = 0.5f;  // pause between studio and game logo

    [Header("Scene")]
    [SerializeField] private string mainMenuSceneName = "MainMenu";

    // ─── Lifecycle ────────────────────────────────────────────────────────────

    private void Start()
    {
        // Validate references before running
        if (studioLogo == null || gameLogo == null)
        {
            Debug.LogError("[SplashScreen] Logo references missing. Assign in Inspector. Skipping to Main Menu.");
            LoadMainMenu();
            return;
        }

        // Start fully transparent
        SetAlpha(studioLogo, 0f);
        SetAlpha(gameLogo, 0f);

        StartCoroutine(PlaySequence());
    }

    // ─── Sequence ─────────────────────────────────────────────────────────────

    private IEnumerator PlaySequence()
    {
        // Studio logo
        yield return StartCoroutine(FadeImage(studioLogo, 0f, 1f, fadeInDuration));
        yield return new WaitForSeconds(holdDuration);
        yield return StartCoroutine(FadeImage(studioLogo, 1f, 0f, fadeOutDuration));

        yield return new WaitForSeconds(gapBetweenLogos);

        // Game logo
        yield return StartCoroutine(FadeImage(gameLogo, 0f, 1f, fadeInDuration));
        yield return new WaitForSeconds(holdDuration);
        yield return StartCoroutine(FadeImage(gameLogo, 1f, 0f, fadeOutDuration));

        LoadMainMenu();
    }

    // ─── Fade Utility ─────────────────────────────────────────────────────────

    private IEnumerator FadeImage(Image image, float fromAlpha, float toAlpha, float duration)
    {
        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            SetAlpha(image, Mathf.Lerp(fromAlpha, toAlpha, elapsed / duration));
            yield return null;
        }

        SetAlpha(image, toAlpha);
    }

    private void SetAlpha(Image image, float alpha)
    {
        Color c = image.color;
        c.a = alpha;
        image.color = c;
    }

    // ─── Transition ───────────────────────────────────────────────────────────

    private void LoadMainMenu()
    {
        // Debug.Log("[SplashScreen] Sequence complete. Loading Main Menu.");
        SceneManager.LoadScene(mainMenuSceneName);
    }
}