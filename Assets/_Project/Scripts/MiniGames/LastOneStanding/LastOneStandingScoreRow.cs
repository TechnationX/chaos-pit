// LastOneStandingScoreRow.cs
// One row in the Last One Standing HUD score panel.
// Shows player name, alive/eliminated status, and survival rank when eliminated.

using UnityEngine;
using TMPro;
using UnityEngine.UI;

namespace ChaosPit.Minigames.LastOneStanding
{
    public class LastOneStandingScoreRow : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────
        [SerializeField] private TextMeshProUGUI _nameText;
        [SerializeField] private TextMeshProUGUI _statusText;
        [SerializeField] private Image           _statusIndicator;   // colored dot/icon

        [Header("Status Colors")]
        [SerializeField] private Color _colorAlive      = new Color(0.3f, 1f, 0.4f);
        [SerializeField] private Color _colorEliminated = new Color(0.6f, 0.6f, 0.6f);

        private bool _isEliminated = false;

        // ── Public API ────────────────────────────────────────────

        public void Init(string playerName)
        {
            if (_nameText != null) _nameText.text = playerName;
            if (_statusText != null) _statusText.text = "0";
            if (_statusText != null) _statusText.color = _colorAlive;
            if (_statusIndicator != null) _statusIndicator.color = _colorAlive;
        }

        public void SetAlive(int score = 0)
        {
            if (_statusText != null) _statusText.text = score.ToString();
            if (_statusText != null) _statusText.color = _colorAlive;
            if (_statusIndicator != null) _statusIndicator.color = _colorAlive;
        }

        public void SetEliminated(int score)
        {
            _isEliminated = true;
            if (_statusText != null) _statusText.text = score.ToString();
            if (_statusText != null) _statusText.color = _colorEliminated;
            if (_statusIndicator != null) _statusIndicator.color = _colorEliminated;
        }

        public void UpdateScore(int score)
        {
            if (_statusText != null) _statusText.text = score.ToString();
            // Color stays as-is — alive = green, eliminated = grey
        }

        public void ResetForNewRound()
        {
            _isEliminated = false;
            // Keep current score text, just reset color to alive
            if (_statusText != null) _statusText.color = _colorAlive;
            if (_statusIndicator != null) _statusIndicator.color = _colorAlive;
        }
    }
}
