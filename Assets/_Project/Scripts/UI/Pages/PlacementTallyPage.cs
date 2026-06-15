// PlacementTallyPage.cs

using System.Linq;
using TMPro;
using UnityEngine;

public class PlacementTallyPage : LeaderboardPage
{
    [Header("UI")]
    [SerializeField] private TextMeshProUGUI _titleLabel;
    [SerializeField] private Transform _entryContainer;
    [SerializeField] private LeaderboardEntryUI _entryPrefab;

    public override void Populate(int maxEntries)
    {
        if (_titleLabel != null) _titleLabel.text = "Win & Placement Tallies";

        foreach (Transform child in _entryContainer)
            Destroy(child.gameObject);

        var entries = LeaderboardManager.Instance?.GetCachedSessionEntries();
        if (entries == null) return;

        foreach (var e in entries.Take(maxEntries))
        {
            LeaderboardEntryUI entry = Instantiate(_entryPrefab, _entryContainer);
            entry.SetData(e.Standing, e.DisplayName, "0 wins", "0   0   0");
        }

        // TODO: replace zeroed stats with real PlayerProfile data (WinCount, PlacementCounts[])
        // Format so 1st, 2nd, 3rd are on top and just numbers are shown per output
        // TODO: BACKEND — pull all-time win/placement data from database
    }
}