// SessionScorePage.cs

using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

public class SessionScorePage : LeaderboardPage
{
    [Header("UI")]
    [SerializeField] private TextMeshProUGUI _titleLabel;
    [SerializeField] private Transform _entryContainer;
    [SerializeField] private LeaderboardEntryUI _entryPrefab;

    public override void Populate(int maxEntries)
    {
        if (_titleLabel != null) _titleLabel.text = "Session Scores";

        foreach (Transform child in _entryContainer)
            Destroy(child.gameObject);

        var entries = LeaderboardManager.Instance?.GetCachedSessionEntries();
        if (entries == null) return;

        foreach (var e in entries.Take(maxEntries))
        {
            LeaderboardEntryUI entry = Instantiate(_entryPrefab, _entryContainer);
            entry.SetData(e.Standing, e.DisplayName, $"{e.SessionScore} pts", string.Empty);
        }
    }
}