// MiniGameController.cs

using System.Collections.Generic;
using FishNet.Object;
using UnityEngine;

public abstract class MiniGameController : NetworkBehaviour
{
    [Header("Spawn Points")]
    [SerializeField] protected Transform[] _spawnPoints;

    protected List<PlayerObject> _players = new List<PlayerObject>();
    protected int _currentRound = 0;
    protected bool _gameActive = false;

    // --- Required overrides ---

    /// <summary>
    /// Called by GameRoomManager when all players are loaded into the scene.
    /// </summary>
    public abstract void StartGame(List<PlayerObject> players);

    /// <summary>
    /// Called each time a round begins.
    /// </summary>
    public abstract void StartRound();

    /// <summary>
    /// Called when win condition is met or timer expires.
    /// </summary>
    public abstract void EndRound();

    /// <summary>
    /// Returns ordered list of players by standing for current round.
    /// </summary>
    public abstract List<RoundResult> GetResults();

    /// <summary>
    /// Called by GameRoomManager after results are displayed and before scene unload.
    /// </summary>
    public abstract void CleanUp();

    // --- Base helpers available to all mini games ---

    /// <summary>
    /// Teleports all players to this scene's spawn points.
    /// </summary>
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

    /// <summary>
    /// Removes a player from the game gracefully (disconnect, elimination, etc.)
    /// </summary>
    public virtual void RemovePlayer(PlayerObject player)
    {
        if (_players.Contains(player))
        {
            _players.Remove(player);
            Debug.Log($"[MiniGameController] Player removed: {player.name}, remaining: {_players.Count}");
        }
    }

    /// <summary>
    /// Builds a result label string from standing value.
    /// </summary>
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
}