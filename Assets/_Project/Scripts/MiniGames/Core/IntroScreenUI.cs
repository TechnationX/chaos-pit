// IntroScreenUI.cs
// Shared intro/rules screen, shown by GameRoomManager right after all
// players finish loading a minigame scene and BEFORE it calls
// controller.StartGame() — so no round-timer time is ever burned while
// players are reading it. Lives on IntroCanvas.prefab's IntroPanel,
// instanced identically in every minigame scene (same pattern as
// ResultsCanvas.prefab / ResultsScreenUI.cs).
//
// Fully server-driven: Show()/Hide() are only ever called from
// GameRoomManager's RpcShowIntroScreen/RpcHideIntroScreen (ObserversRpc),
// and the Skip button only sends a request to the server — it never hides
// itself locally, so every client dismisses in lockstep.

using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class IntroScreenUI : MonoBehaviour
{
    [Header("Refs")]
    [SerializeField] private TextMeshProUGUI _titleText;
    [SerializeField] private TextMeshProUGUI _rulesText;
    [SerializeField] private TextMeshProUGUI _countdownText;
    [SerializeField] private Button _skipButton;

    private Coroutine _countdownCoroutine;

    private void Awake()
    {
        // No SetActive(false) here on purpose. The per-scene IntroCanvas
        // PrefabInstance already carries an m_IsActive: 0 override on this
        // panel, so it's already hidden at scene load without any code.
        // Unity does NOT call Awake() on a GameObject that starts inactive —
        // it's deferred until the object's first activation, which is this
        // script's own Show() calling SetActive(true). Calling
        // SetActive(false) here used to fire synchronously inside that same
        // SetActive(true) call and immediately undo it, so the panel never
        // actually rendered a frame — that was the bug behind "intro screen
        // showed nothing."
        if (_skipButton != null)
            _skipButton.onClick.AddListener(RequestSkip);
    }

    public void Show(string title, string rulesText, float duration)
    {
        gameObject.SetActive(true);

        if (_titleText != null) _titleText.text = title;
        if (_rulesText != null) _rulesText.text = rulesText;

        if (_countdownCoroutine != null) StopCoroutine(_countdownCoroutine);
        _countdownCoroutine = StartCoroutine(CountdownCoroutine(duration));

        // The Skip button needs a visible, unlocked cursor to click — at
        // this point in the flow the local player's camera is still
        // whatever it was left on from the lobby (first-person, cursor
        // locked+hidden), since RpcReinitializeCamera/the minigame camera
        // switch doesn't run until after this screen closes.
        FindLocalPlayer()?.Camera.ReleaseCursor();
    }

    public void Hide()
    {
        if (_countdownCoroutine != null)
        {
            StopCoroutine(_countdownCoroutine);
            _countdownCoroutine = null;
        }

        gameObject.SetActive(false);

        // Re-lock — the minigame camera switch that follows right after
        // this (GameRoomManager.RpcReinitializeCamera) also locks the
        // cursor itself, but locking here too covers the brief window
        // before that RPC arrives, and the (currently unused) case where
        // Hide() fires without a camera switch following it.
        FindLocalPlayer()?.Camera.LockCursor();
    }

    private IEnumerator CountdownCoroutine(float duration)
    {
        float remaining = duration;
        while (remaining > 0f)
        {
            if (_countdownText != null)
                _countdownText.text = $"Starting in {Mathf.CeilToInt(remaining)}...";
            yield return new WaitForSeconds(1f);
            remaining -= 1f;
        }

        if (_countdownText != null) _countdownText.text = string.Empty;
    }

    private void RequestSkip()
    {
        PlayerObject local = FindLocalPlayer();
        if (local != null) GameRoomManager.Instance?.RequestSkipIntro(local);
    }

    private PlayerObject FindLocalPlayer()
    {
        foreach (var p in FindObjectsByType<PlayerObject>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (p.IsOwner) return p;
        return null;
    }
}
