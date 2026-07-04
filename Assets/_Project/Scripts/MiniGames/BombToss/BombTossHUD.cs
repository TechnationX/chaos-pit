// BombTossHUD.cs
// Client-side HUD for Bomb Toss.
// Receives network messages routed from BombTossController.OnNetworkMessage.

using System.Collections.Generic;
using UnityEngine;
using TMPro;

namespace ChaosPit.Minigames.BombToss
{
    public class BombTossHUD : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────

        [Header("HUD Elements")]
        [SerializeField] private TextMeshProUGUI _roundText;
        [SerializeField] private TextMeshProUGUI _fuseTimerText;
        [SerializeField] private TextMeshProUGUI _holderNameText;
        [SerializeField] private TextMeshProUGUI _passPromptText;
        [SerializeField] private TextMeshProUGUI _eliminationFeedText;

        [Header("Score Rows")]
        [SerializeField] private Transform _scoreRowContainer;
        [SerializeField] private GameObject _scoreRowPrefab;

        [SerializeField] private BombVisualManager _bombVisual;

        // ── Runtime State ─────────────────────────────────────────

        private Dictionary<int, string> _nameMap = new Dictionary<int, string>();
        private Dictionary<int, BombTossScoreRow> _scoreRows = new Dictionary<int, BombTossScoreRow>();
        private int _localPlayerId = -1;

        // ── Init ──────────────────────────────────────────────────

        public void Init(Dictionary<int, string> nameMap)
        {
            _nameMap = nameMap;
            _localPlayerId = GetLocalPlayerId();

            // Clear any existing rows
            foreach (Transform child in _scoreRowContainer)
                Destroy(child.gameObject);
            _scoreRows.Clear();

            foreach (var kvp in nameMap)
            {
                var row = Instantiate(_scoreRowPrefab, _scoreRowContainer)
                    .GetComponent<BombTossScoreRow>();
                row.Init(kvp.Key, kvp.Value);
                _scoreRows[kvp.Key] = row;
            }

            SetPassPrompt(false);
        }

        // ── Message Routing ───────────────────────────────────────

        public void OnNetworkMessage(string messageType, string payload)
        {
            switch (messageType)
            {
                case "bt_round_start":
                    ApplyRoundStart(payload);
                    break;
                case "bt_fuse_sync":
                    ApplyFuseSync(payload);
                    break;
                case "bt_holder_changed":
                    if (int.TryParse(payload, out int newHolder))
                        SetHolder(newHolder);
                    break;
                case "bt_pass_failed":
                    ShowFeed("No one in range!");
                    break;
                case "bt_player_eliminated":
                    ApplyElimination(payload);
                    break;
                case "bt_game_over":
                    ApplyGameOver(payload);
                    break;
            }
        }

        // ── Payload Handlers ──────────────────────────────────────

        private void ApplyRoundStart(string payload)
        {
            // Format: holderId,fuseTime,currentRound,roundCount
            string[] p = payload.Split(',');
            if (p.Length < 4) return;

            if (int.TryParse(p[0], out int holderId))
                SetHolder(holderId);

            if (int.TryParse(p[2], out int round))
                if (_roundText != null) _roundText.text = $"Round {round}";
        }

        private void ApplyFuseSync(string payload)
        {
            // Format: holderId,timeRemaining
            string[] p = payload.Split(',');
            if (p.Length < 2) return;

            if (float.TryParse(p[1],
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out float t))
            {
                if (_fuseTimerText != null) _fuseTimerText.text = $"{t:F0}s";
            }
        }

        private void SetHolder(int playerId)
        {
            if (_holderNameText == null) return;

            bool isLocal = playerId == _localPlayerId;
            string name = _nameMap.TryGetValue(playerId, out string n) ? n : $"Player_{playerId}";

            _holderNameText.text = isLocal ? "YOU HAVE THE BOMB!" : $"{name} has the bomb";
            _holderNameText.color = isLocal ? Color.red : Color.white;
            _bombVisual?.AttachToHolder(playerId);
        }

        private void ApplyElimination(string payload)
        {
            // Format: eliminatedId,points,scoresPayload
            string[] p = payload.Split(',', 3);
            if (p.Length < 1) return;
            if (!int.TryParse(p[0], out int elimId)) return;

            string name = _nameMap.TryGetValue(elimId, out string n) ? n : $"Player_{elimId}";
            ShowFeed($"{name} exploded!");

            if (_scoreRows.TryGetValue(elimId, out var row))
                row.SetEliminated();

            _bombVisual?.Hide();

            if (p.Length >= 3)
                ApplyScorePayload(p[2]);
        }

        private void ApplyGameOver(string payload)
        {
            ApplyScorePayload(payload);
            _bombVisual?.Hide();
        }

        private void ApplyScorePayload(string payload)
        {
            if (string.IsNullOrEmpty(payload)) return;
            foreach (string entry in payload.Split('|'))
            {
                string[] p = entry.Split(',');
                if (p.Length < 2) continue;
                if (!int.TryParse(p[0], out int id)) continue;
                if (!int.TryParse(p[1], out int score)) continue;
                if (_scoreRows.TryGetValue(id, out var row))
                    row.UpdateScore(score);
            }
        }

        // ── Pass Prompt ───────────────────────────────────────────

        public void SetPassPrompt(bool visible, string targetName = "")
        {
            if (_passPromptText == null) return;
            _passPromptText.gameObject.SetActive(visible);
            if (visible) _passPromptText.text = $"[E] Pass to {targetName}";
        }

        // ── Feed ──────────────────────────────────────────────────

        private void ShowFeed(string message)
        {
            if (_eliminationFeedText != null)
                _eliminationFeedText.text = message;
        }

        // ── Helpers ───────────────────────────────────────────────

        private int GetLocalPlayerId()
        {
            foreach (var p in FindObjectsByType<PlayerObject>(
                FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (p.IsOwner) return p.PlayerId;
            return -1;
        }
    }
}