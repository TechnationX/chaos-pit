// UIScreenBase.cs
// Place in: Assets/_Project/Scripts/UI/
// Base class for all UI screens.
// Phase 1: Show/Hide via SetActive (instant).
// Phase 2: Drop in CanvasGroup fade here without touching any child screen scripts.
// Back button: assign in Inspector on each screen. Calls ShowMainMenu() on MainMenuManager.

using UnityEngine;
using UnityEngine.UI;
using System.Collections;

[RequireComponent(typeof(CanvasGroup))]
public class UIScreenBase : MonoBehaviour
{
    // ─── Config ───────────────────────────────────────────────────────────────

    [Header("Transition")]
    [SerializeField] private bool useFade = false;
    [SerializeField] private float fadeDuration = 0.25f;

    [Header("Navigation")]
    [SerializeField] private Button backButton;  // Assign in Inspector on each screen

    // ─── State ────────────────────────────────────────────────────────────────

    public bool IsVisible { get; private set; } = false;

    private CanvasGroup _canvasGroup;
    private Coroutine _fadeCoroutine;
    private MainMenuManager _mainMenuManager;

    // ─── Lifecycle ────────────────────────────────────────────────────────────

    protected virtual void Awake()
    {
        _canvasGroup = GetComponent<CanvasGroup>();
        _mainMenuManager = FindFirstObjectByType<MainMenuManager>();

        if (backButton != null)
        {
            backButton.onClick.AddListener(OnBackPressed);
        }

        if (!IsVisible)
        {
            SetVisibility(false, instant: true);
        }
    }

    // ─── Public API ───────────────────────────────────────────────────────────

    public void Show() => SetVisibility(true, instant: !useFade);
    public void Hide() => SetVisibility(false, instant: !useFade);

    // ─── Visibility Control ───────────────────────────────────────────────────

    private void SetVisibility(bool visible, bool instant)
    {
        IsVisible = visible;

        if (instant)
        {
            _canvasGroup.alpha = visible ? 1f : 0f;
            _canvasGroup.interactable = visible;
            _canvasGroup.blocksRaycasts = visible;
            gameObject.SetActive(visible);
        }
        else
        {
            if (_fadeCoroutine != null) StopCoroutine(_fadeCoroutine);
            _fadeCoroutine = StartCoroutine(Fade(visible));
        }
    }

    private IEnumerator Fade(bool fadeIn)
    {
        float startAlpha = fadeIn ? 0f : 1f;
        float targetAlpha = fadeIn ? 1f : 0f;
        float elapsed = 0f;

        if (fadeIn)
        {
            gameObject.SetActive(true);
            _canvasGroup.interactable = false;
            _canvasGroup.blocksRaycasts = false;
        }

        while (elapsed < fadeDuration)
        {
            elapsed += Time.deltaTime;
            _canvasGroup.alpha = Mathf.Lerp(startAlpha, targetAlpha, elapsed / fadeDuration);
            yield return null;
        }

        _canvasGroup.alpha = targetAlpha;

        if (!fadeIn)
        {
            gameObject.SetActive(false);
        }
        else
        {
            _canvasGroup.interactable = true;
            _canvasGroup.blocksRaycasts = true;
        }

        _fadeCoroutine = null;
    }

    // ─── Navigation ───────────────────────────────────────────────────────────

    // Override in child screens that need custom back behavior (e.g. CreateSessionScreen)
    protected virtual void OnBackPressed()
    {
        _mainMenuManager?.ShowMainMenu();
    }

    // ─── Overrideable Hooks ───────────────────────────────────────────────────

    public virtual void OnShow() { }
    public virtual void OnHide() { }
}