// JinxedController.cs
// Server-authoritative controller for the Jinxed minigame.
// One player starts Jinxed; tagged players spread the jinx.
// Arena tiles fall in sequential outer-ring order.
// Rounds end when the timer expires; survivors earn a round win.

using ChaosPit.Minigames.LastOneStanding;
using FishNet.Component.Transforming;
using FishNet.Connection;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ChaosPit.Minigames.Jinxed
{
    // ── Enums ─────────────────────────────────────────────────────

    public enum JinxedPlayerState { Survivor, Jinxed, Eliminated }

    // ── Per-Player Runtime Data ───────────────────────────────────

    public class JinxedPlayerData
    {
        public int PlayerId;
        public string DisplayName;
        public JinxedPlayerState State = JinxedPlayerState.Survivor;
        public int TotalScore = 0;
        public float TotalSurvival = 0f;
        public float EliminatedAt = -1f;
        public bool TagOnCooldown = false;
    }

    // ── Controller ────────────────────────────────────────────────

    public class JinxedController : MiniGameController
    {
        // ── Inspector ─────────────────────────────────────────────
        [Header("References")]
        [SerializeField] private ArenaGrid _arenaGrid;

        [Header("Round Settings")]
        [SerializeField] private int _totalRounds = 3;
        [SerializeField] private float _roundDuration = 60f;

        [Header("Tile Fall Settings")]
        [SerializeField] private float _tileFallInterval = 1.5f;
        [SerializeField] private float _tileWarnDuration = 0.8f;
        [SerializeField] private float _tileDangerDuration = 0.4f;

        [Header("Elimination")]
        [SerializeField] private Transform _eliminationSpawnPoint;

        // ── Runtime ───────────────────────────────────────────────
        private Dictionary<int, JinxedPlayerData> _jinxedPlayers = new();
        private List<int> _fallOrder = new();
        private int _fallIndex = 0;
        private float _roundTimer = 0f;
        private bool _roundActive = false;
        private int _lastJinxedId = -1;

        private Coroutine _roundCoroutine;
        private Coroutine _fallCoroutine;
        private Coroutine _clientFallCoroutine;

        private static readonly int[] _standingPoints = { 10, 8, 5, 3, 2, 1 };

        // ── MiniGameController Overrides ──────────────────────────
        public override void ClientInit()
        {
            // HUD is found once on client init and cached
            // Message routing to HUD happens in OnNetworkMessage
        }

        public override void StartGame(List<PlayerObject> players)
        {
            _players.Clear();
            _players.AddRange(players);

            _jinxedPlayers.Clear();
            foreach (var p in players)
            {
                _jinxedPlayers[p.PlayerId] = new JinxedPlayerData
                {
                    PlayerId = p.PlayerId,
                    DisplayName = p.PlayerName
                };
            }

            _currentRound = 0;
            _gameActive = true;
            _roundCoroutine = StartCoroutine(RunGameCoroutine());
        }

        public override void StartRound()
        {
            // Round flow is driven internally by RunGameCoroutine.
            // GameRoomManager calls this — safe to leave as no-op.
        }

        public override void EndRound()
        {
            _roundActive = false;
        }

        public override List<RoundResult> GetResults()
        {
            var results = new List<RoundResult>();
            var sorted = _jinxedPlayers.Values
                .OrderByDescending(p => p.TotalScore)
                .ThenByDescending(p => p.TotalSurvival)
                .ToList();

            for (int i = 0; i < sorted.Count; i++)
            {
                int standing = i + 1;
                int careerPoints = i < _standingPoints.Length ? _standingPoints[i] : 0;
                string label = GetResultLabel(standing);
                PlayerObject po = _players.FirstOrDefault(p => p.PlayerId == sorted[i].PlayerId);

                results.Add(new RoundResult(po, standing, careerPoints, label));
            }

            return results;
        }

        public override void CleanUp()
        {
            StopAllCoroutines();
            _roundActive = false;
            _gameActive = false;

            foreach (var p in _players)
            {
                p.Movement.ClearAllMovementLocks();
                p.GetComponent<JinxedPlayerEffect>()?.RemoveJinxEffect();
            }

            _arenaGrid?.DestroyGrid();
        }

        public override void OnNetworkMessage(string messageType, string payload)
        {
            //if (messageType != "jinxed_timer") Debug.Log($"[Jinxed] OnNetworkMessage — type: {messageType}");

            switch (messageType)
            {
                case "jinxed_tag_attempt":
                    HandleTagAttempt(payload);
                    break;
                case "jinxed_round_start":
                    HandleRoundStartClient(payload);
                    break;
                case "jinxed_state_change":
                    HandleStateChangeClient(payload);
                    break;
                case "jinxed_tag_cooldown":
                    HandleCooldownClient(payload);
                    break;
                case "jinxed_timer":
                    HandleTimerClient(payload);
                    break;
                case "jinxed_round_end":
                    HandleRoundEndClient(payload);
                    break;
                case "jinxed_game_end":
                    if (!FishNet.InstanceFinder.IsServerStarted)
                        HandleGameEndClient(payload);
                    break;
            }
        }

        private void HandleStateChangeClient(string payload)
        {

            if (!TryParseTwo(payload, out int playerId, out int stateInt)) return;
            //Debug.Log($"[Jinxed] Looking for playerId: {playerId}");
            JinxedPlayerState state = (JinxedPlayerState)stateInt;

            //Debug.Log($"[Jinxed] StateChange — playerId: {playerId}, state: {state}, " +
            //    $"players in list: {_players.Count}, " +
            //    $"target found: {FindPlayerById(playerId) != null}");

            // Update HUD for local player only
            PlayerObject local = _players.FirstOrDefault(p => p.IsOwner);
            if (local != null && local.Owner?.ClientId == playerId)
            {
                JinxedHUD hud = FindFirstObjectByType<JinxedHUD>(FindObjectsInactive.Include);
                hud?.SetPlayerStatus(playerId, state);
            }

            // Apply visual effect on the affected player — visible to all clients
            PlayerObject target = FindPlayerById(playerId);
            if (target == null) return;

            JinxedPlayerEffect effect = target.GetComponent<JinxedPlayerEffect>();
            if (effect == null) return;

            JinxedHUD allHud = FindFirstObjectByType<JinxedHUD>(FindObjectsInactive.Include);
            allHud?.SetPlayerStatus(playerId, state);

            switch (state)
            {
                case JinxedPlayerState.Jinxed:
                    effect.ApplyJinxEffect();
                    break;
                case JinxedPlayerState.Eliminated:
                    effect.ApplyEliminatedEffect();
                    break;
                case JinxedPlayerState.Survivor:
                    effect.RemoveJinxEffect();
                    break;
            }
        }

        private void HandleCooldownClient(string payload)
        {
            if (!TryParseTwo(payload, out int playerId, out int _)) return;

            PlayerObject local = _players.FirstOrDefault(p => p.IsOwner);
            if (local == null || local.Owner?.ClientId != playerId) return;

            var parts = payload.Split('|');
            if (parts.Length < 2) return;
            float duration = float.Parse(parts[1]);

            JinxedHUD hud = FindFirstObjectByType<JinxedHUD>(FindObjectsInactive.Include);
            if (duration > 0f) hud?.StartCooldown(duration);
            else hud?.ClearCooldown();
        }

        private void HandleTimerClient(string payload)
        {
            if (!int.TryParse(payload, out int seconds)) return;
            JinxedHUD hud = FindFirstObjectByType<JinxedHUD>(FindObjectsInactive.Include);
            hud?.SetTimer(seconds);
        }

        private void HandleRoundStartClient(string payload)
        {
            // Populate _players on client if not already done
            if (_players.Count == 0)
            {
                var allPlayers = FindObjectsByType<PlayerObject>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None);
                _players.AddRange(allPlayers);
                //Debug.Log($"[Jinxed] Client populated _players: {_players.Count}");
            }

            var parts = payload.Split('|');
            if (parts.Length < 7) return;
            if (!int.TryParse(parts[0], out int jinxedId)) return;
            if (!int.TryParse(parts[1], out int roundNum)) return;
            if (!int.TryParse(parts[2], out int totalRounds)) return;
            if (!float.TryParse(parts[3], out float fallInterval)) return;
            if (!float.TryParse(parts[4], out float warnDur)) return;
            if (!float.TryParse(parts[5], out float dangerDur)) return;

            // Parse scores: "playerId:score,playerId:score,..."
            var scoreMap = new Dictionary<int, int>();
            var nameMap = new Dictionary<int, string>();

            foreach (var entry in parts[6].Split(','))
            {
                var s = entry.Split(':');
                if (s.Length < 3) continue;
                if (!int.TryParse(s[0], out int pid)) continue;
                if (!int.TryParse(s[1], out int sc)) continue;
                string name = s[2];
                int key = _players.FirstOrDefault(p => p.PlayerId == pid)?.Owner?.ClientId ?? pid;
                scoreMap[key] = sc;
                nameMap[key] = name;
            }


            //foreach (var p in _players)
            //    Debug.Log($"[Jinxed] NameMap — OwnerId: {p.Owner?.ClientId}, PlayerName: '{p.PlayerName}'");

            //Debug.Log($"[Jinxed] RoundStart payload length: {payload.Length}, parts: {parts.Length}");

            if (!FishNet.InstanceFinder.IsServerStarted)
            {
                _arenaGrid.ResetAllTiles();
                _arenaGrid.BuildGrid();
            }

            // Recompute fall order locally — deterministic, no need to send over network
            var fallIndices = _arenaGrid.GetFallOrder();

            if (_clientFallCoroutine != null) StopCoroutine(_clientFallCoroutine);
            _clientFallCoroutine = StartCoroutine(ClientTileFallCoroutine(fallIndices, fallInterval, warnDur, dangerDur));

            JinxedHUD hud = FindFirstObjectByType<JinxedHUD>(FindObjectsInactive.Include);
            if (hud != null)
            {
                hud.InitScoreRows(nameMap);

                // Apply accumulated scores after rows are built
                foreach (var kvp in scoreMap)
                    hud.UpdatePlayerScore(kvp.Key, kvp.Value);

                hud.OnRoundStart(roundNum, totalRounds, jinxedId);
            }

            foreach (var p in _players)
                p.GetComponent<JinxedPlayerEffect>()?.RemoveJinxEffect();

            PlayerObject startingJinxed = FindPlayerById(jinxedId);
            startingJinxed?.GetComponent<JinxedPlayerEffect>()?.ApplyJinxEffect();
        }

        private IEnumerator ClientTileFallCoroutine(List<int> fallOrder, float interval, float warnDur, float dangerDur)
        {
            foreach (int tileIndex in fallOrder)
            {
                yield return new WaitForSeconds(interval);
                _arenaGrid.BeginTileDrop(tileIndex, warnDur, dangerDur);
            }
        }

        private void HandleRoundEndClient(string payload)
        {
            if (_clientFallCoroutine != null)
            {
                StopCoroutine(_clientFallCoroutine);
                _clientFallCoroutine = null;
            }

            // Payload per player: "playerId:totalScore:totalSurvival:state|..."
            JinxedHUD hud = FindFirstObjectByType<JinxedHUD>(FindObjectsInactive.Include);
            if (hud == null) return;

            hud.OnRoundEnd();

            var entries = payload.Split('|');
            foreach (var entry in entries)
            {
                var parts = entry.Split(':');
                if (parts.Length < 4) continue;
                if (!int.TryParse(parts[0], out int playerId)) continue;
                if (!int.TryParse(parts[1], out int score)) continue;

                hud.UpdatePlayerScore(playerId, score);
            }
        }

        public override void OnClientAction(string messageType, string payload, NetworkConnection sender)
        {
            switch (messageType)
            {
                case "jinxed_tag_attempt":
                    HandleTagAttempt(payload);
                    break;
                case "jinxed_kill_request":
                    HandleKillRequest(payload);
                    break;
            }
        }

        private void HandleKillRequest(string payload)
        {
            if (!int.TryParse(payload, out int playerId)) return;
            if (!_jinxedPlayers.TryGetValue(playerId, out var pd)) return;
            if (pd.State == JinxedPlayerState.Eliminated) return;
            Debug.Log($"[Jinxed] HandleKillRequest — playerId: {playerId}, state: {pd?.State}");

            OnPlayerEliminated(playerId);

            // Teleport eliminated player to holding area
            PlayerObject po = _players.FirstOrDefault(p => p.PlayerId == playerId);
            if (po == null) return;

            // Lock movement immediately on server
            po.Movement.SetMovementLocked(true, "jinxed_round");

            if (_eliminationSpawnPoint != null)
            {
                NetworkTransform nt = po.GetComponent<NetworkTransform>();
                if (nt != null) nt.Teleport();
                po.transform.position = _eliminationSpawnPoint.position;
                po.transform.rotation = _eliminationSpawnPoint.rotation;
                GameRoomManager.Instance.TeleportPlayer(
                    po.Owner,
                    _eliminationSpawnPoint.position,
                    _eliminationSpawnPoint.rotation);
            }
        }

        // ── Game Flow ─────────────────────────────────────────────

        private IEnumerator RunGameCoroutine()
        {
            while (_currentRound < _totalRounds)
            {
                _currentRound++;
                yield return StartCoroutine(RunRoundCoroutine());
                yield return new WaitForSeconds(3f);
            }

            EndGameFinal();
        }

        private IEnumerator RunRoundCoroutine()
        {
            // Reset player states
            foreach (var pd in _jinxedPlayers.Values)
            {
                pd.State = JinxedPlayerState.Survivor;
                pd.EliminatedAt = -1f;
                pd.TagOnCooldown = false;
            }

            // Build grid and compute fall order
            _arenaGrid.BuildGrid();
            _fallOrder = _arenaGrid.GetFallOrder();
            _fallIndex = 0;
            int startingJinxedId = PickStartingJinxed();

            string scoreParts = string.Join(",", _jinxedPlayers.Values.Select(pd =>
            {
                PlayerObject po = _players.FirstOrDefault(p => p.PlayerId == pd.PlayerId);
                PlayerProfile profile = PlayerProfileManager.Instance.GetProfile(po?.Owner);
                string name = profile?.DisplayName ?? po?.PlayerName ?? $"Player_{pd.PlayerId}";
                return $"{pd.PlayerId}:{pd.TotalScore}:{name}";
            }));

            BroadcastMessage("jinxed_round_start",
                $"{startingJinxedId}|{_currentRound}|{_totalRounds}|{_tileFallInterval}|{_tileWarnDuration}|{_tileDangerDuration}|{scoreParts}");
                       
            // Small yield to let clients process round_start before state change
            yield return null;

            // Now set starting jinxed state
            _lastJinxedId = startingJinxedId;
            SetJinxedPlayerState(startingJinxedId, JinxedPlayerState.Jinxed);

            // Teleport players to spawns
            TeleportPlayersToSpawns();
            foreach (var p in _players)
                p.Movement.ClearAllMovementLocks();

            // Start tile fall
            _fallCoroutine = StartCoroutine(TileFallCoroutine());

            // Round timer loop
            _roundTimer = _roundDuration;
            _roundActive = true;
            float _lastTimerBroadcast = _roundDuration;

            while (_roundTimer > 0f && _roundActive)
            {
                _roundTimer -= Time.deltaTime;

                // Only broadcast timer once per second
                int currentSecond = Mathf.CeilToInt(_roundTimer);
                int lastSecond = Mathf.CeilToInt(_lastTimerBroadcast);
                if (currentSecond != lastSecond)
                {
                    BroadcastMessage("jinxed_timer", currentSecond.ToString());
                    _lastTimerBroadcast = _roundTimer;
                }

                int survivorCount = _jinxedPlayers.Values.Count(p => p.State == JinxedPlayerState.Survivor);
                if (survivorCount == 0)
                {
                    _roundActive = false;
                    break;
                }

                yield return null;
            }

            if (_fallCoroutine != null) StopCoroutine(_fallCoroutine);
            _roundActive = false;

            float survivalTime = _roundDuration - Mathf.Max(0f, _roundTimer);

            // Award round points
            var roundSorted = _jinxedPlayers.Values
                .OrderByDescending(p => p.State != JinxedPlayerState.Eliminated)
                .ThenByDescending(p => p.EliminatedAt)
                .ToList();

            for (int i = 0; i < roundSorted.Count; i++)
            {
                int score = i < _standingPoints.Length ? _standingPoints[i] : 0;
                roundSorted[i].TotalScore += score;
                roundSorted[i].TotalSurvival += roundSorted[i].EliminatedAt >= 0f
                    ? roundSorted[i].EliminatedAt
                    : survivalTime;
            }

            // Broadcast round end
            var roundEndParts = new List<string>();
            foreach (var pd in _jinxedPlayers.Values)
                roundEndParts.Add($"{pd.PlayerId}:{pd.TotalScore}:{pd.TotalSurvival:F1}:{(int)pd.State}");

            BroadcastMessage("jinxed_round_end", string.Join("|", roundEndParts));

            _arenaGrid.ResetAllTiles();
        }

        private IEnumerator TileFallCoroutine()
        {
            while (_fallIndex < _fallOrder.Count && _roundActive)
            {
                yield return new WaitForSeconds(_tileFallInterval);
                if (!_roundActive) yield break;

                int tileIndex = _fallOrder[_fallIndex];
                _fallIndex++;

                _arenaGrid.BeginTileDrop(tileIndex, _tileWarnDuration, _tileDangerDuration);
            }
        }

        // ── Tag Logic ─────────────────────────────────────────────

        private void HandleTagAttempt(string payload)
        {
            if (!TryParseTwo(payload, out int taggerId, out int targetId)) return;

            if (!_jinxedPlayers.TryGetValue(taggerId, out var tagger)) return;
            if (!_jinxedPlayers.TryGetValue(targetId, out var target)) return;

            if (!_roundActive) return;
            if (tagger.State != JinxedPlayerState.Jinxed) return;
            if (target.State != JinxedPlayerState.Survivor) return;
            if (tagger.TagOnCooldown) return;

            SetJinxedPlayerState(targetId, JinxedPlayerState.Jinxed);
        }

        // ── Elimination ───────────────────────────────────────────

        public void OnPlayerEliminated(int playerId)
        {
            

            if (!_jinxedPlayers.TryGetValue(playerId, out var pd)) return;
            if (pd.State == JinxedPlayerState.Eliminated) return;
            Debug.Log($"[Jinxed] OnPlayerEliminated — playerId: {playerId}, eliminatedAt: {pd.EliminatedAt}");

            pd.EliminatedAt = _roundDuration - Mathf.Max(0f, _roundTimer);
            SetJinxedPlayerState(playerId, JinxedPlayerState.Eliminated);
        }

        // ── End Game ──────────────────────────────────────────────

        private void EndGameFinal()
        {
            _gameActive = false;

            var sorted = _jinxedPlayers.Values
                .OrderByDescending(p => p.TotalScore)
                .ThenByDescending(p => p.TotalSurvival)
                .ToList();

            // Build results payload for clients
            var parts = new List<string>();
            for (int i = 0; i < sorted.Count; i++)
            {
                var pd = sorted[i];
                PlayerObject po = _players.FirstOrDefault(p => p.PlayerId == pd.PlayerId);
                PlayerProfile profile = PlayerProfileManager.Instance.GetProfile(po?.Owner);
                string name = profile?.DisplayName ?? po?.PlayerName ?? $"Player_{pd.PlayerId}";
                int careerPoints = i < _standingPoints.Length ? _standingPoints[i] : 0;
                parts.Add($"{pd.PlayerId}:{careerPoints}:{name}");
            }
            BroadcastMessage("jinxed_game_end", string.Join("|", parts));

            // Reset all player visuals
            foreach (var pd in _jinxedPlayers.Values)
                BroadcastMessage("jinxed_state_change", $"{pd.PlayerId}|{(int)JinxedPlayerState.Survivor}");

            GameRoomManager.Instance.NotifyGameComplete(this, GetResults());
        }

        protected override void OnShowResults(ResultsData data)
        {
            JinxedHUD hud = FindFirstObjectByType<JinxedHUD>(FindObjectsInactive.Include);
            hud?.SetScorePanelVisible(false);

            if (_resultsScreenPanel != null)
                _resultsScreenPanel.SetActive(true);

            if (hud != null)
            {
                var entries = new List<(string name, int score, string label)>();
                foreach (PlayerResultEntry entry in data.Entries)
                    entries.Add((entry.DisplayName, entry.PointsEarned, entry.ResultLabel));
                hud.ShowResults(entries);
            }

            StartCoroutine(ResultsTimerCoroutine());
        }

        private IEnumerator ResultsTimerCoroutine()
        {
            float remaining = _resultsDuration;
            while (remaining > 0f)
            {
                JinxedHUD hud = FindFirstObjectByType<JinxedHUD>(FindObjectsInactive.Include);
                hud?.SetResultsCountdown(Mathf.CeilToInt(remaining));
                yield return new WaitForSeconds(1f);
                remaining -= 1f;
            }

            if (_resultsScreenPanel != null)
                _resultsScreenPanel.SetActive(false);

            JinxedHUD hudFinal = FindFirstObjectByType<JinxedHUD>(FindObjectsInactive.Include);
            hudFinal?.SetScorePanelVisible(true);

            NotifyResultsDismissed();
        }

        private void HandleGameEndClient(string payload)
        {
            JinxedHUD hud = FindFirstObjectByType<JinxedHUD>(FindObjectsInactive.Include);
            hud?.SetScorePanelVisible(false);

            var entries = new List<(string name, int score, string label)>();
            var lines = payload.Split('|');
            for (int i = 0; i < lines.Length; i++)
            {
                var parts = lines[i].Split(':');
                if (parts.Length < 3) continue;
                if (!int.TryParse(parts[1], out int score)) continue;
                string name = parts[2];
                string label = GetResultLabel(i + 1);
                entries.Add((name, score, label));
            }

            hud.ShowResults(entries);

            if (_resultsScreenPanel != null)
                _resultsScreenPanel.SetActive(true);

            StartCoroutine(ClientResultsTimerCoroutine());
        }

        private IEnumerator ClientResultsTimerCoroutine()
        {
            float remaining = _resultsDuration;
            while (remaining > 0f)
            {
                JinxedHUD hud = FindFirstObjectByType<JinxedHUD>(FindObjectsInactive.Include);
                hud?.SetResultsCountdown(Mathf.CeilToInt(remaining));
                yield return new WaitForSeconds(1f);
                remaining -= 1f;
            }

            JinxedHUD hudFinal = FindFirstObjectByType<JinxedHUD>(FindObjectsInactive.Include);
            hudFinal?.SetScorePanelVisible(true);
        }

        // ── Helpers ───────────────────────────────────────────────

        private void SetJinxedPlayerState(int playerId, JinxedPlayerState state)
        {
            if (!_jinxedPlayers.TryGetValue(playerId, out var pd)) return;
            pd.State = state;
            BroadcastMessage("jinxed_state_change", $"{playerId}|{(int)state}");

            // Enable tag input on the newly jinxed player's client
            SetClientTagMode(playerId, state == JinxedPlayerState.Jinxed);
        }

        private void SetClientTagMode(int playerId, bool active)
        {
            PlayerObject po = _players.FirstOrDefault(p => p.PlayerId == playerId);
            if (po == null) return;
            GameRoomManager.Instance.SetPlayerTagMode(po.Owner, po.NetworkObject, active);
        }

        private int PickStartingJinxed()
        {
            var candidates = _jinxedPlayers.Keys
                .Where(id => id != _lastJinxedId)
                .ToList();

            if (candidates.Count == 0)
                candidates = _jinxedPlayers.Keys.ToList();

            return candidates[Random.Range(0, candidates.Count)];
        }

        private bool TryParseTwo(string payload, out int a, out int b)
        {
            a = b = 0;
            var parts = payload.Split('|');
            if (parts.Length < 2) return false;
            return int.TryParse(parts[0], out a) && int.TryParse(parts[1], out b);
        }

        private void BroadcastMessage(string messageType, string payload)
        {
            GameRoomManager.Instance.RpcMinigameMessage(messageType, payload);
        }
        private PlayerObject FindPlayerById(int playerId)
        {
            // First try existing _players list match by PlayerId
            var match = _players.FirstOrDefault(p => p.PlayerId == playerId);
            if (match != null) return match;

            // Fallback: search all PlayerObjects in scene by OwnerId
            var all = FindObjectsByType<PlayerObject>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            return all.FirstOrDefault(p => p.Owner?.ClientId == playerId);
        }

        protected override void TeleportPlayersToSpawns()
        {
            if (_spawnPoints == null || _spawnPoints.Length == 0)
            {
                Debug.LogWarning("[Jinxed] No spawn points assigned.");
                return;
            }

            for (int i = 0; i < _players.Count; i++)
            {
                PlayerObject player = _players[i];
                Vector3 pos = _spawnPoints[i % _spawnPoints.Length].position;
                Quaternion rot = _spawnPoints[i % _spawnPoints.Length].rotation;

                NetworkTransform nt = player.GetComponent<NetworkTransform>();
                if (nt != null) nt.Teleport();
                player.transform.position = pos;
                player.transform.rotation = rot;

                GameRoomManager.Instance.TeleportPlayer(player.Owner, pos, rot);
            }
        }

    }
}