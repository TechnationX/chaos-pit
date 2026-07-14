// BombTossController.cs
// Server-authoritative controller for the Bomb Toss minigame.
//
// Round flow:
//   - Round starts with a random player holding the bomb
//   - Fuse burns down; holder can pass to a nearby player via crosshair aim
//   - Holder when fuse hits 0 is eliminated and sent to elimination spawn
//   - Points awarded per round based on elimination order
//   - Final tally across all rounds decides winner

using FishNet.Component.Transforming;
using FishNet.Connection;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ChaosPit.Minigames.BombToss
{
    public class BombTossController : MiniGameController
    {
        // ── Inspector ─────────────────────────────────────────────

        [Header("Round Settings")]
        [SerializeField] private int[] _placementPoints = { 10, 8, 6, 3, 2, 1 };

        [Header("Fuse Settings")]
        [SerializeField] private float _baseFuseTime = 15f;
        [SerializeField] private float _fuseReductionPerRound = 1f;
        [SerializeField] private float _minimumFuseTime = 5f;

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
                _activePlayers.Add(p.Owner.ClientId);
                _cumulativeScores[p.Owner.ClientId] = 0;
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

            int standing = _eliminationOrder + 1;
            int points = CalculatePlacementPoints(standing, _players.Count);
            _cumulativeScores[eliminated] += points;

            // Teleport eliminated player to elimination spawn
            PlayerObject elimPlayer = FindPlayerByClientId(eliminated);
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
                // Award points to last survivor
                if (_activePlayers.Count == 1)
                {
                    int survivor = _activePlayers[0];
                    int survivorPoints = CalculatePlacementPoints(_eliminationOrder + 1, _players.Count);
                    _cumulativeScores[survivor] += survivorPoints;
                }

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
                DisplayName = _nameMap.TryGetValue(r.Player.Owner.ClientId, out string n) ? n : $"Player_{r.Player.PlayerId}",
                PointsEarned = r.ScoreAwarded,
                ResultLabel = r.ResultLabel,
                Standing = r.Standing
            }).ToList();

            OnShowResults(new ResultsData { Entries = entries });
        }

        private int CalculatePlacementPoints(int standing, int totalPlayers)
        {
            int lastIndex = _placementPoints.Length - 1;
            int index = lastIndex - totalPlayers + standing;
            index = Mathf.Clamp(index, 0, lastIndex);
            return _placementPoints[index];
        }

        // ── Client Action (server receives from clients) ───────────

        public override void OnClientAction(string messageType, string payload, NetworkConnection sender)
        {
            //Debug.Log($"[BombToss] OnClientAction — type: {messageType}, payload: {payload}, sender: {sender.ClientId}");
            if (messageType != "bt_attempt_pass") return;

            // Find the PlayerObject owned by this connection to get PlayerId
            PlayerObject senderPlayer = _players.FirstOrDefault(p => p.Owner == sender);
            //Debug.Log($"[BombToss] senderPlayer found: {senderPlayer != null}, currentHolder: {_currentHolderId}");

            if (senderPlayer == null) return;
            int senderPlayerId = senderPlayer.Owner.ClientId;
            if (senderPlayerId != _currentHolderId) return;
            //Debug.Log($"[BombToss] senderPlayerId: {senderPlayerId}, matches holder: {senderPlayerId == _currentHolderId}");

            if (senderPlayerId != _currentHolderId) return;
            if (!int.TryParse(payload, out int targetId)) return;
            //Debug.Log($"[BombToss] targetId: {targetId}, in active: {_activePlayers.Contains(targetId)}");

            if (!_activePlayers.Contains(targetId) || targetId == _currentHolderId) return;

            //Debug.Log($"[BombToss] distance: {dist}, passDistance: {_passDistance}");

            _currentHolderId = targetId;
            GameRoomManager.Instance.RpcMinigameMessage("bt_holder_changed", $"{_currentHolderId}");
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
                            PlayerObject local = FindObjectsByType<PlayerObject>(
                                FindObjectsInactive.Include, FindObjectsSortMode.None)
                                .FirstOrDefault(p2 => p2.IsOwner);
                            bool isLocalHolder = startHolder == GetLocalPlayerId();
                            //Debug.Log($"[BombToss] round_start holder in payload: {startHolder}, local.PlayerId: {local?.PlayerId}, local.Owner.ClientId: {local?.Owner.ClientId}");
                            SetLocalPassMode(isLocalHolder);
                        }
                    }
                    _hud?.OnNetworkMessage(messageType, payload);
                    break;

                case "bt_fuse_sync":
                    _hud?.OnNetworkMessage(messageType, payload);
                    break;

                case "bt_holder_changed":
                    if (int.TryParse(payload, out int newHolder))
                    {
                        PlayerObject local = FindObjectsByType<PlayerObject>(
                            FindObjectsInactive.Include, FindObjectsSortMode.None)
                            .FirstOrDefault(p => p.IsOwner);
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
            _hud?.HideHUD();

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
                parts.Add($"{p.Owner.ClientId},{name}");
                _nameMap[p.Owner.ClientId] = name;
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
                PlayerObject player = _players.FirstOrDefault(p => p.Owner.ClientId == sorted[i].Key);
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

            var entries = new List<(int id, int score)>();
            foreach (string entry in payload.Split('|'))
            {
                string[] p = entry.Split(',');
                if (p.Length < 2) continue;
                if (!int.TryParse(p[0], out int id)) continue;
                if (!int.TryParse(p[1], out int score)) continue;
                entries.Add((id, score));
            }
            entries.Sort((a, b) => b.score.CompareTo(a.score));

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("RESULTS");
            for (int i = 0; i < entries.Count; i++)
            {
                string name = _nameMap.TryGetValue(entries[i].id, out string n) ? n : $"Player_{entries[i].id}";
                string label = GetResultLabel(i + 1);
                sb.AppendLine($"{label}: {name}  +{entries[i].score}pts");
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
                PlayerObject player = FindPlayerByClientId(_activePlayers[i]);
                if (player == null) continue;

                Transform spawn = _spawnPoints[i % _spawnPoints.Length];
                NetworkTransform nt = player.GetComponent<NetworkTransform>();
                if (nt != null) nt.Teleport();
                player.transform.position = spawn.position;
                player.transform.rotation = spawn.rotation;
                GameRoomManager.Instance.TeleportPlayer(player.Owner, spawn.position, spawn.rotation);
            }
        }

        private PlayerObject FindPlayerByClientId(int clientId)
        {
            foreach (PlayerObject p in _players)
                if (p.Owner.ClientId == clientId) return p;
            return null;
        }

        private int GetLocalPlayerId()
        {
            foreach (var p in FindObjectsByType<PlayerObject>(
                FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (p.IsOwner) return p.Owner.ClientId;
            return -1;
        }

        private void SetLocalPassMode(bool active)
        {
            PlayerObject local = FindObjectsByType<PlayerObject>(
                FindObjectsInactive.Include, FindObjectsSortMode.None)
                .FirstOrDefault(p => p.IsOwner);

            //Debug.Log($"[BombToss] SetLocalPassMode {active} — local found: {local != null}, localPlayerId: {GetLocalPlayerId()}");

            if (local == null) return;

            var interaction = local.GetComponent<InteractionManager>();
            //Debug.Log($"[BombToss] interaction found: {interaction != null}, setting bombPass: {active}");
            interaction?.SetBombPassActive(active);
        }
    }
}