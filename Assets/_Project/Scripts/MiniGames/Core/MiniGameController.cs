// MiniGameController.cs

using FishNet.Object;
using System.Collections;
using FishNet.Connection;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

public abstract class MiniGameController : MonoBehaviour
{
    [Header("Spawn Points")]
    [SerializeField] protected Transform[] _spawnPoints;
    public Transform[] SpawnPoints => _spawnPoints;

    protected List<PlayerObject> _players = new List<PlayerObject>();
    protected int _currentRound = 0;
    protected bool _gameActive = false;

    // Which station this controller instance belongs to. Set once by
    // GameRoomManager.StartGameAfterLoad right after it finds this
    // controller, before StartGame() runs. Every subclass call site that
    // reports an in-round network message (round starts, timer syncs,
    // eliminations, holder/turn changes, tile updates, game-over payloads)
    // passes this back into GameRoomManager.RpcMinigameMessage so the
    // server knows which station's session to fan the message out to —
    // see RpcMinigameMessage's own comment in GameRoomManager.cs for why
    // that's necessary (an unscoped lookup on the receiving end used to
    // resolve broadcasts to whichever controller instance happened to load
    // first, merging concurrent same-type games together on a host).
    public int StationIndex { get; private set; } = -1;

    public void SetStationIndex(int stationIndex)
    {
        StationIndex = stationIndex;
    }


    [Header("Results Screen")]
    [SerializeField] protected GameObject _resultsScreenPanel;
    [SerializeField] protected float _resultsDuration = 10f;

    private ResultsScreenUI _resultsUI;

    // Cached lookup of the ResultsScreenUI living on _resultsScreenPanel (see
    // ResultsCanvas.prefab). Every minigame shares the same prefab instance,
    // so no per-controller Inspector wiring is needed.
    protected ResultsScreenUI ResultsUI
    {
        get
        {
            if (_resultsUI == null && _resultsScreenPanel != null)
                _resultsUI = _resultsScreenPanel.GetComponent<ResultsScreenUI>();
            return _resultsUI;
        }
    }

    // --- Required overrides ---

    /// Called by GameRoomManager when all players are loaded into the scene.
    public abstract void StartGame(List<PlayerObject> players);

    /// Called each time a round begins.
    public abstract void StartRound();

    /// Called when win condition is met or timer expires.
    public abstract void EndRound();

    /// Returns ordered list of players by standing for current round.
    public abstract List<RoundResult> GetResults();

    /// Called by GameRoomManager after results are displayed and before scene unload.
    public abstract void CleanUp();

    public virtual void ClientInit() { }

    // --- Base helpers available to all mini games ---

    /// Teleports all players to this scene's spawn points.
    protected virtual void TeleportPlayersToSpawns()
    {
        for (int i = 0; i < _players.Count; i++)
        {
            if (_spawnPoints == null || _spawnPoints.Length == 0)
            {
                Debug.LogWarning("[MiniGameController] No spawn points assigned.");
                return;
            }

            int spawnIndex = i % _spawnPoints.Length;
            _players[i].transform.position = _spawnPoints[spawnIndex].position;
            _players[i].transform.rotation = _spawnPoints[spawnIndex].rotation;
        }
    }

    /// Removes a player from the game gracefully (disconnect, elimination, etc.)
    public virtual void RemovePlayer(PlayerObject player)
    {
        if (_players.Contains(player))
        {
            _players.Remove(player);
            Debug.Log($"[MiniGameController] Player removed: {player.name}, remaining: {_players.Count}");
        }
    }

    /// Builds a result label string from standing value.
    protected string GetResultLabel(int standing)
    {
        return standing switch
        {
            1 => "Winner",
            2 => "2nd Place",
            3 => "3rd Place",
            _ => $"{standing}th Place"
        };
    }

    protected void UnlockAllPlayers()
    {
        foreach (PlayerObject player in _players)
        {
            player.Movement.ClearAllMovementLocks();
            player.Interaction.SetInteractionEnabled(false); // keep interactions off in minigame
        }
    }

    // Called by GameRoomManager after scores are processed. This only ever
    // runs on the server's own controller instance (GameRoomManager.OnGameComplete
    // calls it as a direct method call inside a [Server]-tagged method, not
    // an RPC) — it's the path responsible for telling GameRoomManager to
    // return players to the lobby once the results screen finishes.
    //
    // This used to ALSO show the results panel directly, and relied on
    // ResultsUI.Populate's own countdown coroutine (which requires the panel's
    // GameObject to be active to run at all) to eventually fire the
    // return-to-lobby signal. That coupling was the actual bug: this method
    // runs on the SERVER's own process, which — on a host — is the same
    // process as the host's own screen, so showing the panel unconditionally
    // here meant an uninvolved host always saw this station's results screen
    // (and whatever was rendered underneath it) for the full results
    // duration, exactly like the intro-screen and camera bugs fixed earlier.
    //
    // Now split: the return-to-lobby timer runs as its own plain coroutine
    // below, with no UI or GameObject involved at all, so it safely runs on
    // every process regardless of who's watching. The actual panel only gets
    // shown if the LOCAL viewer of this process is really one of this game's
    // players — checked via PlayerObject.IsOwner, which is only ever true on
    // the one process that actually owns that specific player object. That's
    // reliable for a host exactly the same way it's already reliable for a
    // remote client (see PlayerCamera.Initialize's own IsOwner check).
    public void ShowResults(ResultsData data)
    {
        if (_players.Any(p => p != null && p.IsOwner))
        {
            OnShowResults(data);
            if (_resultsScreenPanel != null) _resultsScreenPanel.SetActive(true);
            if (ResultsUI != null) ResultsUI.Populate(data, _resultsDuration, OnResultsHidden);
        }

        if (_resultsTimerCoroutine != null) StopCoroutine(_resultsTimerCoroutine);
        _resultsTimerCoroutine = StartCoroutine(ResultsTimerCoroutine());
    }

    private Coroutine _resultsTimerCoroutine;

    // Server-authoritative return-to-lobby timer, decoupled from any UI —
    // see ShowResults's comment above for why this can no longer ride on
    // ResultsUI's own countdown coroutine.
    private IEnumerator ResultsTimerCoroutine()
    {
        yield return new WaitForSeconds(_resultsDuration);
        GameRoomManager.Instance.OnResultsDismissed(this);
    }

    // Client-only display path: pure clients learn the game ended through a
    // results broadcast (e.g. "bt_game_over" / "tm_game_over") rather than
    // through ShowResults, so they show the same row layout and countdown
    // locally without re-triggering the return-to-lobby flow (that flow is
    // now entirely owned by ShowResults's own timer above, regardless of
    // whether this method or ShowResults is the one actually populating the
    // panel on a given process).
    protected void ShowResultsClientOnly(ResultsData data)
    {
        OnShowResults(data);

        if (_resultsScreenPanel != null) _resultsScreenPanel.SetActive(true);
        if (ResultsUI != null) ResultsUI.Populate(data, _resultsDuration, OnResultsHidden);
    }

    // Override in subclass for any game-specific UI change made the moment
    // results are shown (e.g. hiding the in-round HUD/score panel).
    protected virtual void OnShowResults(ResultsData data) { }

    // Override in subclass for any game-specific UI change made once the
    // results screen finishes and hides itself (e.g. restoring the HUD).
    protected virtual void OnResultsHidden() { }

    // Call this from subclass when results timer expires
    protected void NotifyResultsDismissed()
    {
        GameRoomManager.Instance.OnResultsDismissed(this);
    }

    /// Shared results-countdown timer used by minigame results screens that
    /// have not yet been migrated onto ResultsScreenUI (currently only
    /// StubMiniGame — out of scope for the ResultsCanvas consolidation).
    /// Counts down _resultsDuration on the given text field.
    ///
    /// Pass notifyDismissal: true ONLY for the instance responsible for
    /// telling GameRoomManager to return players to the lobby — this is
    /// the server-authoritative path reached via OnShowResults (which only
    /// ever runs on the server's own controller instance, since
    /// GameRoomManager.OnGameComplete calls ShowResults() as a direct
    /// method call inside a [Server]-tagged method, not an RPC).
    ///
    /// Pure clients learn the game ended through a results broadcast
    /// instead (e.g. "bt_game_over" / "tm_game_over") and should pass
    /// notifyDismissal: false — they display the countdown locally without
    /// double-triggering the return-to-lobby flow. On a host (server+client
    /// in the same process), both paths run on the same object; the
    /// notifyDismissal: false call simply re-displays the same countdown,
    /// which is harmless.
    protected IEnumerator ResultsCountdownCoroutine(TextMeshProUGUI countdownText, bool notifyDismissal, System.Action onComplete = null)
    {
        float remaining = _resultsDuration;
        while (remaining > 0f)
        {
            if (countdownText != null)
                countdownText.text = $"Returning in {Mathf.CeilToInt(remaining)}...";
            yield return new WaitForSeconds(1f);
            remaining -= 1f;
        }

        if (countdownText != null) countdownText.text = string.Empty;

        onComplete?.Invoke();

        if (notifyDismissal)
        {
            if (_resultsScreenPanel != null) _resultsScreenPanel.SetActive(false);
            GameRoomManager.Instance.OnResultsDismissed(this);
        }
    }

    /// Called by GameRoomManager.RpcMinigameMessage on all clients.
    /// Override in subclass to handle minigame-specific network messages.
    /// <param name="messageType">Identifies what kind of data this is e.g. "colors", "tiles"</param>
    /// <param name="payload">JSON string — parse with JsonUtility or manually</param>
    public virtual void OnNetworkMessage(string messageType, string payload) { }

    /// Called on the server when a client sends a RequestMinigameAction.
    /// Override in subclass to handle client-originated requests (e.g. kill confirm, shove).
    public virtual void OnClientAction(string messageType, string payload, NetworkConnection sender) { }

}
