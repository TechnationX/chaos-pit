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
        public int TagsThisRound = 0;
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

        [Header("Scoring")]
        [SerializeField] private int[] _placementPoints = { 10, 7, 5, 3, 1, 0 };
        [SerializeField] private int _pointsPerTag = 2;

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

        private JinxedHUD _hud;

        // ── MiniGameController Overrides ──────────────────────────

        public override void ClientInit()
        {
            // Shows the (now inactive-by-default) HUD GameObject — see
            // JinxedHUD.ShowHUD's comment. This only ever runs on a real
            // participant's own process (see RpcInitMinigame's comment in
            // GameRoomManager.cs), so it only ever shows the HUD to a real
            // player.
            GetHud()?.ShowHUD();
        }

        // Every method below used to call FindFirstObjectByType<JinxedHUD>(...)
        // fresh, each time, searching every loaded scene rather than just this
        // controller's own. On a host running two concurrent Jinxed games (two
        // stations), that could resolve to the OTHER station's HUD instance
        // instead of this one's — same bug class as GameRoomManager's
        // FindActiveMinigameController before it was scoped to
        // GetStationScene(stationIndex). Cached and scoped here the same way:
        // resolved once, from this controller's own Scene, and reused.
        private JinxedHUD GetHud()
        {
            if (_hud != null) return _hud;

            foreach (GameObject root in gameObject.scene.GetRootGameObjects())
            {
                JinxedHUD hud = root.GetComponentInChildren<JinxedHUD>(true);
                if (hud != null) { _hud = hud; break; }
            }

            return _hud;
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

        public override void StartRound() { }

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
                int careerPoints = CalculatePlacementPoints(standing, sorted.Count);
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
                p.GetComponent<JinxedPlayerEffect>()?.RemoveJinxEffect();
            }

            _arenaGrid?.DestroyGrid();
        }

        public override void OnNetworkMessage(string messageType, string payload)
        {
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
                pd.TagsThisRound = 0;
            }

            // Build grid and compute fall order
            _arenaGrid.BuildGrid();
            _fallOrder = _arenaGrid.GetFallOrder();
            _fallIndex = 0;

            // Pick starting jinxed
            int startingJinxedId = PickStartingJinxed();
            _lastJinxedId = startingJinxedId;

            // Build round start payload with scores and names
            string scoreParts = string.Join(",", _jinxedPlayers.Values.Select(pd =>
            {
                PlayerObject po = _players.FirstOrDefault(p => p.PlayerId == pd.PlayerId);
                PlayerProfile profile = PlayerProfileManager.Instance.GetProfile(po?.Owner);
                string name = profile?.DisplayName ?? po?.PlayerName ?? $"Player_{pd.PlayerId}";
                return $"{pd.PlayerId}:{pd.TotalScore}:{name}";
            }));

            // Broadcast round start FIRST so clients populate before state changes arrive
            BroadcastMessage("jinxed_round_start",
                $"{startingJinxedId}|{_currentRound}|{_totalRounds}|{_tileFallInterval}|{_tileWarnDuration}|{_tileDangerDuration}|{scoreParts}");

            // Small yield to let clients process round_start before state change
            yield return null;

            // Now set starting jinxed state
            SetJinxedPlayerState(startingJinxedId, JinxedPlayerState.Jinxed);

            // Teleport players to spawns
            TeleportPlayersToSpawns();

            // Start tile fall
            _fallCoroutine = StartCoroutine(TileFallCoroutine());

            // Round timer loop
            _roundTimer = _roundDuration;
            _roundActive = true;
            float _lastTimerBroadcast = _roundDuration;

            while (_roundTimer > 0f && _roundActive)
            {
                _roundTimer -= Time.deltaTime;

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

            // Award round scores
            float survivalTime = _roundDuration - Mathf.Max(0f, _roundTimer);

            var roundSorted = _jinxedPlayers.Values
                .OrderByDescending(p => p.State != JinxedPlayerState.Eliminated)
                .ThenByDescending(p => p.EliminatedAt)
                .ToList();

            for (int i = 0; i < roundSorted.Count; i++)
            {
                int placementScore = CalculatePlacementPoints(i + 1, roundSorted.Count);
                int tagScore = roundSorted[i].TagsThisRound * _pointsPerTag;
                roundSorted[i].TotalScore += placementScore + tagScore;
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
            tagger.TagsThisRound++;
            StartCoroutine(TagCooldownCoroutine(taggerId));
        }

        private IEnumerator TagCooldownCoroutine(int taggerId)
        {
            if (!_jinxedPlayers.TryGetValue(taggerId, out var pd)) yield break;
            pd.TagOnCooldown = true;
            yield return new WaitForSeconds(3f);
            pd.TagOnCooldown = false;
        }

        // ── Elimination ───────────────────────────────────────────

        private void HandleKillRequest(string payload)
        {
            if (!int.TryParse(payload, out int playerId)) return;
            if (!_jinxedPlayers.TryGetValue(playerId, out var pd)) return;
            if (pd.State == JinxedPlayerState.Eliminated) return;

            OnPlayerEliminated(playerId);

            PlayerObject po = _players.FirstOrDefault(p => p.PlayerId == playerId);
            if (po == null) return;

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

        public void OnPlayerEliminated(int playerId)
        {
            if (!_jinxedPlayers.TryGetValue(playerId, out var pd)) return;
            if (pd.State == JinxedPlayerState.Eliminated) return;

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

            var parts = new List<string>();
            for (int i = 0; i < sorted.Count; i++)
            {
                var pd = sorted[i];
                PlayerObject po = _players.FirstOrDefault(p => p.PlayerId == pd.PlayerId);
                PlayerProfile profile = PlayerProfileManager.Instance.GetProfile(po?.Owner);
                string name = profile?.DisplayName ?? po?.PlayerName ?? $"Player_{pd.PlayerId}";
                int careerPoints = CalculatePlacementPoints(i + 1, sorted.Count);
                parts.Add($"{pd.PlayerId}:{careerPoints}:{name}");
            }

            BroadcastMessage("jinxed_game_end", string.Join("|", parts));

            // Reset all player visuals
            foreach (var pd in _jinxedPlayers.Values)
                BroadcastMessage("jinxed_state_change", $"{pd.PlayerId}|{(int)JinxedPlayerState.Survivor}");

            GameRoomManager.Instance.NotifyGameComplete(this, GetResults());
        }

        // ── Results ───────────────────────────────────────────────

        protected override void OnShowResults(ResultsData data)
        {
            JinxedHUD hud = GetHud();
            hud?.SetInRoundHudVisible(false);

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
                JinxedHUD hud = GetHud();
                hud?.SetResultsCountdown(Mathf.CeilToInt(remaining));
                yield return new WaitForSeconds(1f);
                remaining -= 1f;
            }

            if (_resultsScreenPanel != null)
                _resultsScreenPanel.SetActive(false);

            JinxedHUD hudFinal = GetHud();
            hudFinal?.SetInRoundHudVisible(true);

            NotifyResultsDismissed();
        }

        // ── Client Handlers ───────────────────────────────────────

        private void HandleRoundStartClient(string payload)
        {
            // Populate _players first
            if (_players.Count == 0)
            {
                var allPlayers = FindObjectsByType<PlayerObject>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None);
                _players.AddRange(allPlayers);
            }

            var parts = payload.Split('|');
            if (parts.Length < 7) return;
            if (!int.TryParse(parts[0], out int jinxedId)) return;
            if (!int.TryParse(parts[1], out int roundNum)) return;
            if (!int.TryParse(parts[2], out int totalRounds)) return;
            if (!float.TryParse(parts[3], out float fallInterval)) return;
            if (!float.TryParse(parts[4], out float warnDur)) return;
            if (!float.TryParse(parts[5], out float dangerDur)) return;

            // Parse scores and names
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

            if (!FishNet.InstanceFinder.IsServerStarted)
            {
                _arenaGrid.ResetAllTiles();
                _arenaGrid.BuildGrid();
            }

            var fallIndices = _arenaGrid.GetFallOrder();
            if (_clientFallCoroutine != null) StopCoroutine(_clientFallCoroutine);
            _clientFallCoroutine = StartCoroutine(ClientTileFallCoroutine(
                fallIndices, fallInterval, warnDur, dangerDur));

            JinxedHUD hud = GetHud();
            if (hud != null)
            {
                hud.InitScoreRows(nameMap);

                foreach (var kvp in scoreMap)
                    hud.UpdatePlayerScore(kvp.Key, kvp.Value);

                hud.OnRoundStart(roundNum, totalRounds, jinxedId);
            }

            foreach (var p in _players)
                p.GetComponent<JinxedPlayerEffect>()?.RemoveJinxEffect();

            PlayerObject startingJinxed = FindPlayerById(jinxedId);
            startingJinxed?.GetComponent<JinxedPlayerEffect>()?.ApplyJinxEffect();
        }

        private IEnumerator ClientTileFallCoroutine(List<int> fallOrder, float interval,
            float warnDur, float dangerDur)
        {
            foreach (int tileIndex in fallOrder)
            {
                yield return new WaitForSeconds(interval);
                _arenaGrid.BeginTileDrop(tileIndex, warnDur, dangerDur);
            }
        }

        private void HandleStateChangeClient(string payload)
        {
            if (!TryParseTwo(payload, out int playerId, out int stateInt)) return;

            JinxedPlayerState state = (JinxedPlayerState)stateInt;

            // Update HUD for local player
            PlayerObject local = _players.FirstOrDefault(p => p.IsOwner);
            if (local != null && local.Owner?.ClientId == playerId)
            {
                JinxedHUD hud = GetHud();
                hud?.SetPlayerStatus(playerId, state);
            }

            // Update score row status for all players
            JinxedHUD allHud = GetHud();
            allHud?.SetPlayerStatus(playerId, state);

            // Apply visual effect
            PlayerObject target = FindPlayerById(playerId);
            if (target == null) return;

            JinxedPlayerEffect effect = target.GetComponent<JinxedPlayerEffect>();
            if (effect == null) return;

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

        private void HandleTimerClient(string payload)
        {
            if (!int.TryParse(payload, out int seconds)) return;
            JinxedHUD hud = GetHud();
            hud?.SetTimer(seconds);
        }

        private void HandleRoundEndClient(string payload)
        {
            if (_clientFallCoroutine != null)
            {
                StopCoroutine(_clientFallCoroutine);
                _clientFallCoroutine = null;
            }

            JinxedHUD hud = GetHud();
            if (hud == null) return;

            hud.OnRoundEnd();

            var entries = payload.Split('|');
            foreach (var entry in entries)
            {
                var ps = entry.Split(':');
                if (ps.Length < 4) continue;
                if (!int.TryParse(ps[0], out int playerId)) continue;
                if (!int.TryParse(ps[1], out int score)) continue;
                hud.UpdatePlayerScore(playerId, score);
            }
        }

        private void HandleGameEndClient(string payload)
        {
            JinxedHUD hud = GetHud();
            hud?.SetInRoundHudVisible(false);

            if (_resultsScreenPanel != null)
                _resultsScreenPanel.SetActive(true);

            if (hud == null) return;

            var entries = new List<(string name, int score, string label)>();
            var lines = payload.Split('|');
            for (int i = 0; i < lines.Length; i++)
            {
                var ps = lines[i].Split(':');
                if (ps.Length < 3) continue;
                if (!int.TryParse(ps[1], out int score)) continue;
                string name = ps[2];
                string label = GetResultLabel(i + 1);
                entries.Add((name, score, label));
            }

            hud.ShowResults(entries);
            StartCoroutine(ClientResultsTimerCoroutine());
        }

        private IEnumerator ClientResultsTimerCoroutine()
        {
            float remaining = _resultsDuration;
            while (remaining > 0f)
            {
                JinxedHUD hud = GetHud();
                hud?.SetResultsCountdown(Mathf.CeilToInt(remaining));
                yield return new WaitForSeconds(1f);
                remaining -= 1f;
            }

            JinxedHUD hudFinal = GetHud();
            hudFinal?.SetInRoundHudVisible(true);
        }

        // ── Teleport ──────────────────────────────────────────────

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

        // ── Helpers ───────────────────────────────────────────────

        private void SetJinxedPlayerState(int playerId, JinxedPlayerState state)
        {
            if (!_jinxedPlayers.TryGetValue(playerId, out var pd)) return;
            pd.State = state;
            BroadcastMessage("jinxed_state_change", $"{playerId}|{(int)state}");
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

        private PlayerObject FindPlayerById(int playerId)
        {
            var match = _players.FirstOrDefault(p => p.PlayerId == playerId);
            if (match != null) return match;

            var all = FindObjectsByType<PlayerObject>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            return all.FirstOrDefault(p => p.Owner?.ClientId == playerId);
        }

        private int CalculatePlacementPoints(int standing, int totalPlayers)
        {
            int lastIndex = _placementPoints.Length - 1;
            int index = lastIndex - totalPlayers + standing;
            index = Mathf.Clamp(index, 0, lastIndex);
            return _placementPoints[index];
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
            GameRoomManager.Instance.RpcMinigameMessage(messageType, payload, StationIndex);
        }
    }
}