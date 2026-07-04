// TemplateController.cs
// TEMPLATE — Copy this file and rename the class, namespace, and message types
// for each new minigame. Search for TODO comments to find every place that needs
// game-specific logic added.

using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace ChaosPit.Minigames.Template
{
    /// <summary>
    /// Template MiniGameController.
    ///
    /// Network pattern:
    ///   Server calls GameRoomManager.Instance.RpcMinigameMessage(type, payload)
    ///   All clients receive it and route to OnNetworkMessage(type, payload) here.
    ///
    /// Steps to create a new minigame from this template:
    ///   1. Copy this file, TemplateHUD.cs, and TemplateScoreRow.cs
    ///   2. Rename the namespace, class names, and message type strings
    ///   3. Fill in all TODO sections with game-specific logic
    ///   4. Create a new scene, add this controller, wire Inspector references
    ///   5. Add a MiniGameRegistryEntry ScriptableObject for this game
    ///   6. Add the scene to Build Settings
    /// </summary>
    public class TemplateController : MiniGameController
    {
        // ── Inspector ─────────────────────────────────────────────
        [Header("Game Config")]
        [SerializeField] private float _roundDuration = 60f;
        [SerializeField] private int   _totalRounds   = 1;

        [Header("Scoring — Points Per Standing")]
        [SerializeField] private int _points1st = 10;
        [SerializeField] private int _points2nd = 8;
        [SerializeField] private int _points3rd = 6;
        [SerializeField] private int _points4th = 4;
        [SerializeField] private int _points5th = 2;
        [SerializeField] private int _points6th = 1;

        [Header("References")]
        [SerializeField] private TemplateHUD _hud;

        // ── Runtime State ─────────────────────────────────────────
        private Dictionary<int, string> _nameMap      = new();
        private List<RoundResult>       _roundResults = new();

        private float     _timeRemaining;
        private bool      _roundActive;
        private Coroutine _gameLoopCoroutine;

        // ── MiniGameController Overrides ──────────────────────────

        public override void StartGame(List<PlayerObject> players)
        {
            // Server only — game loop and state management
            if (!FishNet.InstanceFinder.IsServerStarted) return;

            _players      = new List<PlayerObject>(players);
            _currentRound = 0;
            _gameActive   = true;

            // Send player identity to all clients so they can display names
            GameRoomManager.Instance.RpcMinigameMessage("tmpl_players", BuildPlayersPayload());

            // TODO: send any additional setup data to clients here
            // e.g. GameRoomManager.Instance.RpcMinigameMessage("tmpl_setup", BuildSetupPayload());

            _gameLoopCoroutine = StartCoroutine(GameLoopCoroutine());

            Debug.Log($"[Template] StartGame — {_players.Count} players.");
        }

        public override void ClientInit()
        {
            // Called on ALL clients after the scene loads.
            // Generate or initialize any client-side objects here (grids, local state, etc.)
            // TODO: add client-side initialization
        }

        public override void StartRound()
        {
            _currentRound++;
            _timeRemaining = _roundDuration;
            _roundActive   = true;

            // TODO: reset any per-round server state here

            GameRoomManager.Instance.RpcMinigameMessage("tmpl_round_start",
                _roundDuration.ToString(System.Globalization.CultureInfo.InvariantCulture));

            Debug.Log($"[Template] StartRound {_currentRound}.");
        }

        public override void EndRound()
        {
            _roundActive = false;

            // TODO: finalize server-side scoring here before BuildResults()

            _roundResults = BuildResults();

            GameRoomManager.Instance.RpcMinigameMessage("tmpl_round_end", "");
            GameRoomManager.Instance.RpcMinigameMessage("tmpl_results", BuildResultsPayload());

            Debug.Log($"[Template] EndRound {_currentRound}.");
        }

        public override List<RoundResult> GetResults() => _roundResults;

        public override void CleanUp()
        {
            if (_gameLoopCoroutine != null) StopCoroutine(_gameLoopCoroutine);

            _players.Clear();
            _nameMap.Clear();
            _roundResults.Clear();
            _roundActive  = false;
            _gameActive   = false;

            // TODO: clean up any game-specific state or spawned objects

            Debug.Log("[Template] CleanUp complete.");
        }

        public override void RemovePlayer(PlayerObject player)
        {
            _players.Remove(player);
            _nameMap.Remove(player.PlayerId);
            // TODO: remove player from any game-specific scoring state
            Debug.Log($"[Template] Player removed: {player.PlayerId}");
        }

        // ── Network Message Receiver (all clients) ────────────────

        public override void OnNetworkMessage(string messageType, string payload)
        {
            switch (messageType)
            {
                case "tmpl_players":
                    ApplyPlayersPayload(payload);
                    break;

                case "tmpl_round_start":
                    if (float.TryParse(payload,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out float duration))
                        _hud?.OnRoundStart(duration);
                    break;

                case "tmpl_round_end":
                    _hud?.OnRoundEnd();
                    break;

                case "tmpl_results":
                    if (!FishNet.InstanceFinder.IsServerStarted)
                        ApplyResultsPayload(payload);
                    break;

                // TODO: add game-specific message types here
                // case "tmpl_mydata": ApplyMyDataPayload(payload); break;
            }
        }

        // ── Game Loop ─────────────────────────────────────────────

        private IEnumerator GameLoopCoroutine()
        {
            while (_currentRound < _totalRounds)
            {
                StartRound();

                while (_timeRemaining > 0f && _roundActive)
                {
                    _timeRemaining -= Time.deltaTime;

                    // TODO: run per-frame server logic here
                    // e.g. poll player positions, check win conditions

                    yield return null;
                }

                EndRound();

                // Pause between rounds if multi-round
                if (_currentRound < _totalRounds)
                    yield return new WaitForSeconds(3f);
            }

            GameRoomManager.Instance.NotifyGameComplete(this, _roundResults);
        }

        // ── Results (host) ────────────────────────────────────────

        protected override void OnShowResults(ResultsData data)
        {
            if (_resultsScreenPanel != null)
                _resultsScreenPanel.SetActive(true);

            StartCoroutine(ResultsTimerCoroutine());
        }

        private IEnumerator ResultsTimerCoroutine()
        {
            yield return new WaitForSeconds(_resultsDuration);

            if (_resultsScreenPanel != null)
                _resultsScreenPanel.SetActive(false);

            GameRoomManager.Instance.OnResultsDismissed(this);
        }

        // ── Scoring ───────────────────────────────────────────────

        private List<RoundResult> BuildResults()
        {
            // TODO: replace this with game-specific standing logic
            // Default: sort by whatever metric your game uses
            var sorted = new List<PlayerObject>(_players);

            // Example: random order (replace with real sorting)
            sorted.Sort((a, b) => UnityEngine.Random.value > 0.5f ? 1 : -1);

            int[] pts = { _points1st, _points2nd, _points3rd, _points4th, _points5th, _points6th };

            var results      = new List<RoundResult>();
            int prevStanding = 1;

            for (int i = 0; i < sorted.Count; i++)
            {
                PlayerObject player   = sorted[i];
                int          standing = i + 1;
                int          points   = (standing - 1 < pts.Length) ? pts[standing - 1] : 0;

                results.Add(new RoundResult(player, standing, points, GetResultLabel(standing)));
                prevStanding = standing;
            }

            return results;
        }

        // ── Payload Builders (server) ─────────────────────────────
        // Pipe-delimited, comma-separated format.
        // Add new builders here for game-specific data.

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
            foreach (RoundResult r in _roundResults)
            {
                PlayerProfile profile = PlayerProfileManager.Instance.GetProfile(r.Player.Owner);
                int level = profile != null ? PlayerResultEntry.CalculateLevel(profile.CareerScore) : 1;
                parts.Add($"{r.Player.PlayerId},{r.Standing},{r.ScoreAwarded},{r.ResultLabel},{level}");
            }
            return string.Join("|", parts);
        }

        // TODO: add game-specific payload builders here
        // private string BuildMyDataPayload() { ... }

        // ── Payload Parsers (client) ──────────────────────────────

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

            // Initialize HUD score rows once names are known
            _hud?.InitScoreRows(_nameMap);
        }

        private void ApplyResultsPayload(string payload)
        {
            if (string.IsNullOrEmpty(payload)) return;

            var entries = new List<(string label, string name, string points, string level)>();

            foreach (string entry in payload.Split('|'))
            {
                string[] p = entry.Split(',');
                if (p.Length < 5) continue;
                if (!int.TryParse(p[0], out int id)) continue;

                string name   = _nameMap.TryGetValue(id, out string n) ? n : $"Player_{id}";
                string label  = p[3];
                string points = p[2];
                string level  = p[4];

                entries.Add((label, name, points, level));
            }

            _hud?.ShowClientResults(entries);
            StartCoroutine(ClientResultsCountdownCoroutine());
        }

        private IEnumerator ClientResultsCountdownCoroutine()
        {
            float remaining = _resultsDuration;
            while (remaining > 0f)
            {
                _hud?.SetResultsCountdown(Mathf.CeilToInt(remaining));
                yield return new WaitForSeconds(1f);
                remaining -= 1f;
            }
            _hud?.ClearResultsCountdown();
        }

        // ── Helpers ───────────────────────────────────────────────

        private PlayerObject FindLocalPlayer()
        {
            foreach (var p in FindObjectsByType<PlayerObject>(FindObjectsSortMode.None))
                if (p.IsOwner) return p;
            return null;
        }
    }
}
