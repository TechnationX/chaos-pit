// ThiefsMarketController.cs
// Server-authoritative controller for the Thief's Market minigame.
//
// Round flow:
//   - Item pool spawns at a fresh procedurally-generated layout each round
//     (mostly scattered singles, a few clusters — see GenerateItemLayout).
//   - Players auto-pick-up items on trigger overlap (client detects locally,
//     server validates and confirms — see ThiefsMarketItemVisual).
//   - Players punch each other (crosshair + click, short cooldown) to force
//     1-3 items out of a target's held stack. Dropped items scatter near the
//     victim and are unpickable for a short delay, then anyone can grab them.
//   - Round ends on timer. Highest held count wins the round (ties share the
//     win). 2-3 rounds per game.
//   - Final standing: round wins, then total items across rounds, then
//     steals, then fewest times stunned. Remaining ties share a standing.

using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using FishNet.Connection;
using FishNet.Component.Transforming;

namespace ChaosPit.Minigames.ThiefsMarket
{
    public class ThiefsMarketController : MiniGameController
    {
        // ── Inspector ─────────────────────────────────────────────

        [Header("Round Settings")]
        [SerializeField] private int _roundCount = 3;
        [SerializeField] private float _roundDuration = 75f;
        [SerializeField] private int[] _placementPoints = { 10, 7, 5, 3, 1, 0 };

        [Header("Item Pool")]
        [SerializeField] private Transform _itemPoolContainer;

        [Header("Procedural Item Layout")]
        // Rectangular spawn bounds in world space (X/Z), fixed Y. Fresh layout
        // generated every round from a shared seed — see GenerateItemLayout.
        [SerializeField] private Vector2 _spawnAreaMin = new Vector2(-10f, -10f);
        [SerializeField] private Vector2 _spawnAreaMax = new Vector2(10f, 10f);
        [SerializeField] private float _spawnGroundY = 0f;
        [SerializeField] private float _minItemSpacing = 1.5f;
        [SerializeField] private int _minClusters = 1;
        [SerializeField] private int _maxClusters = 3;
        [SerializeField] private int _minClusterSize = 3;
        [SerializeField] private int _maxClusterSize = 6;
        [SerializeField] private float _clusterRadius = 2.5f;
        [SerializeField] private float _minClusterSpacing = 4f;
        [SerializeField] private int _maxPlacementAttempts = 30;

        // Broadcast once per game so every client can deterministically
        // regenerate the identical layout each round without transmitting
        // per-item positions — same principle as ArenaGrid's fall order in
        // Jinxed. Combined with round number for a per-round seed.
        private int _gameSeed;

        [Header("Punch Settings")]
        // Check distance and cooldown now live on InteractionManager
        // (single source of truth, same prefab server + client) — only the
        // network-latency tolerance margin stays here, since that's a
        // server-networking concern, not a gameplay-feel knob.
        [SerializeField] private float _punchLatencyBuffer = 0.5f;
        [SerializeField] private int _minStolenItems = 1;
        [SerializeField] private int _maxStolenItems = 3;
        [SerializeField] private float _dropPickupDelay = 1.5f;
        [SerializeField] private float _dropScatterRadius = 1.2f;
        [SerializeField] private float _stunDuration = 1f;

        [Header("Results UI")]
        [SerializeField] private TMPro.TextMeshProUGUI _resultsText;
        [SerializeField] private TMPro.TextMeshProUGUI _countdownText;

        // ── Runtime State ─────────────────────────────────────────

        private enum ItemState { Available, Held, Dropped }

        private class ItemData
        {
            public int ItemId;
            public ItemState State;
            public int HolderPlayerId = -1;
            public Vector3 Position;
            public float PickupEligibleAt;
            public int PointValue;
        }

        private Dictionary<int, ItemData> _items = new Dictionary<int, ItemData>();
        private Dictionary<int, ThiefsMarketItemVisual> _itemVisualsById = new Dictionary<int, ThiefsMarketItemVisual>();
        private Dictionary<int, HashSet<int>> _heldItemsByPlayer = new Dictionary<int, HashSet<int>>();

        private Dictionary<int, int> _roundWins = new Dictionary<int, int>();
        private Dictionary<int, int> _totalItemsAcrossRounds = new Dictionary<int, int>();
        private Dictionary<int, int> _totalSteals = new Dictionary<int, int>();
        private Dictionary<int, int> _totalStuns = new Dictionary<int, int>();
        private Dictionary<int, float> _punchCooldownUntil = new Dictionary<int, float>();
        private Dictionary<int, string> _nameMap = new Dictionary<int, string>();

        private bool _roundActive = false;
        private List<RoundResult> _finalResults = new List<RoundResult>();
        private string _lastPlayersPayload = string.Empty;

        private ThiefsMarketHUD _hud;

        // ── Required Abstract Implementations ─────────────────────

        public override void StartGame(List<PlayerObject> players)
        {
            if (!FishNet.InstanceFinder.IsServerStarted) return;

            _players = new List<PlayerObject>(players);
            _currentRound = 0;
            _gameActive = true;

            _nameMap.Clear();
            _roundWins.Clear();
            _totalItemsAcrossRounds.Clear();
            _totalSteals.Clear();
            _totalStuns.Clear();
            _punchCooldownUntil.Clear();
            _heldItemsByPlayer.Clear();

            foreach (PlayerObject p in _players)
            {
                _roundWins[p.PlayerId] = 0;
                _totalItemsAcrossRounds[p.PlayerId] = 0;
                _totalSteals[p.PlayerId] = 0;
                _totalStuns[p.PlayerId] = 0;
            }

            BuildItemPool();

            _gameSeed = UnityEngine.Random.Range(int.MinValue, int.MaxValue);
            GameRoomManager.Instance.RpcMinigameMessage("tm_seed", _gameSeed.ToString());

            GameRoomManager.Instance.RpcMinigameMessage("tm_players", BuildPlayersPayload());

            StartCoroutine(StartRoundDelayed(2f));
        }

        public override void StartRound()
        {
            _currentRound++;
            _roundActive = true;

            ResetItemsForRound();
            RespawnPlayers();

            GameRoomManager.Instance.RpcMinigameMessage("tm_round_reset", _currentRound.ToString());
            GameRoomManager.Instance.RpcMinigameMessage("tm_round_start",
                $"{_currentRound},{_roundDuration.ToString(System.Globalization.CultureInfo.InvariantCulture)}");

            // Explicit per-player TargetRpc rather than client self-discovery
            // (see SetPlayerThiefsMarketPunchMode) — timing-safe here because
            // StartRound() only runs 2s after StartGameAfterLoad already sent
            // RpcUnlockPlayer to everyone, so nothing disables the component
            // out from under this afterward.
            ActivatePunchModeForAllPlayers(true);

            StartCoroutine(RoundTimerCoroutine());
        }

        // Real end-of-round logic lives in EndCurrentRound(), triggered by
        // the timer coroutine — mirrors how BombTossController leaves this
        // abstract hook empty and drives flow from its fuse coroutine instead.
        public override void EndRound() { }

        public override List<RoundResult> GetResults() => _finalResults;

        public override void CleanUp()
        {
            _items.Clear();
            _itemVisualsById.Clear();
            _heldItemsByPlayer.Clear();
            _roundWins.Clear();
            _totalItemsAcrossRounds.Clear();
            _totalSteals.Clear();
            _totalStuns.Clear();
            _punchCooldownUntil.Clear();
            _nameMap.Clear();
            _lastPlayersPayload = string.Empty;
            _finalResults.Clear();
            _roundActive = false;
            _gameActive = false;
            Debug.Log("[ThiefsMarket] CleanUp complete.");
        }

        public override void ClientInit()
        {
            _hud = FindFirstObjectByType<ThiefsMarketHUD>();

            // "tm_players" is broadcast the instant StartGame() runs on the
            // server, which can reach this client before RpcInitMinigame does
            // (no guaranteed ordering between an ObserversRpc and a TargetRpc
            // fired moments later). If that happened, ApplyPlayersPayload ran
            // with _hud still null and silently no-opped. Re-apply the cached
            // payload now that _hud is guaranteed to exist.
            if (!string.IsNullOrEmpty(_lastPlayersPayload))
                _hud?.Init(_nameMap);

            // Item visuals live in the scene identically for every client —
            // no server data needed to discover and ID them.
            if (_itemVisualsById.Count == 0)
                BuildItemPool();

            // NOTE: punch-mode activation intentionally does NOT happen here.
            // GameRoomManager.RpcUnlockPlayer runs after RpcInitMinigame and
            // calls SetInteractionEnabled(false), which would immediately
            // disable the InteractionManager component and wipe out an
            // enable-punch-mode call made this early. Activation happens on
            // "tm_round_start" instead — see OnNetworkMessage below — mirroring
            // when BombTossController activates its own interaction mode.
        }

        public override void RemovePlayer(PlayerObject player)
        {
            base.RemovePlayer(player);

            if (_heldItemsByPlayer.TryGetValue(player.PlayerId, out var itemIds))
            {
                foreach (int itemId in itemIds.ToList())
                {
                    if (_items.TryGetValue(itemId, out ItemData item))
                    {
                        item.State = ItemState.Dropped;
                        item.HolderPlayerId = -1;
                        item.Position = player.transform.position;
                        item.PickupEligibleAt = Time.time; // immediately pickable
                    }
                }
                _heldItemsByPlayer.Remove(player.PlayerId);
            }
        }

        // ── Item Pool Setup ──────────────────────────────────────────

        private void BuildItemPool()
        {
            _itemVisualsById.Clear();
            _items.Clear();

            if (_itemPoolContainer == null)
            {
                Debug.LogError("[ThiefsMarket] No item pool container assigned.");
                return;
            }

            ThiefsMarketItemVisual[] visuals =
                _itemPoolContainer.GetComponentsInChildren<ThiefsMarketItemVisual>(true);

            for (int i = 0; i < visuals.Length; i++)
            {
                visuals[i].SetId(i);
                _itemVisualsById[i] = visuals[i];
                _items[i] = new ItemData { ItemId = i, State = ItemState.Available, HolderPlayerId = -1, PointValue = visuals[i].PointValue };
            }
        }

        private void ResetItemsForRound()
        {
            foreach (var kvp in _items)
            {
                kvp.Value.State = ItemState.Available;
                kvp.Value.HolderPlayerId = -1;
                kvp.Value.PickupEligibleAt = 0f;
            }
            _heldItemsByPlayer.Clear();
        }

        // ── Round Flow ─────────────────────────────────────────────

        private IEnumerator StartRoundDelayed(float delay)
        {
            yield return new WaitForSeconds(delay);
            StartRound();
        }

        private void RespawnPlayers()
        {
            if (_spawnPoints == null || _spawnPoints.Length == 0)
            {
                Debug.LogWarning("[ThiefsMarket] No spawn points assigned.");
                return;
            }

            for (int i = 0; i < _players.Count; i++)
            {
                PlayerObject player = _players[i];
                Transform spawn = _spawnPoints[i % _spawnPoints.Length];

                NetworkTransform nt = player.GetComponent<NetworkTransform>();
                if (nt != null) nt.Teleport();

                player.transform.position = spawn.position;
                player.transform.rotation = spawn.rotation;
                GameRoomManager.Instance.TeleportPlayer(player.Owner, spawn.position, spawn.rotation);
            }
        }

        private IEnumerator RoundTimerCoroutine()
        {
            float remaining = _roundDuration;

            while (remaining > 0f && _roundActive)
            {
                remaining -= 0.5f;
                GameRoomManager.Instance.RpcMinigameMessage("tm_timer_sync",
                    Mathf.Max(0f, remaining).ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
                yield return new WaitForSeconds(0.5f);
            }

            if (_roundActive)
                EndCurrentRound();
        }

        private void EndCurrentRound()
        {
            _roundActive = false;

            int maxCount = 0;
            var counts = new Dictionary<int, int>();

            foreach (PlayerObject p in _players)
            {
                int value = _heldItemsByPlayer.TryGetValue(p.PlayerId, out var set)
                    ? set.Sum(id => _items[id].PointValue)
                    : 0;
                counts[p.PlayerId] = value;
                _totalItemsAcrossRounds[p.PlayerId] += value;
                if (value > maxCount) maxCount = value;
            }

            var roundWinners = new List<int>();
            foreach (var kvp in counts)
            {
                // maxCount > 0 guard: don't hand out a "win" if nobody picked anything up.
                if (kvp.Value == maxCount && maxCount > 0)
                {
                    _roundWins[kvp.Key]++;
                    roundWinners.Add(kvp.Key);
                }
            }

            string countsPayload = string.Join("|", counts.Select(kvp => $"{kvp.Key},{kvp.Value}"));
            string winnersPayload = string.Join(",", roundWinners);
            GameRoomManager.Instance.RpcMinigameMessage("tm_round_result", $"{countsPayload};{winnersPayload}");

            if (_currentRound >= _roundCount)
                StartCoroutine(EndGameDelayed(2f));
            else
                StartCoroutine(StartRoundDelayed(3f));
        }

        private IEnumerator EndGameDelayed(float delay)
        {
            yield return new WaitForSeconds(delay);

            ActivatePunchModeForAllPlayers(false);

            _finalResults = BuildFinalResults();
            GameRoomManager.Instance.RpcMinigameMessage("tm_game_over", BuildFinalResultsPayload());
            GameRoomManager.Instance.NotifyGameComplete(this, _finalResults);
        }

        // ── Client Action (server receives from clients) ───────────

        public override void OnClientAction(string messageType, string payload, NetworkConnection sender)
        {
            switch (messageType)
            {
                case "tm_pickup_request":
                    HandlePickupRequest(payload, sender);
                    break;
                case "tm_punch_request":
                    HandlePunchRequest(payload, sender);
                    break;
            }
        }

        private void HandlePickupRequest(string payload, NetworkConnection sender)
        {
            if (!_roundActive) return;
            if (!int.TryParse(payload, out int itemId)) return;
            if (!_items.TryGetValue(itemId, out ItemData item)) return;

            bool eligible = item.State == ItemState.Available
                || (item.State == ItemState.Dropped && Time.time >= item.PickupEligibleAt);
            if (!eligible) return;

            // Resolve via ClientId (reliable network identifier), then use
            // PlayerId for all bookkeeping — matches _roundWins,
            // _totalItemsAcrossRounds, _nameMap, and everything else in this
            // controller, which are all keyed by PlayerId.
            PlayerObject pickerUp = FindPlayerByClientId(sender.ClientId);
            if (pickerUp == null) return;
            int playerId = pickerUp.PlayerId;

            item.State = ItemState.Held;
            item.HolderPlayerId = playerId;

            if (!_heldItemsByPlayer.TryGetValue(playerId, out var held))
            {
                held = new HashSet<int>();
                _heldItemsByPlayer[playerId] = held;
            }
            held.Add(itemId);
            int heldValue = held.Sum(id => _items[id].PointValue);

            GameRoomManager.Instance.RpcMinigameMessage("tm_pickup_confirm", $"{itemId},{playerId},{heldValue}");
        }

        private void HandlePunchRequest(string payload, NetworkConnection sender)
        {
            if (!_roundActive) return;
            if (!int.TryParse(payload, out int victimId)) return;

            int attackerId = sender.ClientId;
            if (attackerId == victimId) return;

            PlayerObject attacker = FindPlayerByClientId(attackerId);
            PlayerObject victim = FindPlayerByClientId(victimId);
            if (attacker == null || victim == null) return;

            // Distance and cooldown are read from the attacker's own
            // InteractionManager — same prefab, same Inspector-tuned values
            // server-side as client-side, no separate copy to keep in sync.
            float checkDistance = attacker.Interaction.PunchCheckDistance;
            float cooldown = attacker.Interaction.PunchCooldown;

            float now = Time.time;
            if (_punchCooldownUntil.TryGetValue(attackerId, out float readyAt) && now < readyAt) return;

            float bufferedRange = checkDistance + _punchLatencyBuffer;
            if (Vector3.Distance(attacker.transform.position, victim.transform.position) > bufferedRange) return;

            _punchCooldownUntil[attackerId] = now + cooldown;

            if (!_heldItemsByPlayer.TryGetValue(victim.PlayerId, out var victimItems) || victimItems.Count == 0)
            {
                GameRoomManager.Instance.RpcMinigameMessage("tm_punch_whiff", $"{attackerId},{victimId}");
                return;
            }

            int stolenCount = Mathf.Min(UnityEngine.Random.Range(_minStolenItems, _maxStolenItems + 1), victimItems.Count);
            List<int> stolenIds = victimItems.Take(stolenCount).ToList();

            var dropParts = new List<string>();
            var culture = System.Globalization.CultureInfo.InvariantCulture;

            foreach (int itemId in stolenIds)
            {
                victimItems.Remove(itemId);
                if (!_items.TryGetValue(itemId, out ItemData item)) continue;

                Vector2 circle = UnityEngine.Random.insideUnitCircle * _dropScatterRadius;
                Vector3 dropPos = victim.transform.position + new Vector3(circle.x, 0f, circle.y);

                item.State = ItemState.Dropped;
                item.HolderPlayerId = -1;
                item.Position = dropPos;
                item.PickupEligibleAt = now + _dropPickupDelay;

                dropParts.Add($"{itemId}:{dropPos.x.ToString(culture)}:{dropPos.y.ToString(culture)}:{dropPos.z.ToString(culture)}");
            }

            _totalSteals[attacker.PlayerId] = _totalSteals.TryGetValue(attacker.PlayerId, out int steals) ? steals + 1 : 1;
            _totalStuns[victim.PlayerId] = _totalStuns.TryGetValue(victim.PlayerId, out int stuns) ? stuns + 1 : 1;

            GameRoomManager.Instance.SetPlayerMovementLocked(victim.Owner, victim.NetworkObject, true, "tm_stunned");
            StartCoroutine(ClearStunAfterDelay(victim));

            int victimNewValue = victimItems.Sum(id => _items[id].PointValue);

            string dropPayload = string.Join(";", dropParts);
            GameRoomManager.Instance.RpcMinigameMessage("tm_stolen",
                $"{attacker.PlayerId},{victim.PlayerId},{victimNewValue},{dropPayload}");
        }

        private IEnumerator ClearStunAfterDelay(PlayerObject victim)
        {
            yield return new WaitForSeconds(_stunDuration);
            if (victim != null && victim.Owner != null)
                GameRoomManager.Instance.SetPlayerMovementLocked(victim.Owner, victim.NetworkObject, false, "tm_stunned");
        }

        // ── Network Messages (all clients receive) ─────────────────

        public override void OnNetworkMessage(string messageType, string payload)
        {
            switch (messageType)
            {
                case "tm_players":
                    ApplyPlayersPayload(payload);
                    break;

                case "tm_seed":
                    int.TryParse(payload, out _gameSeed);
                    break;

                case "tm_round_reset":
                    if (int.TryParse(payload, out int roundNum))
                        GenerateAndApplyItemLayout(roundNum);
                    _hud?.OnNetworkMessage(messageType, payload);
                    break;

                case "tm_pickup_confirm":
                    ApplyPickupConfirm(payload);
                    _hud?.OnNetworkMessage(messageType, payload);
                    break;

                case "tm_stolen":
                    ApplyStolen(payload);
                    _hud?.OnNetworkMessage(messageType, payload);
                    break;

                case "tm_round_start":
                case "tm_timer_sync":
                case "tm_punch_whiff":
                case "tm_round_result":
                    _hud?.OnNetworkMessage(messageType, payload);
                    break;

                case "tm_game_over":
                    _hud?.OnNetworkMessage(messageType, payload);
                    // Punch mode is deactivated server-side via
                    // SetPlayerThiefsMarketPunchMode in EndGameDelayed(),
                    // not here.
                    // OnShowResults (below) only ever runs on the server's own
                    // controller instance — GameRoomManager.OnGameComplete calls
                    // ShowResults() as a direct method call inside a [Server]
                    // method, it isn't an RPC. Pure clients only ever learn the
                    // game ended through this broadcast, so they build and show
                    // their own results panel here. On host, both paths run on
                    // the same object — harmless, same as Bomb Toss.
                    if (!FishNet.InstanceFinder.IsServerStarted)
                        ApplyGameOverPayload(payload);
                    break;
            }
        }

        // ── Procedural Item Layout ──────────────────────────────────

        // Runs identically on every machine (server-as-host and every remote
        // client) — same item count (from the scene), same seed (gameSeed +
        // roundNum), same deterministic algorithm, so no per-item position
        // ever needs to go over the network.
        private void GenerateAndApplyItemLayout(int roundNum)
        {
            if (_itemVisualsById.Count == 0) return;

            List<Vector3> positions = GenerateItemLayout(_itemVisualsById.Count, _gameSeed + roundNum);

            for (int i = 0; i < _itemVisualsById.Count; i++)
            {
                _itemVisualsById[i].SetPosition(positions[i]);
                _itemVisualsById[i].SetVisible(true);
            }
        }

        // Places "mostly scattered singles, a few clusters": cluster centers
        // and sizes are rolled first, cluster members placed within
        // _clusterRadius of their center, then whatever items remain are
        // scattered as singles respecting _minItemSpacing from everything
        // already placed (including cluster centers, so singles don't land
        // inside a cluster).
        private List<Vector3> GenerateItemLayout(int itemCount, int seed)
        {
            var rng = new System.Random(seed);
            var positions = new List<Vector3>(itemCount);

            int clusterCount = rng.Next(_minClusters, _maxClusters + 1);
            var clusterCenters = new List<Vector2>();
            var clusterSizes = new List<int>();

            int remaining = itemCount;
            for (int i = 0; i < clusterCount && remaining > 0; i++)
            {
                int size = Mathf.Min(remaining, rng.Next(_minClusterSize, _maxClusterSize + 1));
                clusterSizes.Add(size);
                remaining -= size;
            }

            foreach (int size in clusterSizes)
            {
                Vector2 center = FindValidPoint(rng, clusterCenters, _minClusterSpacing);
                clusterCenters.Add(center);

                for (int i = 0; i < size; i++)
                {
                    Vector2 offset = RandomInsideCircle(rng) * _clusterRadius;
                    Vector2 point = ClampToBounds(center + offset);
                    positions.Add(new Vector3(point.x, _spawnGroundY, point.y));
                }
            }

            // Everything placed so far counts as an obstacle for singles too,
            // so a scattered item can't land on top of a cluster.
            var placed = positions.Select(p => new Vector2(p.x, p.z)).ToList();
            placed.AddRange(clusterCenters);

            for (int i = 0; i < remaining; i++)
            {
                Vector2 point = FindValidPoint(rng, placed, _minItemSpacing);
                placed.Add(point);
                positions.Add(new Vector3(point.x, _spawnGroundY, point.y));
            }

            return positions;
        }

        // Tries random points within bounds up to _maxPlacementAttempts,
        // rejecting any candidate closer than minSpacing to an existing
        // point. Falls back to an unchecked random point if it can't find a
        // valid spot in time — guarantees every item in the pool gets a
        // position rather than risking an infinite loop if the spawn area is
        // too small/dense for the requested spacing.
        private Vector2 FindValidPoint(System.Random rng, List<Vector2> existing, float minSpacing)
        {
            for (int attempt = 0; attempt < _maxPlacementAttempts; attempt++)
            {
                Vector2 candidate = RandomPointInBounds(rng);

                bool valid = true;
                foreach (Vector2 p in existing)
                {
                    if (Vector2.Distance(candidate, p) < minSpacing) { valid = false; break; }
                }
                if (valid) return candidate;
            }

            return RandomPointInBounds(rng);
        }

        private Vector2 RandomPointInBounds(System.Random rng)
        {
            return new Vector2(
                (float)(rng.NextDouble() * (_spawnAreaMax.x - _spawnAreaMin.x) + _spawnAreaMin.x),
                (float)(rng.NextDouble() * (_spawnAreaMax.y - _spawnAreaMin.y) + _spawnAreaMin.y));
        }

        private Vector2 RandomInsideCircle(System.Random rng)
        {
            float angle = (float)(rng.NextDouble() * Mathf.PI * 2.0);
            float radius = Mathf.Sqrt((float)rng.NextDouble()); // sqrt for uniform area distribution, not just radius
            return new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
        }

        private Vector2 ClampToBounds(Vector2 point)
        {
            return new Vector2(
                Mathf.Clamp(point.x, _spawnAreaMin.x, _spawnAreaMax.x),
                Mathf.Clamp(point.y, _spawnAreaMin.y, _spawnAreaMax.y));
        }

        private void ApplyPickupConfirm(string payload)
        {
            string[] p = payload.Split(',');
            if (p.Length < 3) return;
            if (!int.TryParse(p[0], out int itemId)) return;

            if (_itemVisualsById.TryGetValue(itemId, out var visual))
                visual.SetVisible(false);
        }

        private void ApplyStolen(string payload)
        {
            // attackerId,victimId,victimNewCount,dropPayload
            string[] p = payload.Split(new[] { ',' }, 4);
            if (p.Length < 4 || string.IsNullOrEmpty(p[3])) return;

            var culture = System.Globalization.CultureInfo.InvariantCulture;

            foreach (string entry in p[3].Split(';'))
            {
                if (string.IsNullOrEmpty(entry)) continue;
                string[] fields = entry.Split(':');
                if (fields.Length < 4) continue;
                if (!int.TryParse(fields[0], out int itemId)) continue;
                if (!float.TryParse(fields[1], System.Globalization.NumberStyles.Float, culture, out float x)) continue;
                if (!float.TryParse(fields[2], System.Globalization.NumberStyles.Float, culture, out float y)) continue;
                if (!float.TryParse(fields[3], System.Globalization.NumberStyles.Float, culture, out float z)) continue;

                if (_itemVisualsById.TryGetValue(itemId, out var visual))
                {
                    visual.SetPosition(new Vector3(x, y, z));
                    visual.SetVisible(true);
                }
            }
        }

        // ── Results Screen ─────────────────────────────────────────

        // Server-authoritative path — only ever runs where GameRoomManager's
        // [Server]-tagged OnGameComplete executes (the server/host instance).
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

            _hud?.SetScorePanelVisible(false);
            StartCoroutine(ResultsCountdownCoroutine(_countdownText, notifyDismissal: true,
                onComplete: () => _hud?.SetScorePanelVisible(true)));
        }

        // Pure-client broadcast path — mirrors BombTossController's
        // ApplyGameOverPayload / ClientResultsCountdownCoroutine. Does NOT
        // call OnResultsDismissed — only the server-authoritative path above
        // is allowed to trigger the return-to-lobby flow.
        private void ApplyGameOverPayload(string payload)
        {
            if (string.IsNullOrEmpty(payload)) return;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("RESULTS");

            foreach (string entry in payload.Split('|'))
            {
                // standing,id,roundWins,totalItems,steals,stuns
                string[] p = entry.Split(',');
                if (p.Length < 6) continue;
                if (!int.TryParse(p[0], out int standing)) continue;
                if (!int.TryParse(p[1], out int id)) continue;

                string name = _nameMap.TryGetValue(id, out string n) ? n : $"Player_{id}";
                int points = (standing - 1) < _placementPoints.Length ? _placementPoints[standing - 1] : 0;
                sb.AppendLine($"{GetResultLabel(standing)}: {name}  +{points}pts");
            }

            if (_resultsScreenPanel != null) _resultsScreenPanel.SetActive(true);
            if (_resultsText != null) _resultsText.text = sb.ToString();
            _hud?.SetScorePanelVisible(false);
            StartCoroutine(ResultsCountdownCoroutine(_countdownText, notifyDismissal: false,
                onComplete: () => _hud?.SetScorePanelVisible(true)));
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

        private List<RoundResult> BuildFinalResults()
        {
            List<int> ranked = _players.Select(p => p.PlayerId).ToList();

            ranked.Sort((a, b) =>
            {
                int cmp = _roundWins[b].CompareTo(_roundWins[a]);
                if (cmp != 0) return cmp;

                cmp = _totalItemsAcrossRounds[b].CompareTo(_totalItemsAcrossRounds[a]);
                if (cmp != 0) return cmp;

                cmp = _totalSteals[b].CompareTo(_totalSteals[a]);
                if (cmp != 0) return cmp;

                return _totalStuns[a].CompareTo(_totalStuns[b]); // fewer stuns wins the tie
            });

            var results = new List<RoundResult>();
            int standing = 1;

            for (int i = 0; i < ranked.Count; i++)
            {
                if (i > 0 && IsTied(ranked[i], ranked[i - 1]))
                {
                    // shared standing — keep previous standing number
                }
                else
                {
                    standing = i + 1;
                }

                PlayerObject player = FindPlayerById(ranked[i]);
                if (player == null) continue;

                int points = (standing - 1) < _placementPoints.Length ? _placementPoints[standing - 1] : 0;
                results.Add(new RoundResult(player, standing, points, GetResultLabel(standing)));
            }

            return results;
        }

        private bool IsTied(int a, int b)
        {
            return _roundWins[a] == _roundWins[b]
                && _totalItemsAcrossRounds[a] == _totalItemsAcrossRounds[b]
                && _totalSteals[a] == _totalSteals[b]
                && _totalStuns[a] == _totalStuns[b];
        }

        private string BuildFinalResultsPayload()
        {
            var parts = new List<string>();
            foreach (RoundResult r in _finalResults)
            {
                int id = r.Player.PlayerId;
                parts.Add($"{r.Standing},{id},{_roundWins[id]},{_totalItemsAcrossRounds[id]},{_totalSteals[id]},{_totalStuns[id]}");
            }
            return string.Join("|", parts);
        }

        // ── Payload Parsers ────────────────────────────────────────

        private void ApplyPlayersPayload(string payload)
        {
            if (string.IsNullOrEmpty(payload)) return;
            _lastPlayersPayload = payload;
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

        // ── Helpers ────────────────────────────────────────────────

        private PlayerObject FindPlayerById(int id)
        {
            foreach (PlayerObject p in _players)
                if (p.PlayerId == id) return p;
            return null;
        }

        // HandlePunchRequest resolves attacker/victim from sender.ClientId and
        // the ClientId sent in the payload — both are real network ClientIds,
        // not PlayerId. FindPlayerById compares against PlayerId instead, which
        // isn't guaranteed to equal ClientId for every player (it happened to
        // match for one player by coincidence, not for the host). This looks
        // players up by the field that's actually being compared.
        private PlayerObject FindPlayerByClientId(int clientId)
        {
            foreach (PlayerObject p in _players)
                if (p.Owner != null && p.Owner.ClientId == clientId) return p;
            return null;
        }

        private void ActivatePunchModeForAllPlayers(bool active)
        {
            foreach (PlayerObject p in _players)
                GameRoomManager.Instance.SetPlayerThiefsMarketPunchMode(p.Owner, p.NetworkObject, active);
        }
    }
}