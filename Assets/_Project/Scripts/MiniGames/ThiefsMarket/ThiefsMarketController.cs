// ThiefsMarketController.cs
// Server-authoritative controller for the Thief's Market minigame.
//
// Round flow:
//   - Fixed item pool spawns available at scene-authored positions.
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

        [Header("Punch Settings")]
        [SerializeField] private float _punchRange = 2.5f;
        [SerializeField] private float _punchLatencyBuffer = 0.5f;
        [SerializeField] private float _punchCooldown = 1.5f;
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
            public Vector3 Position;       // meaningful only when Dropped
            public float PickupEligibleAt; // meaningful only when Dropped
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

            GameRoomManager.Instance.RpcMinigameMessage("tm_players", BuildPlayersPayload());

            StartCoroutine(StartRoundDelayed(2f));
        }

        public override void StartRound()
        {
            _currentRound++;
            _roundActive = true;

            ResetItemsForRound();
            RespawnPlayers();

            GameRoomManager.Instance.RpcMinigameMessage("tm_round_reset", string.Empty);
            GameRoomManager.Instance.RpcMinigameMessage("tm_round_start",
                $"{_currentRound},{_roundDuration.ToString(System.Globalization.CultureInfo.InvariantCulture)}");

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
            _finalResults.Clear();
            _roundActive = false;
            _gameActive = false;
            Debug.Log("[ThiefsMarket] CleanUp complete.");
        }

        public override void ClientInit()
        {
            _hud = FindFirstObjectByType<ThiefsMarketHUD>();

            // Item visuals live in the scene identically for every client —
            // no server data needed to discover and ID them.
            if (_itemVisualsById.Count == 0)
                BuildItemPool();

            // Punch is active for every player the whole round (unlike Bomb
            // Toss's single-holder pass mode), so it's turned on locally as
            // soon as this client's minigame scene is ready rather than
            // toggled per-player via RPC.
            SetLocalPunchModeActive(true);
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
                _items[i] = new ItemData { ItemId = i, State = ItemState.Available, HolderPlayerId = -1 };
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
                int count = _heldItemsByPlayer.TryGetValue(p.PlayerId, out var set) ? set.Count : 0;
                counts[p.PlayerId] = count;
                _totalItemsAcrossRounds[p.PlayerId] += count;
                if (count > maxCount) maxCount = count;
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

            int playerId = sender.ClientId;

            item.State = ItemState.Held;
            item.HolderPlayerId = playerId;

            if (!_heldItemsByPlayer.TryGetValue(playerId, out var held))
            {
                held = new HashSet<int>();
                _heldItemsByPlayer[playerId] = held;
            }
            held.Add(itemId);

            GameRoomManager.Instance.RpcMinigameMessage("tm_pickup_confirm", $"{itemId},{playerId},{held.Count}");
        }

        private void HandlePunchRequest(string payload, NetworkConnection sender)
        {
            if (!_roundActive) return;
            if (!int.TryParse(payload, out int victimId)) return;

            int attackerId = sender.ClientId;
            if (attackerId == victimId) return;

            float now = Time.time;
            if (_punchCooldownUntil.TryGetValue(attackerId, out float readyAt) && now < readyAt) return;

            PlayerObject attacker = FindPlayerById(attackerId);
            PlayerObject victim = FindPlayerById(victimId);
            if (attacker == null || victim == null) return;

            float bufferedRange = _punchRange + _punchLatencyBuffer;
            if (Vector3.Distance(attacker.transform.position, victim.transform.position) > bufferedRange) return;

            _punchCooldownUntil[attackerId] = now + _punchCooldown;

            if (!_heldItemsByPlayer.TryGetValue(victimId, out var victimItems) || victimItems.Count == 0)
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

            _totalSteals[attackerId] = _totalSteals.TryGetValue(attackerId, out int steals) ? steals + 1 : 1;
            _totalStuns[victimId] = _totalStuns.TryGetValue(victimId, out int stuns) ? stuns + 1 : 1;

            GameRoomManager.Instance.SetPlayerMovementLocked(victim.Owner, true, "tm_stunned");
            StartCoroutine(ClearStunAfterDelay(victim));

            string dropPayload = string.Join(";", dropParts);
            GameRoomManager.Instance.RpcMinigameMessage("tm_stolen",
                $"{attackerId},{victimId},{victimItems.Count},{dropPayload}");
        }

        private IEnumerator ClearStunAfterDelay(PlayerObject victim)
        {
            yield return new WaitForSeconds(_stunDuration);
            if (victim != null && victim.Owner != null)
                GameRoomManager.Instance.SetPlayerMovementLocked(victim.Owner, false, "tm_stunned");
        }

        // ── Network Messages (all clients receive) ─────────────────

        public override void OnNetworkMessage(string messageType, string payload)
        {
            switch (messageType)
            {
                case "tm_players":
                    ApplyPlayersPayload(payload);
                    break;

                case "tm_round_reset":
                    ResetAllVisuals();
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
                    SetLocalPunchModeActive(false);
                    _hud?.OnNetworkMessage(messageType, payload);
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

        private void ResetAllVisuals()
        {
            foreach (var visual in _itemVisualsById.Values)
            {
                visual.ResetToOriginal();
                visual.SetVisible(true);
            }
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

            StartCoroutine(ResultsCountdownCoroutine(_countdownText, notifyDismissal: true));
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
                if (!int.TryParse(p[2], out int roundWins)) continue;

                string name = _nameMap.TryGetValue(id, out string n) ? n : $"Player_{id}";
                sb.AppendLine($"#{standing} {name} — {roundWins} round win(s)");
            }

            if (_resultsScreenPanel != null) _resultsScreenPanel.SetActive(true);
            if (_resultsText != null) _resultsText.text = sb.ToString();
            StartCoroutine(ResultsCountdownCoroutine(_countdownText, notifyDismissal: false));
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

        private void SetLocalPunchModeActive(bool active)
        {
            PlayerObject local = FindObjectsByType<PlayerObject>(
                FindObjectsInactive.Include, FindObjectsSortMode.None)
                .FirstOrDefault(p => p.IsOwner);

            if (local == null) return;

            var interaction = local.GetComponent<InteractionManager>();
            interaction?.SetThiefsMarketPunchActive(active, _hud);
        }
    }
}