// TemplateScoreRow.cs
// TEMPLATE — Rename class and namespace to match your minigame.
// Attach to a prefab with TMP children for name and score display.
// Extend Init() and SetScore() if your game needs more columns (e.g. color swatch, kills, time).

using UnityEngine;
using TMPro;

namespace ChaosPit.Minigames.Template
{
    /// <summary>
    /// One row in the TemplateHUD score panel.
    /// Shows player name and current score value.
    ///
    /// TODO: add a color swatch Image if your game assigns player colors
    /// TODO: rename SetScore() to match your game's primary metric (e.g. SetTileCount, SetKills)
    /// </summary>
    public class TemplateScoreRow : MonoBehaviour
    {
        [SerializeField] private TextMeshProUGUI _nameText;
        [SerializeField] private TextMeshProUGUI _scoreText;

        public void Init(string playerName)
        {
            if (_nameText  != null) _nameText.text  = playerName;
            if (_scoreText != null) _scoreText.text = "0";
        }

        public void SetScore(int score)
        {
            if (_scoreText != null) _scoreText.text = score.ToString();
        }
    }
}
