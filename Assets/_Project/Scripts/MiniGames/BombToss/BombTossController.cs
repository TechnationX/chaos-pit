// BombTossController.cs
// Server-authoritative controller for the Bomb Toss minigame.
//
// Round flow:
//   - Round starts with a random player holding the bomb
//   - Fuse burns down; holder can pass to a nearby player via crosshair aim
//   - Holder when fuse hits 0 is eliminated and sent to elimination spawn
//   - Points awarded per round based on elimination order
//   - Final tally across all rounds decides winner

using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using FishNet.Connection;
using FishNet.Component.Transforming;

namespace ChaosPit.Minigames.BombToss
{
    public class BombTossController : MiniGameController
    {
        // ── Inspector ─────────────────────────────────────────────

        [Header("Round Settings")]
        [SerializeField] private int _roundCount = 5;
        [SerializeField] private float[] _pointsPerElimination = { 1, 2, 3, 4, 5, 6, 7, 8 };

        [Header("Fuse Settings")]
        [SerializeField] private float _baseFuseTime = 15f;
        [SerializeField] private float _fuseReductionPerRound = 1f;
        [SerializeField] private float _minimumFuseTime = 5f;

        [Header("Pass Settings")]
        [SerializeField] private float _passDistance = 3f;

        [Header("Elimination Spawns")]
        [SerializeField] private Transform _eliminationSpawn;

        [Header("Results UI")]
        [SerializeField] private TMPro.TextMeshProUGUI _resultsText;
        [SerializeField] private TMPro.TextMeshProUGUI _countdownText;

        // ── Runtime State ─────────────────────────────────────────

        private List<int> _activePlayers = new List<int>();
        private List<int> _eliminatedPlayers = new List<int>();
        private int _currentHolderId = -1;
        private int _eliminationOrder = 0;
        private Dictionary<int, int> _cumulativeScores = new Dictionary<int, int>();
        private Dictionary<int, string> _nameMap = new Dictionary<int, string>();
        private bool _roundActive = false;
        private List<RoundResult> _finalResults = new List<RoundResult>();

        private BombTossHUD _hud;

        // ── Required Abstract Implementations ─────────────────────

        public override void StartGame(List<PlayerObject> players)
        {
            if (!FishNet.InstanceFinder.IsServerStarted) return;

            _players = new List<PlayerObject>(players);
            _currentRound = 0;
            _gameActive = true;

            _activePlayers.Clear();
            _eliminatedPlayers.Clear();
            _cumulativeScores.Clear();
            _nameMap.Clear();
            _eliminationOrder = 0;

            foreach (PlayerObject p in _players)
            {
                _activePlayers.Add(p.PlayerId);
                _cumulativeScores[p.PlayerId] = 0;
            }

            GameRoomManager.Instance.RpcMinigameMessage("bt_players", BuildPlayersPayload());

            StartCoroutine(StartRoundDelayed(2f));
        }

        public override void StartRound()
        {
            _currentRound++;
            _roundActive = true;
            _eliminationOrder = 0;

            RespawnActivePlayers();

            _currentHolderId = _activePlayers[Random.Range(0, _activePlayers.Count)];

            float fuseTime = Mathf.Max(_minimumFuseTime,
                _baseFuseTime - (_fuseReductionPerRound * (_currentRound - 1)));

            string payload =
                $"{_currentHolderId}," +
                $"{fuseTime.ToString(System.Globalization.CultureInfo.InvariantCulture)}," +
                $"{_currentRound},0";

            GameRoomManager.Instance.RpcMinigameMessage("bt_round_start", payload);

            StartCoroutine(FuseCoroutine(fuseTime));
        }

        public override void EndRound() { }

        public override List<RoundResult> GetResults() => _finalResults;

        public override void CleanUp()
        {
            _activePlayers.Clear();
            _eliminatedPlayers.Clear();
            _cumulativeScores.Clear();
            _nameMap.Clear();
            _finalResults.Clear();
            _roundActive = false;
            _gameActive = false;
            Debug.Log("[BombToss] CleanUp complete.");
        }

        public override void ClientInit()
        {
            _hud = FindFirstObjectByType<BombTossHUD>();
        }

        // ── Round Flow ─────────────────────────────────────────────

        private IEnumerator StartRoundDelayed(float delay)
        {
            yield return new WaitForSeconds(delay);
            StartRound();
        }

        private IEnumerator FuseCoroutine(float fuseTime)
        {
            float remaining = fuseTime;

            while (remaining > 0f && _roundActive)
            {
                remaining -= 0.5f;
                string sync =
                    $"{_currentHolderId}," +
                    $"{Mathf.Max(0f, remaining).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}";
                GameRoomManager.Instance.RpcMinigameMessage("bt_fuse_sync", sync);
                yield return new WaitForSeconds(0.5f);
            }

            if (_roundActive)
                EliminateHolder();
        }

        private void EliminateHolder()
        {
            _roundActive = false;

            int eliminated = _currentHolderId;
            _activePlayers.Remove(eliminated);
            _eliminatedPlayers.Add(eliminated);

            int points = _eliminationOrder < _pointsPerElimination.Length
                ? (int)_pointsPerElimination[_eliminationOrder]
                : 0;
            _cumulativeScores[eliminated] += points;
            _eliminationOrder++;

            // Teleport eliminated player to elimination spawn
            PlayerObject elimPlayer = FindPlayerById(eliminated);
            if (elimPlayer != null && _eliminationSpawn != null)
            {
                Transform spawn = _eliminationSpawn;
                NetworkTransform nt = elimPlayer.GetComponent<NetworkTransform>();
                if (nt != null) nt.Teleport();
                elimPlayer.transform.position = spawn.position;
                elimPlayer.transform.rotation = spawn.rotation;
                GameRoomManager.Instance.TeleportPlayer(elimPlayer.Owner, spawn.position, spawn.rotation);
            }

            string scoresPayload = BuildScoresPayload();
            GameRoomManager.Instance.RpcMinigameMessage("bt_player_eliminated",
                $"{eliminated},{points},{scoresPayload}");

            bool gameOver = _activePlayers.Count <= 1;

            if (gameOver)
            {
                StartCoroutine(EndGameDelayed(1.5f));
            }
            else
            {
                StartCoroutine(StartRoundDelayed(3f));
            }
        }

        private IEnumerator EndGameDelayed(float delay)
        {
            yield return new WaitForSeconds(delay);

            string finalPayload = BuildScoresPayload();
            GameRoomManager.Instance.RpcMinigameMessage("bt_game_over", finalPayload);

            _finalResults = BuildFinalResults();
            GameRoomManager.Instance.NotifyGameComplete(this, _finalResults);

            var entries = _finalResults.Select(r => new PlayerResultEntry
            {
                DisplayName = _nameMap.TryGetValue(r.Player.PlayerId, out string n) ? n : $"Player_{r.Player.PlayerId}",
                PointsEarned = r.ScoreAwarded,
                ResultLabel = r.ResultLabel,
                Standing = r.Standing
            }).ToList();

            OnShowResults(new ResultsData { Entries = entries });
        }

        // ── Client Action (server receives from clients) ───────────

        public override void OnClientAction(string messageType, string payload, NetworkConnection sender)
        {
            if (messageType != "bt_attempt_pass") return;

            int senderPlayerId = sender.ClientId;
            if (senderPlayerId != _currentHolderId) return;
            if (!int.TryParse(payload, out int targetId)) return;
            if (!_activePlayers.Contains(targetId) || targetId == _currentHolderId) return;

            Vector3 holderPos = GetPlayerPosition(_currentHolderId);
            Vector3 targetPos = GetPlayerPosition(targetId);

            if (Vector3.Distance(holderPos, targetPos) <= _passDistance)
            {
                _currentHolderId = targetId;
                GameRoomManager.Instance.RpcMinigameMessage("bt_holder_changed", $"{_currentHolderId}");
            }
            else
            {
                GameRoomManager.Instance.RpcMinigameMessage("bt_pass_failed", senderPlayerId.ToString());
            }
        }

        // ── Network Messages (all clients receive) ─────────────────

        public override void OnNetworkMessage(string messageType, string payload)
        {
            switch (messageType)
            {
                case "bt_players":
                    ApplyPlayersPayload(payload);
                    break;

                case "bt_round_start":
                    {
                        string[] p = payload.Split(',');
                        if (p.Length > 0 && int.TryParse(p[0], out int startHolder))
                        {
                            bool isLocalHolder = startHolder == GetLocalPlayerId();
                            SetLocalPassMode(isLocalHolder);
                        }
                    }
                    _hud?.OnNetworkMessage(messageType, payload);
                    break;

                case "bt_fuse_sync":

                case "bt_holder_changed":
                    if (int.TryParse(payload, out int newHolder))
                    {
                        bool isLocalHolder = newHolder == GetLocalPlayerId();
                        SetLocalPassMode(isLocalHolder);
                    }
                    _hud?.OnNetworkMessage(messageType, payload);
                    break;

                case "bt_player_eliminated":
                    SetLocalPassMode(false);
                    _hud?.OnNetworkMessage(messageType, payload);
                    break;


                case "bt_pass_failed":
                    if (int.TryParse(payload, out int failId) && failId == GetLocalPlayerId())
                        _hud?.OnNetworkMessage("bt_pass_failed", "");
                    break;

                case "bt_game_over":
                    SetLocalPassMode(false);
                    _hud?.OnNetworkMessage("bt_game_over", payload);
                    if (!FishNet.InstanceFinder.IsServerStarted)
                        ApplyGameOverPayload(payload);
                    break;
            }
        }

        // ── Results Screen ─────────────────────────────────────────

        protected override void OnShowResults(ResultsData data)
        {
            if (_resultsScreenPanel != null)
                _resultsScreenPanel.SetActive(true);

            if (_resultsText != null)
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("RESULTS");
                foreach (PlayerResultEntry entry in data.Entries)
                    sb.AppendLine($"{entry.ResultLabel}: {entry.DisplayName}  +{entry.PointsEarned}pts");
                _resultsText.text = sb.ToString();
            }

            StartCoroutine(ResultsCountdownCoroutine(_countdownText, notifyDismissal: true));
        }

        // ── Payload Builders ───────────────────────────────────────

        private string BuildPlayersPayload()
        {
            var parts = new List<string>();
            foreach (PlayerObject p in _players)
            {
                PlayerProfile profile = PlayerProfileManager.Instance.GetProfile(p.Owner);
                string name = profile?.DisplayName ?? $"Player_{p.PlayerId}";
                _nameMap[p.PlayerId] = name;
                parts.Add($"{p.PlayerId},{name}");
            }
            return string.Join("|", parts);
        }

        private string BuildScoresPayload()
        {
            return string.Join("|", _cumulativeScores.Select(kvp => $"{kvp.Key},{kvp.Value}"));
        }

        private List<RoundResult> BuildFinalResults()
        {
            var sorted = _cumulativeScores.OrderByDescending(kvp => kvp.Value).ToList();
            var results = new List<RoundResult>();
            for (int i = 0; i < sorted.Count; i++)
            {
                PlayerObject player = FindPlayerById(sorted[i].Key);
                if (player == null) continue;
                results.Add(new RoundResult(player, i + 1, sorted[i].Value, GetResultLabel(i + 1)));
            }
            return results;
        }

        // ── Payload Parsers ────────────────────────────────────────

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
            _hud?.Init(_nameMap);
        }

        private void ApplyGameOverPayload(string payload)
        {
            if (string.IsNullOrEmpty(payload)) return;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("RESULTS");

            foreach (string entry in payload.Split('|'))
            {
                string[] p = entry.Split(',');
                if (p.Length < 2) continue;
                if (!int.TryParse(p[0], out int id)) continue;
                if (!int.TryParse(p[1], out int score)) continue;
                string name = _nameMap.TryGetValue(id, out string n) ? n : $"Player_{id}";
                sb.AppendLine($"{name}: {score}pts");
            }

            if (_resultsScreenPanel != null) _resultsScreenPanel.SetActive(true);
            if (_resultsText != null) _resultsText.text = sb.ToString();
            StartCoroutine(ResultsCountdownCoroutine(_countdownText, notifyDismissal: false));
        }

        // ── Helpers ────────────────────────────────────────────────

        private void RespawnActivePlayers()
        {
            if (_spawnPoints == null || _spawnPoints.Length == 0)
            {
                Debug.LogWarning("[BombToss] No spawn points assigned.");
                return;
            }

            for (int i = 0; i < _activePlayers.Count; i++)
            {
                PlayerObject player = FindPlayerById(_activePlayers[i]);
                if (player == null) continue;

                Transform spawn = _spawnPoints[i % _spawnPoints.Length];
                NetworkTransform nt = player.GetComponent<NetworkTransform>();
                if (nt != null) nt.Teleport();
                player.transform.position = spawn.position;
                player.transform.rotation = spawn.rotation;
                GameRoomManager.Instance.TeleportPlayer(player.Owner, spawn.position, spawn.rotation);
            }
        }

        private Vector3 GetPlayerPosition(int playerId)
        {
            PlayerObject p = FindPlayerById(playerId);
            return p != null ? p.transform.position : Vector3.zero;
        }

        private PlayerObject FindPlayerById(int id)
        {
            foreach (PlayerObject p in _players)
                if (p.PlayerId == id) return p;
            return null;
        }

        private int GetLocalPlayerId()
        {
            foreach (var p in FindObjectsByType<PlayerObject>(
                FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (p.IsOwner) return p.PlayerId;
            return -1;
        }

        private void SetLocalPassMode(bool active)
        {
            PlayerObject local = FindObjectsByType<PlayerObject>(
                FindObjectsInactive.Include, FindObjectsSortMode.None)
                .FirstOrDefault(p => p.IsOwner);

            if (local == null) return;

            var interaction = local.GetComponent<InteractionManager>();
            interaction?.SetBombPassActive(active, _hud);
        }
    }
}