// JinxedScoreRow.cs
// One row in the Jinxed HUD score panel.
// Shows player name, Survivor/Jinxed/Eliminated status, and total score.

using UnityEngine;
using TMPro;

namespace ChaosPit.Minigames.Jinxed
{
    public class JinxedScoreRow : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────
        [SerializeField] private TextMeshProUGUI _nameText;
        [SerializeField] private TextMeshProUGUI _scoreText;
        [SerializeField] private TextMeshProUGUI _statusText;

        [Header("Status Colors")]
        [SerializeField] private Color _colorSurvivor = new Color(0.3f, 1f, 0.4f);
        [SerializeField] private Color _colorJinxed = new Color(0.7f, 0.3f, 1f);
        [SerializeField] private Color _colorEliminated = new Color(0.6f, 0.6f, 0.6f);

        // ── Public API ────────────────────────────────────────────

        public void Init(string playerName, int score = 0)
        {
            if (_nameText != null) _nameText.text = playerName;
            if (_scoreText != null) _scoreText.text = score.ToString();
            SetStatus(JinxedPlayerState.Survivor);
        }

        public void SetStatus(JinxedPlayerState state)
        {
            if (_statusText == null) return;

            switch (state)
            {
                case JinxedPlayerState.Survivor:
                    _statusText.text = "ALIVE";
                    _statusText.color = _colorSurvivor;
                    break;
                case JinxedPlayerState.Jinxed:
                    _statusText.text = "JINXED";
                    _statusText.color = _colorJinxed;
                    break;
                case JinxedPlayerState.Eliminated:
                    _statusText.text = "OUT";
                    _statusText.color = _colorEliminated;
                    break;
            }
        }

        public void UpdateScore(int score)
        {
            if (_scoreText != null) _scoreText.text = score.ToString();
        }

        public void ResetForNewRound()
        {
            SetStatus(JinxedPlayerState.Survivor);
        }
    }
}