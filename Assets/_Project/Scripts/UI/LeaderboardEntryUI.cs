// LeaderboardEntryUI.cs

using UnityEngine;
using TMPro;

public class LeaderboardEntryUI : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI _standingLabel;
    [SerializeField] private TextMeshProUGUI _nameLabel;
    [SerializeField] private TextMeshProUGUI _primaryValueLabel;
    [SerializeField] private TextMeshProUGUI _secondaryValueLabel;

    public void SetData(int standing, string displayName, string primaryValue, string secondaryValue)
    {
        if (_standingLabel != null) _standingLabel.text = $"{standing}.";
        if (_nameLabel != null) _nameLabel.text = displayName;
        if (_primaryValueLabel != null) _primaryValueLabel.text = primaryValue;

        if (_secondaryValueLabel != null)
        {
            _secondaryValueLabel.text = secondaryValue;
            _secondaryValueLabel.gameObject.SetActive(!string.IsNullOrEmpty(secondaryValue));
        }
    }
}