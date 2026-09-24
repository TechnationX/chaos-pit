using System.Collections;
using FishNet;
using FishNet.Object;
using UnityEngine;

/// <summary>
/// Persistent (DontDestroyOnLoad) full-screen overlay shown while joining/loading
/// into the Lobby. Stays up until the client's own catch-up NetworkObject spawning
/// (furniture, chess pieces, pool/bowling setups, etc.) actually settles, instead
/// of hiding as soon as the Unity scene itself finishes loading. The scene load is
/// fast — the slow part is a joining client instantiating everything the server
/// already had set up before they connected, which used to leave players staring
/// at whatever was on screen beforehand with no feedback that anything was still
/// happening (see LobbySpawner.PrewarmConvexMeshColliders for the other half of
/// this fix — it cuts down how long that catch-up actually takes).
/// </summary>
public class LobbyLoadingScreen : SingletonBehaviour<LobbyLoadingScreen>
{
    [Header("UI")]
    [SerializeField] private CanvasGroup _canvasGroup;

    [Header("Timing")]
    [Tooltip("Time to wait, doing nothing, right after Show() before watching for spawns to settle — covers the brief calm before the catch-up burst actually starts, so that calm isn't mistaken for \"already done\".")]
    [SerializeField] private float _initialGraceSeconds = 1f;
    [Tooltip("How long the client must go without a new NetworkObject spawning before the load is considered settled.")]
    [SerializeField] private float _settleDebounceSeconds = 0.75f;
    [Tooltip("Absolute cap — hides the screen even if spawns are still trickling in, so a stall can never leave the player stuck forever.")]
    [SerializeField] private float _maxWaitSeconds = 30f;

    [Header("Fade")]
    [SerializeField] private float _fadeDuration = 0.25f;

    private Coroutine _activeRoutine;
    private float _lastSpawnTime;

    // Diagnostic-only counter for the intermittent "client hangs on join,
    // stuck on skybox" bug — reset each time ShowAndWaitForSettled() runs,
    // so WaitForSpawnsToSettle's own log can report how many NetworkObjects
    // this client actually received while the screen was up. If this stays
    // at 0 for a hung client — not even the client's own player object —
    // the spawn message(s) never arrived at all, which points at a network
    // delivery/channel stall rather than anything client-side.
    private int _spawnCountThisWait = 0;

    protected override void Awake()
    {
        base.Awake();
        SetInstant(false);
    }

    // Call right before triggering the Lobby scene load — both
    // CreateSessionScreen (host) and JoinSessionScreen (client) call this
    // from their LoadLobby().
    public void ShowAndWaitForSettled()
    {
        Debug.Log("[LobbyLoadingScreen] ShowAndWaitForSettled — loading screen up, waiting for catch-up spawns to settle.");

        if (_activeRoutine != null)
            StopCoroutine(_activeRoutine);

        SetInstant(true);
        _spawnCountThisWait = 0;
        _activeRoutine = StartCoroutine(WaitForSpawnsToSettle());
    }

    // Lets a caller dismiss it early if something else goes wrong (e.g. the
    // connection drops and the app bounces back to Main Menu) instead of
    // leaving it waiting on spawns that are never coming.
    public void HideImmediately()
    {
        if (_activeRoutine != null)
        {
            StopCoroutine(_activeRoutine);
            _activeRoutine = null;
        }
        UnsubscribeFromSpawns();
        SetInstant(false);
    }

    private IEnumerator WaitForSpawnsToSettle()
    {
        SubscribeToSpawns();

        yield return new WaitForSecondsRealtime(_initialGraceSeconds);

        // Reset the baseline after the grace period — if nothing happened to
        // spawn during the grace window itself, that silence shouldn't count
        // against the debounce before the burst has even begun.
        _lastSpawnTime = Time.unscaledTime;

        float startTime = Time.unscaledTime;
        while (Time.unscaledTime - startTime < _maxWaitSeconds)
        {
            if (Time.unscaledTime - _lastSpawnTime >= _settleDebounceSeconds)
                break;
            yield return null;
        }

        // Diagnostic for the intermittent "client hangs on join, stuck on
        // skybox" bug — tells us whether this loading screen actually
        // settled normally (spawns arrived, then went quiet for
        // _settleDebounceSeconds) or got dragged all the way to the 30s
        // hard cap and faded out anyway, which is exactly what would make a
        // broken/incomplete lobby (no furniture, no local player camera,
        // etc.) suddenly reveal itself as "stuck on skybox" instead of
        // staying on the loading screen.
        bool hitHardCap = Time.unscaledTime - startTime >= _maxWaitSeconds;
        Debug.Log($"[LobbyLoadingScreen] WaitForSpawnsToSettle finished — {(hitHardCap ? "HIT THE 30s HARD CAP (spawns never settled)" : "settled normally")}, objects observed: {_spawnCountThisWait}, elapsed: {Time.unscaledTime - startTime:F1}s.");

        UnsubscribeFromSpawns();
        yield return StartCoroutine(Fade(false));
        _activeRoutine = null;
    }

    private void SubscribeToSpawns()
    {
        _lastSpawnTime = Time.unscaledTime;
        var clientObjects = InstanceFinder.ClientManager != null ? InstanceFinder.ClientManager.Objects : null;
        if (clientObjects != null)
            clientObjects.OnSpawnedAdd += HandleObjectSpawned;
    }

    private void UnsubscribeFromSpawns()
    {
        var clientObjects = InstanceFinder.ClientManager != null ? InstanceFinder.ClientManager.Objects : null;
        if (clientObjects != null)
            clientObjects.OnSpawnedAdd -= HandleObjectSpawned;
    }

    private void HandleObjectSpawned(int objectId, NetworkObject networkObject)
    {
        _lastSpawnTime = Time.unscaledTime;
        _spawnCountThisWait++;
    }

    private void SetInstant(bool visible)
    {
        if (_canvasGroup == null) return;
        _canvasGroup.gameObject.SetActive(visible);
        _canvasGroup.alpha = visible ? 1f : 0f;
        _canvasGroup.interactable = visible;
        _canvasGroup.blocksRaycasts = visible;
    }

    private IEnumerator Fade(bool fadeIn)
    {
        if (_canvasGroup == null) yield break;

        if (fadeIn) _canvasGroup.gameObject.SetActive(true);

        float start = _canvasGroup.alpha;
        float target = fadeIn ? 1f : 0f;
        float elapsed = 0f;

        while (elapsed < _fadeDuration)
        {
            elapsed += Time.unscaledDeltaTime;
            _canvasGroup.alpha = Mathf.Lerp(start, target, elapsed / _fadeDuration);
            yield return null;
        }

        _canvasGroup.alpha = target;
        _canvasGroup.interactable = fadeIn;
        _canvasGroup.blocksRaycasts = fadeIn;

        if (!fadeIn) _canvasGroup.gameObject.SetActive(false);
    }

    private void OnDestroy()
    {
        UnsubscribeFromSpawns();
    }
}
