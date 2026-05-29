// ScoreManager.cs

using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class ScoreManager : MonoBehaviour
{
    public static ScoreManager Instance { get; private set; }

    // Each game room gets its own score table — keyed by sessionId
    // Inner dictionary: clientId → total points earned this session
    private Dictionary<string, Dictionary<int, int>> _sessionScores
        = new Dictionary<string, Dictionary<int, int>>();

    // Queue per session to handle rapid result submissions
    private Dictionary<string, Queue<List<RoundResult>>> _processingQueues
        = new Dictionary<string, Queue<List<RoundResult>>>();

    private Dictionary<string, bool> _processingActive
        = new Dictionary<string, bool>();

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    // --- Session Lifecycle ---

    /// <summary>
    /// Called by GameRoomManager when a session starts.
    /// </summary>
    public void RegisterSession(string sessionId)
    {
        if (_sessionScores.ContainsKey(sessionId))
        {
            Debug.LogWarning($"[ScoreManager] Session {sessionId} already registered.");
            return;
        }

        _sessionScores[sessionId] = new Dictionary<int, int>();
        _processingQueues[sessionId] = new Queue<List<RoundResult>>();
        _processingActive[sessionId] = false;

        Debug.Log($"[ScoreManager] Session registered: {sessionId}");
    }

    /// <summary>
    /// Called by GameRoomManager when a session ends and players return to lobby.
    /// </summary>
    public void UnregisterSession(string sessionId)
    {
        if (!_sessionScores.ContainsKey(sessionId))
        {
            Debug.LogWarning($"[ScoreManager] Session {sessionId} not found on unregister.");
            return;
        }

        _sessionScores.Remove(sessionId);
        _processingQueues.Remove(sessionId);
        _processingActive.Remove(sessionId);

        Debug.Log($"[ScoreManager] Session unregistered: {sessionId}");
    }

    // --- Results Processing ---

    /// <summary>
    /// Called by GameRoomManager after each round ends.
    /// Enqueues results for processing — safe to call rapidly or from multiple sessions.
    /// </summary>
    public void SubmitResults(string sessionId, List<RoundResult> results)
    {
        if (!_processingQueues.ContainsKey(sessionId))
        {
            Debug.LogWarning($"[ScoreManager] SubmitResults — session {sessionId} not registered.");
            return;
        }

        _processingQueues[sessionId].Enqueue(results);
        Debug.Log($"[ScoreManager] Results queued for session {sessionId} — queue depth: {_processingQueues[sessionId].Count}");

        if (!_processingActive[sessionId])
            StartCoroutine(ProcessQueue(sessionId));
    }

    private IEnumerator ProcessQueue(string sessionId)
    {
        _processingActive[sessionId] = true;

        while (_processingQueues[sessionId].Count > 0)
        {
            List<RoundResult> results = _processingQueues[sessionId].Dequeue();
            ProcessResults(sessionId, results);
            yield return null; // spread across frames if queue is deep
        }

        _processingActive[sessionId] = false;
    }

    private void ProcessResults(string sessionId, List<RoundResult> results)
    {
        if (results == null || results.Count == 0)
        {
            Debug.LogWarning($"[ScoreManager] ProcessResults — empty results for session {sessionId}.");
            return;
        }

        foreach (RoundResult result in results)
        {
            if (result.Player == null)
            {
                Debug.LogWarning("[ScoreManager] RoundResult has null player — skipping.");
                continue;
            }

            int clientId = result.Player.Owner.ClientId;

            if (!_sessionScores[sessionId].ContainsKey(clientId))
                _sessionScores[sessionId][clientId] = 0;

            _sessionScores[sessionId][clientId] += result.ScoreAwarded;

            // Write to career score
            PlayerProfileManager.Instance.AddCareerScore(result.Player.Owner, result.ScoreAwarded);

            Debug.Log($"[ScoreManager] [{sessionId}] {result.Player.name} — {result.ResultLabel}, " +
                      $"+{result.ScoreAwarded}pts, session total: {_sessionScores[sessionId][clientId]}");
        }

        // TODO: BACKEND — push updated career scores to database after each round
    }

    // --- Leaderboard ---

    /// <summary>
    /// Returns sorted leaderboard for a specific game room session.
    /// Used by Results Screen.
    /// </summary>
    public List<SessionLeaderboardEntry> GetSessionLeaderboard(string sessionId)
    {
        if (!_sessionScores.ContainsKey(sessionId))
        {
            Debug.LogWarning($"[ScoreManager] GetSessionLeaderboard — session {sessionId} not found.");
            return new List<SessionLeaderboardEntry>();
        }

        var leaderboard = new List<SessionLeaderboardEntry>();

        foreach (var kvp in _sessionScores[sessionId])
        {
            PlayerProfile profile = PlayerProfileManager.Instance.GetProfile(kvp.Key);
            string displayName = profile != null ? profile.DisplayName : $"Player_{kvp.Key}";

            leaderboard.Add(new SessionLeaderboardEntry
            {
                ClientId = kvp.Key,
                DisplayName = displayName,
                SessionScore = kvp.Value,
                CareerScore = profile != null ? profile.CareerScore : 0
            });
        }

        leaderboard.Sort((a, b) => b.SessionScore.CompareTo(a.SessionScore));

        // Assign standings after sort
        for (int i = 0; i < leaderboard.Count; i++)
            leaderboard[i].Standing = i + 1;

        return leaderboard;
    }

    /// <summary>
    /// Returns sorted career score leaderboard across all currently connected players.
    /// Used by lobby leaderboard display.
    /// </summary>
    public List<SessionLeaderboardEntry> GetCareerLeaderboard()
    {
        var leaderboard = new List<SessionLeaderboardEntry>();
        var profiles = PlayerProfileManager.Instance.GetAllProfiles();

        foreach (var profile in profiles)
        {
            leaderboard.Add(new SessionLeaderboardEntry
            {
                ClientId = profile.ClientId,
                DisplayName = profile.DisplayName,
                SessionScore = 0,
                CareerScore = profile.CareerScore
            });
        }

        leaderboard.Sort((a, b) => b.CareerScore.CompareTo(a.CareerScore));

        for (int i = 0; i < leaderboard.Count; i++)
            leaderboard[i].Standing = i + 1;

        // TODO: BACKEND — merge with all-time scores from database here
        return leaderboard;
    }

    // --- Accessors ---

    public int GetSessionScore(string sessionId, int clientId)
    {
        if (!_sessionScores.ContainsKey(sessionId)) return 0;
        _sessionScores[sessionId].TryGetValue(clientId, out int score);
        return score;
    }
}

/// <summary>
/// Single entry in a leaderboard — used for both session and career displays.
/// </summary>
[System.Serializable]
public class SessionLeaderboardEntry
{
    public int ClientId;
    public string DisplayName;
    public int SessionScore;
    public int CareerScore;
    public int Standing;
}