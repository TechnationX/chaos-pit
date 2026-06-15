// PaintScoreRow.cs
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace ChaosPit.Minigames.PaintTheTown
{
    /// <summary>
    /// One row in the PaintTheTownHUD score panel.
    /// Shows player color swatch, name, and current tile count.
    /// </summary>
    public class PaintScoreRow : MonoBehaviour
    {
        [SerializeField] private Image            _colorSwatch;
        [SerializeField] private TextMeshProUGUI  _nameText;
        [SerializeField] private TextMeshProUGUI  _countText;

        public void Init(string playerName, Color color)
        {
            if (_colorSwatch != null) _colorSwatch.color = color;
            if (_nameText    != null) _nameText.text     = playerName;
            if (_countText   != null) _countText.text    = "0";
        }

        public void SetCount(int count)
        {
            if (_countText != null) _countText.text = count.ToString();
        }
    }
}
