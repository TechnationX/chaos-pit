// BombTossScoreRow.cs
// Per-player score row for the Bomb Toss HUD.
// Tracks elimination state locally to prevent color resets on score updates.

using UnityEngine;
using TMPro;

namespace ChaosPit.Minigames.BombToss
{
    public class BombTossScoreRow : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────

        [SerializeField] private TextMeshProUGUI _nameText;
        [SerializeField] private TextMeshProUGUI _scoreText;

        [Header("Colors")]
        [SerializeField] private Color _activeColor = Color.white;
        [SerializeField] private Color _eliminatedColor = Color.gray;

        // ── Runtime State ─────────────────────────────────────────

        private bool _isEliminated = false;

        // ── Init ──────────────────────────────────────────────────

        public void Init(int clientId, string displayName)
        {
            if (_nameText != null) _nameText.text = displayName;
            if (_scoreText != null) _scoreText.text = "0";
            SetColor(_activeColor);
        }

        // ── Public API ────────────────────────────────────────────

        public void UpdateScore(int cumulativeScore)
        {
            if (_scoreText != null) _scoreText.text = cumulativeScore.ToString();

            // Do not reset color if already eliminated
            if (!_isEliminated)
                SetColor(_activeColor);
        }

        public void SetEliminated()
        {
            _isEliminated = true;
            SetColor(_eliminatedColor);
        }

        // ── Helpers ───────────────────────────────────────────────

        private void SetColor(Color color)
        {
            if (_nameText != null) _nameText.color = color;
            if (_scoreText != null) _scoreText.color = color;
        }
    }
}