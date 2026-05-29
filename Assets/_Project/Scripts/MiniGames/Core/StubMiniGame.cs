// StubMiniGame.cs

using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class StubMiniGame : MiniGameController
{
    [Header("Stub Settings")]
    [SerializeField] private float _roundDuration = 5f;
    [SerializeField] private int _totalRounds = 2;

    private List<RoundResult> _lastResults = new List<RoundResult>();

    public override void StartGame(List<PlayerObject> players)
    {
        _players = new List<PlayerObject>(players);
        _currentRound = 0;
        _gameActive = true;

        Debug.Log($"[StubMiniGame] StartGame — {_players.Count} players");
        TeleportPlayersToSpawns();
        UnlockAllPlayers();
        StartRound();
    }

    public override void StartRound()
    {
        _currentRound++;
        Debug.Log($"[StubMiniGame] StartRound {_currentRound}");
        StartCoroutine(RoundCoroutine());
    }

    private IEnumerator RoundCoroutine()
    {
        yield return new WaitForSeconds(_roundDuration);
        EndRound();
    }

    public override void EndRound()
    {
        Debug.Log($"[StubMiniGame] EndRound {_currentRound}");
        _lastResults = GetResults();

        foreach (var result in _lastResults)
            Debug.Log($"[StubMiniGame] {result.Player.name} — {result.ResultLabel}, +{result.ScoreAwarded}pts");

        if (_currentRound < _totalRounds)
        {
            StartRound();
        }
        else
        {
            Debug.Log("[StubMiniGame] All rounds complete — notifying GameRoomManager");
            if (_currentRound < _totalRounds)
            {
                StartRound();
            }
            else
            {
                Debug.Log("[StubMiniGame] All rounds complete");
                List<RoundResult> results = GetResults();

                // Find station index by matching this controller to active sessions
                // TODO: pass stationIndex in StartGame instead of searching
                GameRoomManager.Instance.NotifyGameComplete(this, results);
            }
        }
    }

    public override List<RoundResult> GetResults()
    {
        // Shuffle players to simulate random standings
        List<PlayerObject> shuffled = _players.OrderBy(_ => Random.value).ToList();
        List<RoundResult> results = new List<RoundResult>();

        // Scoring baseline from integration guide
        int[] pointTable = new int[] { 10, 8, 6, 4, 2, 1 };

        for (int i = 0; i < shuffled.Count; i++)
        {
            int standing = i + 1;
            int points = i < pointTable.Length ? pointTable[i] : 1;
            string label = GetResultLabel(standing);
            results.Add(new RoundResult(shuffled[i], standing, points, label));
        }

        return results;
    }

    public override void CleanUp()
    {
        _players.Clear();
        _lastResults.Clear();
        _gameActive = false;
        Debug.Log("[StubMiniGame] CleanUp complete");
    }
}