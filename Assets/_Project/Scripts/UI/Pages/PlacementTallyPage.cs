// PlacementTallyPage.cs

using UnityEngine;
using TMPro;

public class PlacementTallyPage : LeaderboardPage
{
    [Header("UI")]
    [SerializeField] private TextMeshProUGUI _titleLabel;
    [SerializeField] private TextMeshProUGUI _placeholderLabel;

    public override void Populate(int maxEntries)
    {
        if (_titleLabel != null)
            _titleLabel.text = "Win & Placement Tallies";

        if (_placeholderLabel != null)
            _placeholderLabel.text = "Coming Soon";

        // TODO: track wins and placement counts per player
        // Will need WinCount and PlacementCounts[] added to PlayerProfile
        // TODO: BACKEND — pull all-time win/placement data from database
    }
}