// ThiefsMarketHUD.cs
// Client-side HUD for Thief's Market.
// Receives network messages routed from ThiefsMarketController.OnNetworkMessage.

using System.Collections.Generic;
using UnityEngine;
using TMPro;
using System.Collections;

namespace ChaosPit.Minigames.ThiefsMarket
{
    public class ThiefsMarketHUD : MonoBehaviour
    {
        [Header("HUD Elements")]
        [SerializeField] private TextMeshProUGUI _roundText;
        [SerializeField] private TextMeshProUGUI _timerText;
        [SerializeField] private TextMeshProUGUI _feedText;

        [Header("Score Rows")]
        [SerializeField] private Transform _scoreRowContainer;
        [SerializeField] private GameObject _scoreRowPrefab;

        private Dictionary<int, string> _nameMap = new Dictionary<int, string>();
        private Dictionary<int, ThiefsMarketScoreRow> _scoreRows = new Dictionary<int, ThiefsMarketScoreRow>();
        private int _localPlayerId = -1;
        private Coroutine _feedClearCoroutine;

        // ── Init ──────────────────────────────────────────────────

        public void Init(Dictionary<int, string> nameMap)
        {
            _nameMap = nameMap;
            _localPlayerId = GetLocalPlayerId();

            foreach (Transform child in _scoreRowContainer)
                Destroy(child.gameObject);
            _scoreRows.Clear();

            foreach (var kvp in nameMap)
            {
                var row = Instantiate(_scoreRowPrefab, _scoreRowContainer)
                    .GetComponent<ThiefsMarketScoreRow>();
                row.Init(kvp.Key, kvp.Value);
                _scoreRows[kvp.Key] = row;
            }
        }

        // ── Message Routing ───────────────────────────────────────

        public void OnNetworkMessage(string messageType, string payload)
        {
            switch (messageType)
            {
                case "tm_round_reset":
                    foreach (var row in _scoreRows.Values)
                        row.UpdateCount(0);
                    ShowFeed(string.Empty);
                    break;

                case "tm_round_start":
                    ApplyRoundStart(payload);
                    break;

                case "tm_timer_sync":
                    ApplyTimerSync(payload);
                    break;

                case "tm_pickup_confirm":
                    ApplyPickupConfirm(payload);
                    break;

                case "tm_stolen":
                    ApplyStolen(payload);
                    break;

                case "tm_punch_whiff":
                    ApplyWhiff(payload);
                    break;

                case "tm_round_result":
                    ApplyRoundResult(payload);
                    break;

                case "tm_game_over":
                    ApplyGameOver(payload);
                    break;
            }
        }

        // ── Payload Handlers ──────────────────────────────────────

        private void ApplyRoundStart(string payload)
        {
            // Format: currentRound,roundDuration
            string[] p = payload.Split(',');
            if (p.Length < 2) return;
            if (_roundText != null) _roundText.text = $"Round {p[0]}";
        }

        private void ApplyTimerSync(string payload)
        {
            if (_timerText == null) return;
            if (float.TryParse(payload,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out float t))
            {
                _timerText.text = $"{Mathf.CeilToInt(t)}s";
            }
        }

        private void ApplyPickupConfirm(string payload)
        {
            // Format: itemId,playerId,newCount
            string[] p = payload.Split(',');
            if (p.Length < 3) return;
            if (!int.TryParse(p[1], out int playerId)) return;
            if (!int.TryParse(p[2], out int newCount)) return;

            if (_scoreRows.TryGetValue(playerId, out var row))
                row.UpdateCount(newCount);
        }

        private void ApplyStolen(string payload)
        {
            // Format: attackerId,victimId,victimNewCount,dropPayload
            string[] p = payload.Split(new[] { ',' }, 4);
            if (p.Length < 3) return;
            if (!int.TryParse(p[0], out int attackerId)) return;
            if (!int.TryParse(p[1], out int victimId)) return;
            if (!int.TryParse(p[2], out int victimNewCount)) return;

            if (_scoreRows.TryGetValue(victimId, out var victimRow))
                victimRow.UpdateCount(victimNewCount);

            string attackerName = _nameMap.TryGetValue(attackerId, out string an) ? an : $"Player_{attackerId}";
            string victimName = _nameMap.TryGetValue(victimId, out string vn) ? vn : $"Player_{victimId}";
            ShowFeed($"{attackerName} punched {victimName}!");
        }

        private void ApplyWhiff(string payload)
        {
            string[] p = payload.Split(',');
            if (p.Length < 2) return;
            if (!int.TryParse(p[0], out int attackerId)) return;
            if (attackerId != _localPlayerId) return;

            ShowFeed("Nothing to steal!");
        }

        private void ApplyRoundResult(string payload)
        {
            // Format: id,count|id,count...;winnerId,winnerId...
            string[] sections = payload.Split(';');
            if (sections.Length < 1) return;

            var winnerIds = new HashSet<int>();
            if (sections.Length >= 2 && !string.IsNullOrEmpty(sections[1]))
            {
                foreach (string id in sections[1].Split(','))
                    if (int.TryParse(id, out int wid))
                        winnerIds.Add(wid);
            }

            foreach (string entry in sections[0].Split('|'))
            {
                string[] p = entry.Split(',');
                if (p.Length < 2) continue;
                if (!int.TryParse(p[0], out int id)) continue;
                if (!int.TryParse(p[1], out int count)) continue;

                if (_scoreRows.TryGetValue(id, out var row))
                {
                    row.UpdateCount(count);
                    row.SetRoundWinner(winnerIds.Contains(id));
                }
            }
        }

        private void ApplyGameOver(string payload)
        {
            // Format: standing,id,roundWins,totalItems,steals,stuns|...
            foreach (string entry in payload.Split('|'))
            {
                // standing,id,points,roundWins,totalItems,steals,stuns
                string[] p = entry.Split(',');
                if (p.Length < 7) continue;
                if (!int.TryParse(p[0], out int standing)) continue;
                if (!int.TryParse(p[1], out int id)) continue;
                if (!int.TryParse(p[3], out int roundWins)) continue;   // index shifted 2 → 3

                if (_scoreRows.TryGetValue(id, out var row))
                    row.SetFinalStanding(standing, roundWins);
            }
        }

        // ── Feed ──────────────────────────────────────────────────

        private void ShowFeed(string message)
        {
            if (_feedText != null)
                _feedText.text = message;

            if (_feedClearCoroutine != null)
                StopCoroutine(_feedClearCoroutine);

            if (!string.IsNullOrEmpty(message))
                _feedClearCoroutine = StartCoroutine(ClearFeedAfterDelay());
        }

        private IEnumerator ClearFeedAfterDelay()
        {
            yield return new WaitForSeconds(2.5f);
            if (_feedText != null) _feedText.text = string.Empty;
            _feedClearCoroutine = null;
        }

        // ── Helpers ───────────────────────────────────────────────

        private int GetLocalPlayerId()
        {
            foreach (var p in FindObjectsByType<PlayerObject>(
                FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (p.IsOwner) return p.Owner.ClientId;
            return -1;
        }

        public void SetScorePanelVisible(bool visible)
        {
            if (_scoreRowContainer != null)
                _scoreRowContainer.gameObject.SetActive(visible);
        }
    }
}