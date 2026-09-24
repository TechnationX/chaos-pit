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
        [Header("Game Config")]
        [SerializeField] private float _roundDuration = 75f;
        [SerializeField] private float _syncInterval = 0.2f;

        [SerializeField] private int[] _placementPoints = { 10, 8, 6, 3, 2, 1 };

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
            // Re-resolve every player's current display name before this
            // round's score rows/results can be read. _nameMap was
            // previously only ever populated once, from StartGame()'s
            // "colors" message — if a just-joined player's real name hadn't
            // finished syncing in from PlayerProfileManager yet at that
            // moment, their score row (and the end-of-round results screen,
            // which also reads _nameMap) kept showing "Player_<id>" for the
            // rest of the game (same root cause fixed in
            // BombTossController; see its StartRound() comment). Uses a
            // dedicated "refresh_names" message rather than re-sending
            // "colors" so the client doesn't also re-run InitScoreRows,
            // which would instantiate a second, duplicate set of rows
            // (InitScoreRows never clears existing ones first).
            GameRoomManager.Instance.RpcMinigameMessage("refresh_names", BuildNamesPayload(), StationIndex);

            _tileGrid.ResetAllTiles();
            _tileGrid.FlushDirtyTiles();
            InitTileCounts();

            _timeRemaining = _roundDuration;
            _roundActive = true;

            _syncCoroutine = StartCoroutine(BatchSyncCoroutine());

            GameRoomManager.Instance.RpcMinigameMessage("reset_tiles", "", StationIndex);
            GameRoomManager.Instance.RpcMinigameMessage("round_start",
                _roundDuration.ToString(System.Globalization.CultureInfo.InvariantCulture), StationIndex);

            Debug.Log("[PaintTheTown] Round started.");
        }

        public override void ClientInit()
        {
            _tileGrid.GenerateGrid();

            // _hud is Inspector-wired directly to this scene's own HUD
            // instance, so no scoped-lookup risk here — just shows the (now
            // inactive-by-default) HUD GameObject. This only ever runs on a
            // real participant's own process (see RpcInitMinigame's comment
            // in GameRoomManager.cs), so it only ever shows the HUD to a
            // real player.
            _hud?.ShowHUD();

            FindLocalPlayer()?.Movement.SetStaminaLimited(true);
        }

        private PlayerObject FindLocalPlayer()
        {
            foreach (var p in FindObjectsByType<PlayerObject>(FindObjectsSortMode.None))
                if (p.IsOwner) return p;
            return null;
        }

        // ── Results Screen ─────────────────────────────────────────
        // Was missing entirely — the score rows stayed visible underneath the
        // results screen because nothing here ever hid them. Mirrors
        // ThiefsMarketController's OnShowResults/OnResultsHidden pair. Paint
        // the Town is currently single-round (see GameLoopCoroutine — one
        // StartRound() call, then EndRound()), so OnResultsHidden never
        // actually fires before the scene unloads, but it's kept symmetrical
        // in case a multi-round mode is added later.
        protected override void OnShowResults(ResultsData data)
        {
            _hud?.SetInRoundHudVisible(false);
        }

        protected override void OnResultsHidden()
        {
            _hud?.SetInRoundHudVisible(true);
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
            GameRoomManager.Instance.RpcMinigameMessage("results", BuildResultsPayload(), StationIndex);

            GameRoomManager.Instance.RpcMinigameMessage("round_end", "", StationIndex);

            Debug.Log("[PaintTheTown] Round ended.");
        }

        public override List<RoundResult> GetResults() => _roundResults;

        public override void CleanUp()
        {
            if (_gameLoopCoroutine != null) StopCoroutine(_gameLoopCoroutine);
            if (_syncCoroutine != null) StopCoroutine(_syncCoroutine);
            StopAllCoroutines();

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
                case "refresh_names": ApplyNameRefresh(payload); break;
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

            GameRoomManager.Instance.RpcMinigameMessage("tiles", BuildTilesPayload(deltas), StationIndex);
            UpdateAndBroadcastCounts();
        }

        private void UpdateAndBroadcastCounts()
        {
            foreach (PlayerObject player in _players)
                _tileCounts[player.PlayerId] = _tileGrid.CountTilesForPlayer(player.PlayerId);

            GameRoomManager.Instance.RpcMinigameMessage("counts", BuildCountsPayload(), StationIndex);
        }

        // Updates _nameMap's existing entries in place (never reassigns the
        // dictionary, and never touches _colorMap) and pushes the corrected
        // names to the HUD's rows without recreating them. See StartRound().
        private void ApplyNameRefresh(string payload)
        {
            if (string.IsNullOrEmpty(payload)) return;
            var updated = new Dictionary<int, string>();
            foreach (string entry in payload.Split('|'))
            {
                string[] p = entry.Split(',');
                if (p.Length < 2) continue;
                if (!int.TryParse(p[0], out int id)) continue;
                _nameMap[id] = p[1];
                updated[id] = p[1];
            }
            _hud?.RefreshNames(updated);
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

        // "id,name|id,name|..." — deliberately lighter than BuildColorsPayload
        // (no color fields) since a refresh only ever needs to correct names.
        private string BuildNamesPayload()
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

     
            var results = new List<RoundResult>();
            int prevCount = -1;
            int prevStanding = 1;

            for (int i = 0; i < sorted.Count; i++)
            {
                PlayerObject player = sorted[i];
                int count = _tileCounts[player.PlayerId];
                int standing = (count == prevCount) ? prevStanding : i + 1;
                int points = CalculatePlacementPoints(standing, sorted.Count);

                results.Add(new RoundResult(player, standing, points, GetResultLabel(standing)));

                prevCount = count;
                prevStanding = standing;
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
            GameRoomManager.Instance.RpcMinigameMessage(messageType, payload, StationIndex);
        }

    }
}
