// StubMiniGame.cs
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class StubMiniGame : MiniGameController
{
    [Header("Stub Settings")]
    [SerializeField] private float _roundDuration = 60f;

    [Header("Results UI")]
    [SerializeField] private TMPro.TextMeshProUGUI _resultsText;
    [SerializeField] private TMPro.TextMeshProUGUI _countdownText;

    private List<RoundResult> _lastResults = new List<RoundResult>();
    private Dictionary<int, string> _nameMap = new Dictionary<int, string>();

    // ── MiniGameController Overrides ──────────────────────────

    public override void StartGame(List<PlayerObject> players)
    {
        if (!FishNet.InstanceFinder.IsServerStarted) return;

        _players = new List<PlayerObject>(players);
        _currentRound = 0;
        _gameActive = true;

        // Send player identity to all clients
        GameRoomManager.Instance.RpcMinigameMessage("stub_players", BuildPlayersPayload());

        Debug.Log($"[StubMiniGame] StartGame — {_players.Count} players");
        StartRound();
    }

    public override void ClientInit()
    {
        // Nothing to generate client-side for the stub
    }

    public override void StartRound()
    {
        _currentRound++;
        Debug.Log($"[StubMiniGame] StartRound {_currentRound} — duration: {_roundDuration}");

        GameRoomManager.Instance.RpcMinigameMessage("stub_start",
            _roundDuration.ToString(System.Globalization.CultureInfo.InvariantCulture));

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

        GameRoomManager.Instance.RpcMinigameMessage("stub_end", BuildResultsPayload());
        GameRoomManager.Instance.NotifyGameComplete(this, _lastResults);
    }

    public override List<RoundResult> GetResults()
    {
        var results = new List<RoundResult>();
        foreach (PlayerObject player in _players)
            results.Add(new RoundResult(player, 1, 10, "Winner"));
        return results;
    }

    public override void CleanUp()
    {
        _players.Clear();
        _lastResults.Clear();
        _nameMap.Clear();
        _gameActive = false;
        Debug.Log("[StubMiniGame] CleanUp complete");
    }

    // ── Network Message Receiver ──────────────────────────────

    public override void OnNetworkMessage(string messageType, string payload)
    {
        switch (messageType)
        {
            case "stub_players": ApplyPlayersPayload(payload); break;
            case "stub_start":
                if (float.TryParse(payload,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out float duration))
                    OnRoundStart(duration);
                break;
            case "stub_end":
                if (!FishNet.InstanceFinder.IsServerStarted)
                    ApplyResultsPayload(payload);
                break;
        }
    }

    // ── Client-Side Display ───────────────────────────────────

    private void OnRoundStart(float duration)
    {
        StartCoroutine(TimerCoroutine(duration));
    }

    private IEnumerator TimerCoroutine(float duration)
    {
        Debug.Log($"[StubMiniGame] TimerCoroutine started — duration: {duration}, countdownText: {_countdownText != null}");
        float remaining = duration;
        while (remaining > 0f)
        {
            Debug.Log($"[StubMiniGame] Timer tick — remaining: {remaining}, text before: '{_countdownText.text}'");
            if (_countdownText != null)
                _countdownText.text = $"{Mathf.CeilToInt(remaining)}";
            Debug.Log($"[StubMiniGame] Timer tick — text after: '{_countdownText.text}'");
            yield return new WaitForSeconds(1f);
            remaining -= 1f;
        }
        if (_countdownText != null)
            _countdownText.text = string.Empty;
    }

    private void ApplyResultsPayload(string payload)
    {
        if (string.IsNullOrEmpty(payload)) return;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("RESULTS");

        foreach (string entry in payload.Split('|'))
        {
            string[] p = entry.Split(',');
            if (p.Length < 4) continue;
            if (!int.TryParse(p[0], out int id)) continue;

            string name = _nameMap.TryGetValue(id, out string n) ? n : $"Player_{id}";
            string label = p[3];
            string points = p[2];
            string level = p.Length > 4 ? p[4] : "1";

            sb.AppendLine($"{label}: {name}");
            sb.AppendLine($"  +{points}pts | Level {level}");
        }

        if (_resultsScreenPanel != null)
            _resultsScreenPanel.SetActive(true);

        if (_resultsText != null)
            _resultsText.text = sb.ToString();

        StartCoroutine(ClientResultsCountdownCoroutine());
    }

    private IEnumerator ClientResultsCountdownCoroutine()
    {
        float remaining = _resultsDuration;
        while (remaining > 0f)
        {
            if (_countdownText != null)
                _countdownText.text = $"Returning in {Mathf.CeilToInt(remaining)}...";
            yield return new WaitForSeconds(1f);
            remaining -= 1f;
        }
        if (_countdownText != null)
            _countdownText.text = string.Empty;
    }

    // ── OnShowResults (host) ──────────────────────────────────

    protected override void OnShowResults(ResultsData data)
    {
        if (_resultsScreenPanel != null)
            _resultsScreenPanel.SetActive(true);

        if (_resultsText != null)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("RESULTS");
            foreach (PlayerResultEntry entry in data.Entries)
            {
                sb.AppendLine($"{entry.ResultLabel}: {entry.DisplayName}");
                sb.AppendLine($"  +{entry.PointsEarned}pts | Level {entry.CareerLevel}");
            }
            _resultsText.text = sb.ToString();
        }

        StartCoroutine(ResultsTimerCoroutine());
    }

    private IEnumerator ResultsTimerCoroutine()
    {
        float remaining = _resultsDuration;
        while (remaining > 0f)
        {
            if (_countdownText != null)
                _countdownText.text = $"Returning in {Mathf.CeilToInt(remaining)}...";
            yield return new WaitForSeconds(1f);
            remaining -= 1f;
        }

        if (_countdownText != null)
            _countdownText.text = string.Empty;

        if (_resultsScreenPanel != null)
            _resultsScreenPanel.SetActive(false);

        GameRoomManager.Instance.OnResultsDismissed(this);
    }

    // ── Payload Builders ──────────────────────────────────────

    private string BuildPlayersPayload()
    {
        var parts = new List<string>();
        foreach (PlayerObject player in _players)
        {
            PlayerProfile profile = PlayerProfileManager.Instance.GetProfile(player.Owner);
            string name = profile?.DisplayName ?? $"Player_{player.PlayerId}";
            parts.Add($"{player.PlayerId},{name}");
        }
        return string.Join("|", parts);
    }

    private string BuildResultsPayload()
    {
        var parts = new List<string>();
        foreach (RoundResult r in _lastResults)
        {
            PlayerProfile profile = PlayerProfileManager.Instance.GetProfile(r.Player.Owner);
            int level = profile != null ? PlayerResultEntry.CalculateLevel(profile.CareerScore) : 1;
            parts.Add($"{r.Player.PlayerId},{r.Standing},{r.ScoreAwarded},{r.ResultLabel},{level}");
        }
        return string.Join("|", parts);
    }

    // ── Payload Parsers ───────────────────────────────────────

    private void ApplyPlayersPayload(string payload)
    {
        if (string.IsNullOrEmpty(payload)) return;
        _nameMap.Clear();
        foreach (string entry in payload.Split('|'))
        {
            string[] p = entry.Split(',');
            if (p.Length < 2) continue;
            if (!int.TryParse(p[0], out int id)) continue;
            _nameMap[id] = p[1];
        }
    }
}