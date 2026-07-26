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
    [SerializeField] protected Button backButton;  // Assign in Inspector on each screen

    // ─── State ────────────────────────────────────────────────────────────────

    public bool IsVisible { get; private set; } = false;

    private CanvasGroup _canvasGroup;
    private CanvasGroup CanvasGroupRef => _canvasGroup != null ? _canvasGroup : (_canvasGroup = GetComponent<CanvasGroup>());
    private Coroutine _fadeCoroutine;
    private MainMenuManager _mainMenuManager;

    // ─── Lifecycle ────────────────────────────────────────────────────────────

    protected virtual void Awake()
    {
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
            CanvasGroupRef.alpha = visible ? 1f : 0f;
            CanvasGroupRef.interactable = visible;
            CanvasGroupRef.blocksRaycasts = visible;
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
            CanvasGroupRef.interactable = false;
            CanvasGroupRef.blocksRaycasts = false;
        }

        while (elapsed < fadeDuration)
        {
            elapsed += Time.deltaTime;
            CanvasGroupRef.alpha = Mathf.Lerp(startAlpha, targetAlpha, elapsed / fadeDuration);
            yield return null;
        }

        CanvasGroupRef.alpha = targetAlpha;

        if (!fadeIn)
        {
            gameObject.SetActive(false);
        }
        else
        {
            CanvasGroupRef.interactable = true;
            CanvasGroupRef.blocksRaycasts = true;
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