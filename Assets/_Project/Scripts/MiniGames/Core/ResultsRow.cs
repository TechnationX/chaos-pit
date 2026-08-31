// ResultsRow.cs
// One row in the shared minigame results screen (see ResultsCanvas.prefab /
// ResultsScreenUI.cs). Every minigame uses the same row prefab so results
// look consistent across all games instead of each one hand-formatting its
// own text block. Standing == 1 (the winner) gets a highlighted style.

using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class ResultsRow : MonoBehaviour
{
    [Header("Refs")]
    [SerializeField] private Image _background;
    [SerializeField] private TextMeshProUGUI _resultLabelText;
    [SerializeField] private TextMeshProUGUI _nameText;
    [SerializeField] private TextMeshProUGUI _detailsText;

    [Header("Normal Style")]
    [SerializeField] private Color _normalBackground = new Color(1f, 1f, 1f, 0.08f);
    [SerializeField] private Color _normalTextColor = Color.white;

    [Header("Winner Highlight (Standing == 1)")]
    [SerializeField] private Color _winnerBackground = new Color(1f, 0.831f, 0f, 1f); // gold
    [SerializeField] private Color _winnerTextColor = new Color(0.15f, 0.1f, 0f, 1f);

    public void Init(PlayerResultEntry entry)
    {
        if (entry == null) return;

        if (_resultLabelText != null) _resultLabelText.text = entry.ResultLabel;
        if (_nameText != null) _nameText.text = entry.DisplayName;
        if (_detailsText != null) _detailsText.text = $"+{entry.PointsEarned}pts   Lv.{entry.CareerLevel}";

        SetHighlighted(entry.Standing == 1);
    }

    private void SetHighlighted(bool isWinner)
    {
        Color bg = isWinner ? _winnerBackground : _normalBackground;
        Color txt = isWinner ? _winnerTextColor : _normalTextColor;
        FontStyles style = isWinner ? FontStyles.Bold : FontStyles.Normal;

        if (_background != null) _background.color = bg;

        if (_resultLabelText != null) { _resultLabelText.color = txt; _resultLabelText.fontStyle = style; }
        if (_nameText != null) { _nameText.color = txt; _nameText.fontStyle = style; }
        if (_detailsText != null) { _detailsText.color = txt; _detailsText.fontStyle = style; }
    }
}
