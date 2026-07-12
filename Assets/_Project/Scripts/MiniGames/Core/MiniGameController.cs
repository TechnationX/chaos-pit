// MiniGameController.cs

using FishNet.Object;
using System.Collections;
using FishNet.Connection;
using System.Collections.Generic;
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


    [Header("Results Screen")]
    [SerializeField] protected GameObject _resultsScreenPanel;
    [SerializeField] protected float _resultsDuration = 10f;

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

    // Called by GameRoomManager after scores are processed
    public void ShowResults(ResultsData data)
    {
        OnShowResults(data);
    }

    // Override in subclass to populate UI and start dismiss timer
    protected virtual void OnShowResults(ResultsData data)
    {
        // Default — subclass should override this entirely
        Debug.LogWarning("[MiniGameController] OnShowResults not implemented in subclass.");
        GameRoomManager.Instance.OnResultsDismissed(this);
    }

    // Call this from subclass when results timer expires
    protected void NotifyResultsDismissed()
    {
        GameRoomManager.Instance.OnResultsDismissed(this);
    }

    // Override to update countdown display each tick
    protected virtual void OnResultsTimerTick(float secondsRemaining) { }

    // Called when timer expires — notifies GameRoomManager to return players
    protected virtual void OnResultsDismissed()
    {
        if (_resultsScreenPanel != null)
            _resultsScreenPanel.SetActive(false);

        GameRoomManager.Instance.OnResultsDismissed(this);
    }

    /// Shared results-countdown timer used by minigame results screens.
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