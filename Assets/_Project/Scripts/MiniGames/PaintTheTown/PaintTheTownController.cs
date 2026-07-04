// PaintTheTownController.cs
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace ChaosPit.Minigames.PaintTheTown
{
    /// <summary>
    /// Paint the Town — Territory minigame.
    /// Players walk over tiles to claim them. Most tiles at round end wins.
    ///
    /// Network pattern:
    ///   Server calls GameRoomManager.Instance.RpcMinigameMessage(type, payload)
    ///   All clients receive it and route to OnNetworkMessage(type, payload) here.
    ///
    /// Message types: "colors", "tiles", "round_start", "round_end", "counts"
    ///
    /// Sprint: not yet implemented.
    /// Minimap: not yet implemented.
    /// Colorblind palette: not yet implemented.
    /// </summary>
    public class PaintTheTownController : MiniGameController
    {
        // ── Inspector ─────────────────────────────────────────────
        [Header("Results UI")]
        [SerializeField] private TMPro.TextMeshProUGUI _resultsText;
        [SerializeField] private TMPro.TextMeshProUGUI _countdownText;

        [Header("Game Config")]
        [SerializeField] private float _roundDuration = 75f;
        [SerializeField] private float _syncInterval = 0.2f;

        [Header("Scoring — Points Per Standing")]
        [SerializeField] private int _points1st = 10;
        [SerializeField] private int _points2nd = 8;
        [SerializeField] private int _points3rd = 6;
        [SerializeField] private int _points4th = 4;
        [SerializeField] private int _points5th = 2;
        [SerializeField] private int _points6th = 1;

        [Header("References")]
        [SerializeField] private TileGrid _tileGrid;
        [SerializeField] private PaintTheTownHUD _hud;

        private Dictionary<int, string> _nameMap = new();

        // ── Player Colors ─────────────────────────────────────────
        private static readonly Color[] _playerColors = new Color[]
        {
            new Color(0.96f, 0.26f, 0.21f),   // Red
            new Color(0.25f, 0.47f, 0.96f),   // Blue
            new Color(0.18f, 0.80f, 0.44f),   // Green
            new Color(0.98f, 0.75f, 0.18f),   // Yellow
            new Color(0.61f, 0.15f, 0.69f),   // Purple
            new Color(0.98f, 0.50f, 0.19f),   // Orange
        };

        // ── Runtime State ─────────────────────────────────────────
        private Dictionary<int, Color> _colorMap = new();
        private Dictionary<int, int> _tileCounts = new();
        private List<RoundResult> _roundResults = new();

        private float _timeRemaining;
        private bool _roundActive;
        private Coroutine _gameLoopCoroutine;
        private Coroutine _syncCoroutine;

        // ── MiniGameController Overrides ──────────────────────────

        public override void StartGame(List<PlayerObject> players)
        {
            _tileGrid.GenerateGrid();

            if (!FishNet.InstanceFinder.IsServerStarted) return;

            _players = new List<PlayerObject>(players);

            AssignPlayerColors();
            InitTileCounts();

            SendMessage("colors", BuildColorsPayload());

            _gameLoopCoroutine = StartCoroutine(GameLoopCoroutine());

            Debug.Log($"[PaintTheTown] StartGame — {_players.Count} players.");
        }

        public override void StartRound()
        {
            _tileGrid.ResetAllTiles();
            _tileGrid.FlushDirtyTiles();
            InitTileCounts();

            _timeRemaining = _roundDuration;
            _roundActive = true;

            _syncCoroutine = StartCoroutine(BatchSyncCoroutine());

            GameRoomManager.Instance.RpcMinigameMessage("reset_tiles", "");
            GameRoomManager.Instance.RpcMinigameMessage("round_start",
                _roundDuration.ToString(System.Globalization.CultureInfo.InvariantCulture));

            Debug.Log("[PaintTheTown] Round started.");
        }

        public override void ClientInit()
        {
            _tileGrid.GenerateGrid();
            FindLocalPlayer()?.Movement.SetStaminaLimited(true);
        }

        private PlayerObject FindLocalPlayer()
        {
            foreach (var p in FindObjectsByType<PlayerObject>(FindObjectsSortMode.None))
                if (p.IsOwner) return p;
            return null;
        }

        public override void EndRound()
        {
            _roundActive = false;

            if (_syncCoroutine != null)
            {
                StopCoroutine(_syncCoroutine);
                _syncCoroutine = null;
            }

            // Final flush before scoring
            FlushAndBroadcastTiles();

            foreach (PlayerObject player in _players)
                _tileCounts[player.PlayerId] = _tileGrid.CountTilesForPlayer(player.PlayerId);

            _roundResults = BuildResults();
            GameRoomManager.Instance.RpcMinigameMessage("results", BuildResultsPayload());

            GameRoomManager.Instance.RpcMinigameMessage("round_end", "");

            Debug.Log("[PaintTheTown] Round ended.");
        }

        public override List<RoundResult> GetResults() => _roundResults;

        public override void CleanUp()
        {
            if (_gameLoopCoroutine != null) StopCoroutine(_gameLoopCoroutine);
            if (_syncCoroutine != null) StopCoroutine(_syncCoroutine);

            _players.Clear();
            _colorMap.Clear();
            _tileCounts.Clear();
            _roundResults.Clear();
            _roundActive = false;

            Debug.Log("[PaintTheTown] CleanUp complete.");
        }

        public override void RemovePlayer(PlayerObject player)
        {
            _players.Remove(player);
            _tileCounts.Remove(player.PlayerId);
            _colorMap.Remove(player.PlayerId);
            Debug.Log($"[PaintTheTown] Player removed: {player.PlayerId}");
        }

        // ── Network Message Receiver (client-side) ────────────────

        public override void OnNetworkMessage(string messageType, string payload)
        {
            //Debug.Log($"[PaintTheTown] OnNetworkMessage — type: {messageType}, payloadLen: {payload?.Length ?? 0}");
            //if (FishNet.InstanceFinder.IsServerStarted) return;


            switch (messageType)
            {
                case "colors": ApplyColorsPayload(payload); break;
                case "tiles": ApplyTilesPayload(payload); break;
                case "round_start":
                    if (float.TryParse(payload,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out float duration))
                        _hud?.OnRoundStart(duration);
                    break;
                case "round_end":
                    _hud?.OnRoundEnd();
                    FindLocalPlayer()?.Movement.SetStaminaLimited(false);
                    break;
                case "counts": ApplyCountsPayload(payload); break;
                case "results":
                    if (!FishNet.InstanceFinder.IsServerStarted)
                        ApplyResultsPayload(payload);
                    break;
            }
        }

        // ── Game Loop ─────────────────────────────────────────────

        private IEnumerator GameLoopCoroutine()
        {
            if (!FishNet.InstanceFinder.IsServerStarted) yield break;

            StartRound();

            while (_timeRemaining > 0f && _roundActive)
            {
                _timeRemaining -= Time.deltaTime;
                PollPlayerPositions();
                yield return null;
            }

            EndRound();
            GameRoomManager.Instance.NotifyGameComplete(this, _roundResults);
        }

        // ── Tile Claiming (server only) ───────────────────────────

        private void PollPlayerPositions()
        {
            foreach (PlayerObject player in _players)
            {
                if (player == null) continue;
                int tileIndex = _tileGrid.GetTileIndexAtWorld(player.transform.position);
                if (tileIndex < 0) continue;
                _tileGrid.ClaimTile(tileIndex, player.PlayerId);
            }
        }

        // ── Batch Sync ────────────────────────────────────────────

        private IEnumerator BatchSyncCoroutine()
        {
            while (_roundActive)
            {
                yield return new WaitForSeconds(_syncInterval);
                FlushAndBroadcastTiles();
            }
        }

        private void FlushAndBroadcastTiles()
        {
            List<TileDelta> deltas = _tileGrid.FlushDirtyTiles();
            if (deltas.Count == 0) return;

            GameRoomManager.Instance.RpcMinigameMessage("tiles", BuildTilesPayload(deltas));
            UpdateAndBroadcastCounts();
        }

        private void UpdateAndBroadcastCounts()
        {
            foreach (PlayerObject player in _players)
                _tileCounts[player.PlayerId] = _tileGrid.CountTilesForPlayer(player.PlayerId);

            GameRoomManager.Instance.RpcMinigameMessage("counts", BuildCountsPayload());
        }

        private void ApplyResultsPayload(string payload)
        {
            if (string.IsNullOrEmpty(payload)) return;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine(ResultsFormatter.Header);

            foreach (string entry in payload.Split('|'))
            {
                string[] p = entry.Split(',');
                if (p.Length < 5) continue;
                if (!int.TryParse(p[0], out int id)) continue;

                string name = _nameMap.TryGetValue(id, out string n) ? n : $"Player_{id}";
                string label = p[3];
                string points = p[2];
                string level = p[4];

                sb.AppendLine(ResultsFormatter.FormatEntry(label, name, int.Parse(points), int.Parse(level)));
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

        // ── Payload Builders (server) ─────────────────────────────
        // Simple pipe-delimited format — avoids JsonUtility struct limitations.
        // Format per message type:
        //   colors:  "id,r,g,b|id,r,g,b|..."
        //   tiles:   "tileIndex,ownerId|tileIndex,ownerId|..."
        //   counts:  "id,count|id,count|..."

        private string BuildColorsPayload()
        {
            var parts = new List<string>();
            foreach (PlayerObject player in _players)
            {
                Color c = _colorMap[player.PlayerId];
                PlayerProfile profile = PlayerProfileManager.Instance.GetProfile(player.Owner);
                string name = profile?.DisplayName ?? $"Player_{player.PlayerId}";
                parts.Add($"{player.PlayerId}" +
                          $",{c.r.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
                          $",{c.g.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
                          $",{c.b.ToString(System.Globalization.CultureInfo.InvariantCulture)}" +
                          $",{name}");
                Debug.Log($"[PaintTheTown] BUILD — playerId: {player.PlayerId}, name: {name}, color: {c}");
            }
            return string.Join("|", parts);
        }

        private string BuildTilesPayload(List<TileDelta> deltas)
        {
            var parts = new List<string>(deltas.Count);
            foreach (TileDelta d in deltas)
                parts.Add($"{d.TileIndex},{d.OwnerId}");
            return string.Join("|", parts);
        }

        private string BuildCountsPayload()
        {
            var parts = new List<string>();
            foreach (var kvp in _tileCounts)
                parts.Add($"{kvp.Key},{kvp.Value}");
            return string.Join("|", parts);
        }

        // ── Payload Parsers (client) ──────────────────────────────

        private void ApplyColorsPayload(string payload)
        {
            if (string.IsNullOrEmpty(payload)) return;
            _colorMap.Clear();
            var nameMap = new Dictionary<int, string>();

            foreach (string entry in payload.Split('|'))
            {
                string[] p = entry.Split(',');
                if (p.Length < 5) continue;
                if (!int.TryParse(p[0], out int id)) continue;
                float r = float.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture);
                float g = float.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture);
                float b = float.Parse(p[3], System.Globalization.CultureInfo.InvariantCulture);
                string name = p[4];
                Debug.Log($"[PaintTheTown] APPLY — id: {id}, name: {name}, color: {new Color(r, g, b)}");

                _colorMap[id] = new Color(r, g, b);
                nameMap[id] = name;
            }

            _nameMap = nameMap;
            _hud?.InitScoreRows(nameMap, _colorMap);
        }

        private void ApplyTilesPayload(string payload)
        {
            if (string.IsNullOrEmpty(payload)) return;
            var deltas = new List<TileDelta>();
            foreach (string entry in payload.Split('|'))
            {
                string[] p = entry.Split(',');
                if (p.Length < 2) continue;
                if (!int.TryParse(p[0], out int tileIdx)) continue;
                if (!int.TryParse(p[1], out int ownerId)) continue;
                deltas.Add(new TileDelta { TileIndex = tileIdx, OwnerId = ownerId });
            }
            _tileGrid.ApplyDeltas(deltas, _colorMap);
        }

        private void ApplyCountsPayload(string payload)
        {
            if (string.IsNullOrEmpty(payload) || _hud == null) return;
            var countMap = new Dictionary<int, int>();
            foreach (string entry in payload.Split('|'))
            {
                string[] p = entry.Split(',');
                if (p.Length < 2) continue;
                if (!int.TryParse(p[0], out int id)) continue;
                if (!int.TryParse(p[1], out int count)) continue;
                countMap[id] = count;
            }
            _hud.UpdateTileCounts(countMap);
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

        // ── Scoring ───────────────────────────────────────────────

        private List<RoundResult> BuildResults()
        {
            var sorted = new List<PlayerObject>(_players);
            sorted.Sort((a, b) => _tileCounts[b.PlayerId].CompareTo(_tileCounts[a.PlayerId]));

            int[] pts = { _points1st, _points2nd, _points3rd, _points4th, _points5th, _points6th };

            var results = new List<RoundResult>();
            int prevCount = -1;
            int prevStanding = 1;

            for (int i = 0; i < sorted.Count; i++)
            {
                PlayerObject player = sorted[i];
                int count = _tileCounts[player.PlayerId];
                int standing = (count == prevCount) ? prevStanding : i + 1;
                int points = (standing - 1 < pts.Length) ? pts[standing - 1] : 0;

                results.Add(new RoundResult(player, standing, points, GetResultLabel(standing)));

                prevCount = count;
                prevStanding = standing;
            }

            return results;
        }

        // ── Helpers ───────────────────────────────────────────────

        private void AssignPlayerColors()
        {
            _colorMap.Clear();
            for (int i = 0; i < _players.Count; i++)
                _colorMap[_players[i].PlayerId] = _playerColors[i % _playerColors.Length];
        }

        private void InitTileCounts()
        {
            _tileCounts.Clear();
            foreach (PlayerObject player in _players)
                _tileCounts[player.PlayerId] = 0;
        }

        // Shadows MonoBehaviour.SendMessage — use explicit name to avoid ambiguity
        private void SendMessage(string messageType, string payload)
        {
            GameRoomManager.Instance.RpcMinigameMessage(messageType, payload);
        }

        protected override void OnShowResults(ResultsData data)
        {
            if (_resultsScreenPanel != null)
                _resultsScreenPanel.SetActive(true);

            if (_resultsText != null)
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine(ResultsFormatter.Header);

                foreach (PlayerResultEntry entry in data.Entries)
                {
                    sb.AppendLine(ResultsFormatter.FormatEntry(entry.ResultLabel, entry.DisplayName, entry.PointsEarned, entry.CareerLevel));
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

    }

    internal static class ResultsFormatter
    {
        public const string Header = "RESULTS";

        public static string FormatEntry(string label, string name, int points, int level)
            => $"{label}: {name}\n  +{points}pts | Level {level}";
    }
}
