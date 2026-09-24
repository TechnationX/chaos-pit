// LastOneStandingController.cs
// Server-authoritative controller for the Last One Standing minigame.
//
// Round flow:
//   - Wave schedule and alive-count check run concurrently every frame
//   - Round ends as soon as alive count hits 1 OR round timer expires
//   - Players respawn at their assigned spawn point between rounds
//   - Points accumulate across all rounds; final results ranked by total score

using FishNet.Component.Transforming;
using FishNet.Connection;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace ChaosPit.Minigames.LastOneStanding
{
    // ── Wave Config ───────────────────────────────────────────────
    [System.Serializable]
    public class TileWave
    {
        [Tooltip("How many tiles drop in this wave.")]
        public int tileCount = 10;

        [Tooltip("Seconds after round start before this wave fires. Each wave delay is relative to the previous wave trigger.")]
        public float waveDelay = 8f;

        [Tooltip("Seconds a tile stays yellow before turning red.")]
        public float warningDuration = 2f;

        [Tooltip("Seconds a tile stays red before disappearing.")]
        public float dangerDuration = 1f;
    }

    // ── Elimination Record ────────────────────────────────────────
    public class EliminationRecord
    {
        public PlayerObject Player;
        public float SurvivalTime;
        public int EliminationOrder; // 1 = first out, N = last out (winner)
    }

    public class LastOneStandingController : MiniGameController
    {
        // ── Inspector ─────────────────────────────────────────────
        [Header("Game Config")]
        [SerializeField] private int _totalRounds = 3;
        [SerializeField] private float _roundDuration = 120f;
        [SerializeField] private float _betweenRoundDuration = 5f;

        [Header("Wave Schedule")]
        [SerializeField]
        private TileWave[] _waves = new TileWave[]
        {
            new TileWave { tileCount =  20, waveDelay =  8f, warningDuration = 2f,   dangerDuration = 1f   },
            new TileWave { tileCount =  30, waveDelay = 10f, warningDuration = 2f,   dangerDuration = 1f   },
            new TileWave { tileCount =  40, waveDelay = 10f, warningDuration = 2f,   dangerDuration = 1f   },
            new TileWave { tileCount =  60, waveDelay =  8f, warningDuration = 1.5f, dangerDuration = 0.75f},
            new TileWave { tileCount = 100, waveDelay =  6f, warningDuration = 1f,   dangerDuration = 0.5f },
        };

        [Header("Career Score — Points Per Final Placement")]
        [SerializeField] private int[] _placementPoints = { 10, 8, 6, 3, 2, 1 };

        [Header("Shove")]
        [SerializeField] private float _shoveForce = 12f;
        [SerializeField] private float _shoveRange = 2f;
        [SerializeField] private float _shoveCooldown = 3f;
        [SerializeField] private float _shoveUpward = 3f;

        [Header("References")]
        [SerializeField] private LastOneStandingHUD _hud;
        [SerializeField] private ArenaGrid _arenaGrid;
        [SerializeField] private Transform _eliminatedWaitPoint;

        // ── Runtime State ─────────────────────────────────────────
        private Dictionary<int, string> _nameMap = new();
        private Dictionary<int, int> _totalScores = new();  // cumulative across rounds
        private Dictionary<int, int> _spawnIndex = new();  // playerId → spawn point index
        private List<EliminationRecord> _roundEliminations = new();
        private HashSet<int> _eliminatedThisRound = new();
        private Dictionary<int, float> _shoveTimestamps = new();

        private List<RoundResult> _finalResults = new();
        private Coroutine _gameLoopCoroutine;
        private Coroutine _waveCoroutine;
        private float _roundStartTime;
        private bool _roundActive;

        // ── MiniGameController Overrides ──────────────────────────

        public override void StartGame(List<PlayerObject> players)
        {
            if (!FishNet.InstanceFinder.IsServerStarted) return;

            _players = new List<PlayerObject>(players);
            _currentRound = 0;
            _gameActive = true;

            // Init cumulative score tracking
            _totalScores.Clear();
            _spawnIndex.Clear();
            for (int i = 0; i < _players.Count; i++)
            {
                int clientId = _players[i].Owner.ClientId;
                _totalScores[clientId] = 0;
                _spawnIndex[clientId] = i;
            }

            _arenaGrid.BuildGrid();

            GameRoomManager.Instance.RpcMinigameMessage("los_players", BuildPlayersPayload(), StationIndex);
            GameRoomManager.Instance.RpcMinigameMessage("los_grid_init",
                $"{_arenaGrid.GridWidth},{_arenaGrid.GridHeight}", StationIndex);

            _gameLoopCoroutine = StartCoroutine(GameLoopCoroutine());

            Debug.Log($"[LOS] StartGame — {_players.Count} players, {_totalRounds} rounds.");
        }

        public override void ClientInit()
        {
            // _hud is Inspector-wired directly to this scene's own HUD
            // instance, so no scoped-lookup risk here — just shows the (now
            // inactive-by-default) HUD GameObject. This only ever runs on a
            // real participant's own process (see RpcInitMinigame's comment
            // in GameRoomManager.cs), so it only ever shows the HUD to a
            // real player.
            _hud?.ShowHUD();
        }

        public override void StartRound()
        {
            _currentRound++;
            _roundActive = true;
            _roundStartTime = Time.time;

            _roundEliminations.Clear();
            _eliminatedThisRound.Clear();
            _shoveTimestamps.Clear();

            // Re-resolve every player's current display name before this
            // round's elimination banners can fire. _nameMap was previously
            // only ever populated once, from StartGame()'s "los_players"
            // message — if a just-joined player's real name hadn't finished
            // syncing in from PlayerProfileManager yet at that moment, the
            // elimination banner kept showing "Player_<id>" for the rest of
            // the game (same root cause fixed in BombTossController; see its
            // StartRound() comment). Uses a dedicated "los_refresh_names"
            // message rather than re-sending "los_players" so the client
            // doesn't also re-run InitScoreRows(), which would destroy and
            // recreate every score row and wipe out anyone's already-applied
            // eliminated/rank marker.
            GameRoomManager.Instance.RpcMinigameMessage("los_refresh_names", BuildPlayersPayload(), StationIndex);

            _arenaGrid.ResetAllTiles();

            // Tell clients to reset their grid
            GameRoomManager.Instance.RpcMinigameMessage("los_grid_reset", "", StationIndex);

            // Respawn all players at their assigned spawn points
            RespawnAllPlayers();

            GameRoomManager.Instance.RpcMinigameMessage("los_round_start",
                $"{_currentRound},{_totalRounds},{_roundDuration.ToString(System.Globalization.CultureInfo.InvariantCulture)}", StationIndex);

            //Debug.Log($"[LOS] StartRound {_currentRound}.");
        }

        public override void EndRound()
        {
            _roundActive = false;

            // Stop wave coroutine if still running
            if (_waveCoroutine != null)
            {
                StopCoroutine(_waveCoroutine);
                _waveCoroutine = null;
            }

            // Assign survivors as winners of this round
            AssignSurvivors();

            // Award points for this round based on elimination order
            AwardRoundPoints();

            GameRoomManager.Instance.RpcMinigameMessage("los_round_end", "", StationIndex);

            //Debug.Log($"[LOS] EndRound {_currentRound}. Scores: {ScoreSummary()}");
        }

        public override List<RoundResult> GetResults() => _finalResults;

        // ── Results Screen ─────────────────────────────────────────
        // Was missing entirely, same as Paint the Town — the alive/eliminated
        // status rows stayed visible underneath the final results screen
        // because nothing here ever hid them. Mirrors ThiefsMarketController's
        // OnShowResults/OnResultsHidden pair. This only covers the FINAL
        // game-end results screen (driven by GameRoomManager.OnGameComplete
        // via the base MiniGameController.ShowResults) — the existing
        // between-round display (_hud.ShowBetweenRoundCountdown/
        // HideBetweenRoundCountdown) is a separate, already-correct path.
        protected override void OnShowResults(ResultsData data)
        {
            _hud?.SetInRoundHudVisible(false);
        }

        protected override void OnResultsHidden()
        {
            _hud?.SetInRoundHudVisible(true);
        }

        public override void CleanUp()
        {
            if (_gameLoopCoroutine != null) StopCoroutine(_gameLoopCoroutine);
            if (_waveCoroutine != null) StopCoroutine(_waveCoroutine);
            StopAllCoroutines();

            foreach (PlayerObject p in _players)
                p.SetShadowCasting(true);

            _players.Clear();
            _nameMap.Clear();
            _totalScores.Clear();
            _spawnIndex.Clear();
            _roundEliminations.Clear();
            _eliminatedThisRound.Clear();
            _shoveTimestamps.Clear();
            _finalResults.Clear();
            _roundActive = false;
            _gameActive = false;

            _arenaGrid?.DestroyGrid();

            Debug.Log("[LOS] CleanUp complete.");
        }

        public override void RemovePlayer(PlayerObject player)
        {
            _players.Remove(player);
            _nameMap.Remove(player.PlayerId);
            _shoveTimestamps.Remove(player.PlayerId);
            player.SetShadowCasting(true);
            Debug.Log($"[LOS] Player removed: {player.PlayerId}");
        }

        // ── Client Action Override ────────────────────────────────

        public override void OnClientAction(string messageType, string payload, NetworkConnection sender)
        {
            switch (messageType)
            {
                case "los_kill_request":
                    if (int.TryParse(payload, out int clientId))
                        ProcessKillRequest(clientId);
                    break;

                case "los_shove_request":
                    if (int.TryParse(payload, out int shoveId))
                        ProcessShoveRequest(shoveId);
                    break;
            }
        }

        // ── Network Message Receiver (all clients) ────────────────

        public override void OnNetworkMessage(string messageType, string payload)
        {
            //Debug.Log($"[LOS Client] OnNetworkMessage: {messageType}");
            switch (messageType)
            {
                case "los_players":
                    ApplyPlayersPayload(payload);
                    break;

                case "los_refresh_names":
                    ApplyNameRefresh(payload);
                    break;

                case "los_grid_init":
                    ApplyGridInit(payload);
                    break;

                case "los_round_start":
                    string[] rp = payload.Split(',');
                    if (rp.Length < 3) break;
                    if (!int.TryParse(rp[0], out int roundNum)) break;
                    if (!int.TryParse(rp[1], out int totalRounds)) break;
                    if (!float.TryParse(rp[2], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float dur)) break;
                    _hud?.OnRoundStart(dur);
                    _hud?.SetRoundInfo(roundNum, totalRounds);
                    break;

                case "los_round_end":
                    _hud?.OnRoundEnd();
                    break;

                case "los_wave":
                    ApplyWavePayload(payload);
                    break;

                case "los_eliminated":
                    ApplyEliminatedPayload(payload);
                    break;

                case "los_shove_result":
                    ApplyShoveResult(payload);
                    break;

                case "los_countdown":
                    if (int.TryParse(payload, out int seconds))
                    {
                        if (seconds > 0) _hud?.ShowBetweenRoundCountdown(seconds);
                        else _hud?.HideBetweenRoundCountdown();
                    }
                    break;

                case "los_results":
                    if (!FishNet.InstanceFinder.IsServerStarted)
                        ApplyResultsPayload(payload);
                    break;

                case "los_scores":
                    ApplyScoresPayload(payload);
                    break;

                case "los_grid_reset":
                    if (!FishNet.InstanceFinder.IsServerStarted)
                        _arenaGrid?.ResetAllTiles();
                    break;
            }
        }

        // ── Game Loop ─────────────────────────────────────────────

        private IEnumerator GameLoopCoroutine()
        {
            while (_currentRound < _totalRounds)
            {
                StartRound();
                yield return StartCoroutine(RoundCoroutine());
                EndRound();

                if (_currentRound < _totalRounds)
                    yield return StartCoroutine(BetweenRoundCoroutine());
            }

            // All rounds done — build final results from cumulative scores
            _finalResults = BuildFinalResults();
            GameRoomManager.Instance.RpcMinigameMessage("los_results", BuildResultsPayload(), StationIndex);
            GameRoomManager.Instance.NotifyGameComplete(this, _finalResults);
        }

        private IEnumerator RoundCoroutine()
        {
            _waveCoroutine = StartCoroutine(WaveScheduleCoroutine());

            float elapsed = 0f;

            // Need at least 2 players for the "last one standing" condition to make sense
            int startingCount = _players.Count;

            while (_roundActive)
            {
                elapsed += Time.deltaTime;

                // Only end early if more than one player started this round
                if (startingCount > 1 && GetAliveCount() <= 1)
                {
                    Debug.Log("[LOS] Round ended — last player standing");
                    _roundActive = false;
                    break;
                }

                if (elapsed >= _roundDuration)
                {
                    Debug.Log("[LOS] Round ended — timer expired");
                    _roundActive = false;
                    break;
                }

                yield return null;
            }

            if (_waveCoroutine != null)
            {
                StopCoroutine(_waveCoroutine);
                _waveCoroutine = null;
            }
        }

        private IEnumerator WaveScheduleCoroutine()
        {
            foreach (TileWave wave in _waves)
            {
                if (!_roundActive) yield break;

                yield return new WaitForSeconds(wave.waveDelay);

                if (!_roundActive) yield break;

                List<int> tilesToDrop = _arenaGrid.PickRandomSafeTiles(wave.tileCount);
                if (tilesToDrop.Count == 0) continue;

                // Apply on server grid
                foreach (int idx in tilesToDrop)
                    _arenaGrid.BeginTileDrop(idx, wave.warningDuration, wave.dangerDuration);

                // Send in chunks of 20 to avoid oversized RPC payloads
                const int chunkSize = 20;
                for (int i = 0; i < tilesToDrop.Count; i += chunkSize)
                {
                    List<int> chunk = tilesToDrop.GetRange(i, Mathf.Min(chunkSize, tilesToDrop.Count - i));
                    string wavePayload = BuildWavePayload(chunk, wave.warningDuration, wave.dangerDuration);
                    GameRoomManager.Instance.RpcMinigameMessage("los_wave", wavePayload, StationIndex);

                    // Small yield between chunks to avoid flooding
                    yield return null;
                }
            }
        }

        private IEnumerator BetweenRoundCoroutine()
        {
            int remaining = Mathf.RoundToInt(_betweenRoundDuration);
            while (remaining > 0)
            {
                GameRoomManager.Instance.RpcMinigameMessage("los_countdown", remaining.ToString(), StationIndex);
                yield return new WaitForSeconds(1f);
                remaining--;
            }
            GameRoomManager.Instance.RpcMinigameMessage("los_countdown", "0", StationIndex);
        }

        // ── Respawn ───────────────────────────────────────────────

        private void RespawnAllPlayers()
        {
            Transform[] spawns = SpawnPoints;
            if (spawns == null || spawns.Length == 0)
            {
                Debug.LogWarning("[LOS] No spawn points assigned.");
                return;
            }

            foreach (PlayerObject player in _players)
            {
                int clientId = player.Owner.ClientId;
                int idx = _spawnIndex.TryGetValue(clientId, out int si)
                    ? si % spawns.Length : 0;

                //Debug.Log($"[LOS] Respawning PlayerId:{player.PlayerId} ClientId:{player.Owner?.ClientId} to spawn {idx}");

                Vector3 pos = spawns[idx].position;
                Quaternion rot = spawns[idx].rotation;

                NetworkTransform nt = player.GetComponent<NetworkTransform>();
                if (nt != null) nt.Teleport();
                player.transform.position = pos;
                player.transform.rotation = rot;

                GameRoomManager.Instance.TeleportPlayer(player.Owner, pos, rot);
                player.SetShadowCasting(true);
            }
        }

        // ── Elimination ───────────────────────────────────────────

        private void ProcessKillRequest(int clientId)
        {
            if (!FishNet.InstanceFinder.IsServerStarted) return;
            if (!_roundActive) return;

            PlayerObject player = FindPlayerByClientId(clientId);
            if (player == null) return;

            int playerId = player.PlayerId;
            if (_eliminatedThisRound.Contains(playerId)) return;

            _eliminatedThisRound.Add(playerId);

            int order = _roundEliminations.Count + 1; // 1 = first out
            float survivalTime = Time.time - _roundStartTime;

            _roundEliminations.Add(new EliminationRecord
            {
                Player = player,
                SurvivalTime = survivalTime,
                EliminationOrder = order
            });

            // Notify all clients
            GameRoomManager.Instance.RpcMinigameMessage("los_eliminated",
                $"{playerId},{order},{survivalTime.ToString(System.Globalization.CultureInfo.InvariantCulture)}", StationIndex);

            // Teleport eliminated player to wait point
            if (_eliminatedWaitPoint != null)
            {
                NetworkTransform nt = player.GetComponent<NetworkTransform>();
                if (nt != null) nt.Teleport();
                player.transform.position = _eliminatedWaitPoint.position;
                player.transform.rotation = _eliminatedWaitPoint.rotation;
                GameRoomManager.Instance.TeleportPlayer(
                    player.Owner,
                    _eliminatedWaitPoint.position,
                    _eliminatedWaitPoint.rotation);
                player.SetShadowCasting(false);
            }

            //Debug.Log($"[LOS] Player {playerId} eliminated. Order: {order}, Survival: {survivalTime:F1}s");

            if (GetAliveCount() <= 1)
                _roundActive = false;
        }

        private PlayerObject FindPlayerByClientId(int clientId)
        {
            foreach (PlayerObject p in _players)
                if (p.Owner != null && p.Owner.ClientId == clientId) return p;
            return null;
        }

        private void AssignSurvivors()
        {
            foreach (PlayerObject player in _players)
            {
                if (_eliminatedThisRound.Contains(player.PlayerId)) continue;

                int order = _roundEliminations.Count + 1;
                _roundEliminations.Add(new EliminationRecord
                {
                    Player = player,
                    SurvivalTime = Time.time - _roundStartTime,
                    EliminationOrder = order
                });
                _eliminatedThisRound.Add(player.PlayerId);
            }
        }

        // ── Scoring ───────────────────────────────────────────────

        private void AwardRoundPoints()
        {
            var sorted = new List<EliminationRecord>(_roundEliminations);
            sorted.Sort((a, b) => b.EliminationOrder.CompareTo(a.EliminationOrder));

            for (int i = 0; i < sorted.Count; i++)
            {
                int clientId = sorted[i].Player.Owner.ClientId;
                int standing = i + 1;
                int points = CalculatePlacementPoints(standing, sorted.Count);

                if (!_totalScores.ContainsKey(clientId))
                    _totalScores[clientId] = 0;

                _totalScores[clientId] += points;
            }

            GameRoomManager.Instance.RpcMinigameMessage("los_scores", BuildScoresPayload(), StationIndex);
        }

        private string BuildScoresPayload()
        {
            var parts = new List<string>();
            foreach (var kvp in _totalScores)
                parts.Add($"{kvp.Key},{kvp.Value}");
            return string.Join("|", parts);
        }

        private void ApplyScoresPayload(string payload)
        {
            if (string.IsNullOrEmpty(payload)) return;
            foreach (string entry in payload.Split('|'))
            {
                string[] p = entry.Split(',');
                if (p.Length < 2) continue;
                if (!int.TryParse(p[0], out int id)) continue;
                if (!int.TryParse(p[1], out int score)) continue;
                _hud?.UpdatePlayerScore(id, score);
            }
        }

        private List<RoundResult> BuildFinalResults()
        {
            var sorted = new List<PlayerObject>(_players);
            sorted.Sort((a, b) =>
            {
                int scoreA = _totalScores.TryGetValue(a.PlayerId, out int sa) ? sa : 0;
                int scoreB = _totalScores.TryGetValue(b.PlayerId, out int sb) ? sb : 0;
                return scoreB.CompareTo(scoreA);
            });

            var results = new List<RoundResult>();
            for (int i = 0; i < sorted.Count; i++)
            {
                PlayerObject player = sorted[i];
                int standing = i + 1;
                int career = CalculatePlacementPoints(standing, sorted.Count);

                results.Add(new RoundResult(player, standing, career, GetResultLabel(standing)));
            }

            return results;
        }

        private int CalculatePlacementPoints(int standing, int totalPlayers)
        {
            int lastIndex = _placementPoints.Length - 1;
            int index = lastIndex - totalPlayers + standing;
            index = Mathf.Clamp(index, 0, lastIndex);
            return _placementPoints[index];
        }

        // ── Shove ─────────────────────────────────────────────────

        private void ProcessShoveRequest(int shoverPlayerId)
        {
            if (!FishNet.InstanceFinder.IsServerStarted) return;
            if (_eliminatedThisRound.Contains(shoverPlayerId)) return;

            float now = Time.time;
            if (_shoveTimestamps.TryGetValue(shoverPlayerId, out float lastShove))
                if (now - lastShove < _shoveCooldown) return;

            _shoveTimestamps[shoverPlayerId] = now;

            PlayerObject shover = FindPlayerById(shoverPlayerId);
            if (shover == null) return;

            Vector3 shoverPos = shover.transform.position;
            PlayerObject target = null;
            float closest = _shoveRange;

            foreach (PlayerObject p in _players)
            {
                if (p.PlayerId == shoverPlayerId) continue;
                if (_eliminatedThisRound.Contains(p.PlayerId)) continue;

                float dist = Vector3.Distance(shoverPos, p.transform.position);
                if (dist < closest) { closest = dist; target = p; }
            }

            if (target == null) return;

            Vector3 dir = (target.transform.position - shoverPos);
            dir.y = 0f;
            dir = dir.normalized;
            Vector3 impulse = dir * _shoveForce + Vector3.up * _shoveUpward;

            Rigidbody rb = target.GetComponent<Rigidbody>();
            if (rb != null) rb.AddForce(impulse, ForceMode.Impulse);

            string impStr =
                $"{impulse.x.ToString(System.Globalization.CultureInfo.InvariantCulture)}," +
                $"{impulse.y.ToString(System.Globalization.CultureInfo.InvariantCulture)}," +
                $"{impulse.z.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

            GameRoomManager.Instance.RpcMinigameMessage("los_shove_result", $"{target.PlayerId},{impStr}", StationIndex);
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

        private string BuildWavePayload(List<int> indices, float warn, float danger)
        {
            string warnStr = warn.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string dangerStr = danger.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var parts = new List<string>();
            foreach (int idx in indices)
                parts.Add($"{idx},{warnStr},{dangerStr}");
            return string.Join("|", parts);
        }

        private string BuildResultsPayload()
        {
            var parts = new List<string>();
            foreach (RoundResult r in _finalResults)
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
            _hud?.InitScoreRows(_nameMap);
        }

        // Same "id,name|id,name|..." format as ApplyPlayersPayload, but
        // updates only _nameMap — doesn't touch the HUD's score rows, unlike
        // ApplyPlayersPayload (which calls InitScoreRows and would destroy/
        // recreate them). See StartRound() for why this exists.
        private void ApplyNameRefresh(string payload)
        {
            if (string.IsNullOrEmpty(payload)) return;
            foreach (string entry in payload.Split('|'))
            {
                string[] p = entry.Split(',');
                if (p.Length < 2) continue;
                if (!int.TryParse(p[0], out int id)) continue;
                _nameMap[id] = p[1];
            }
        }

        private void ApplyGridInit(string payload)
        {
            if (FishNet.InstanceFinder.IsServerStarted) return;
            string[] p = payload.Split(',');
            if (p.Length < 2) return;
            if (!int.TryParse(p[0], out int w) || !int.TryParse(p[1], out int h)) return;
            _arenaGrid?.BuildGrid(w, h);
        }

        private void ApplyWavePayload(string payload)
        {
            if (string.IsNullOrEmpty(payload)) return;
            foreach (string entry in payload.Split('|'))
            {
                string[] p = entry.Split(',');
                if (p.Length < 3) continue;
                if (!int.TryParse(p[0], out int idx)) continue;
                if (!float.TryParse(p[1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float warn)) continue;
                if (!float.TryParse(p[2], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float danger)) continue;
                _arenaGrid?.BeginTileDrop(idx, warn, danger);
            }
        }

        private void ApplyEliminatedPayload(string payload)
        {
            string[] p = payload.Split(',');
            if (p.Length < 2) return;
            if (!int.TryParse(p[0], out int playerId)) return;
            if (!int.TryParse(p[1], out int order)) return;

            _hud?.MarkPlayerEliminated(playerId, order);

            string name = _nameMap.TryGetValue(playerId, out string n) ? n : $"Player_{playerId}";
            PlayerObject local = FindLocalPlayer();
            if (local != null && local.PlayerId == playerId)
                _hud?.ShowEliminationBanner(null);
            else
                _hud?.ShowEliminationBanner(name);
        }

        private void ApplyShoveResult(string payload)
        {
            string[] p = payload.Split(',');
            if (p.Length < 4) return;
            if (!int.TryParse(p[0], out int targetId)) return;
            if (!float.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float fx)) return;
            if (!float.TryParse(p[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float fy)) return;
            if (!float.TryParse(p[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float fz)) return;

            if (FishNet.InstanceFinder.IsServerStarted) return;

            PlayerObject target = FindPlayerById(targetId);
            if (target == null) return;

            Rigidbody rb = target.GetComponent<Rigidbody>();
            if (rb != null) rb.AddForce(new Vector3(fx, fy, fz), ForceMode.Impulse);
        }

        private void ApplyResultsPayload(string payload)
        {
            if (string.IsNullOrEmpty(payload)) return;

            var entries = new List<PlayerResultEntry>();
            foreach (string entry in payload.Split('|'))
            {
                // id,standing,points,label,level
                string[] p = entry.Split(',');
                if (p.Length < 5) continue;
                if (!int.TryParse(p[0], out int id)) continue;
                if (!int.TryParse(p[1], out int standing)) continue;
                int points = int.TryParse(p[2], out int pts) ? pts : 0;
                int level = int.TryParse(p[4], out int lvl) ? lvl : 1;

                entries.Add(new PlayerResultEntry
                {
                    DisplayName = _nameMap.TryGetValue(id, out string n) ? n : $"Player_{id}",
                    Standing = standing,
                    ResultLabel = p[3],
                    PointsEarned = points,
                    CareerLevel = level
                });
            }

            ShowResultsClientOnly(new ResultsData { Entries = entries });
        }

        // ── Helpers ───────────────────────────────────────────────

        private int GetAliveCount()
        {
            int count = 0;
            foreach (PlayerObject p in _players)
                if (!_eliminatedThisRound.Contains(p.PlayerId)) count++;
            return count;
        }

        private PlayerObject FindPlayerById(int id)
        {
            foreach (PlayerObject p in _players)
                if (p.PlayerId == id) return p;
            return null;
        }

        private PlayerObject FindLocalPlayer()
        {
            foreach (var p in FindObjectsByType<PlayerObject>(
                FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (p.IsOwner) return p;
            return null;
        }
    }
}