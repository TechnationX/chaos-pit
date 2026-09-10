// GameRoomConsole.cs

using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

// Plain MonoBehaviour, not networked — same reasoning as BowlingPanelText:
// it only ever displays data MinigameStation already received over the
// network (via UpdateSessionState), it never originates state itself.
// One instance lives in each station's physical waiting room; MinigameStation
// forwards its synced session data here every time it changes.
public class GameRoomConsole : MonoBehaviour
{
    [Header("Station")]
    [SerializeField] private int _stationIndex;
    public int StationIndex => _stationIndex;

    [Header("UI — Status")]
    [SerializeField] private TextMeshProUGUI _statusText;
    [SerializeField] private TextMeshProUGUI _selectedGameText;
    [SerializeField] private TextMeshProUGUI _countdownText;

    [Header("UI — Player List")]
    [SerializeField] private Transform _entryContainer;
    [SerializeField] private TextMeshProUGUI _entryPrefab;

    private const int MaxEntries = 6;

    private string _selectedGameId = string.Empty;
    private MiniGameRegistry _registry;

    /// Called by MinigameStation.UpdateSessionState with the same synced
    /// payload it already receives — no separate networking for the console.
    public void Refresh(GameRoomState state, List<string> playerNames, string selectedGameId, MiniGameRegistry registry)
    {
        _selectedGameId = selectedGameId;
        _registry = registry;

        if (_statusText != null)
        {
            _statusText.text = state switch
            {
                GameRoomState.Waiting => $"{playerNames.Count} player(s) in room",
                GameRoomState.Countdown => "Starting...",
                _ => string.Empty
            };
        }

        MiniGameRegistryEntry entry = !string.IsNullOrEmpty(selectedGameId) && registry != null
            ? registry.GetById(selectedGameId)
            : null;

        if (_selectedGameText != null)
            _selectedGameText.text = entry != null ? $"Selected: {entry.MiniGameName}" : "Selected: None";

        if (_countdownText != null)
            _countdownText.gameObject.SetActive(state == GameRoomState.Countdown);

        BuildEntries(playerNames);
    }

    private void BuildEntries(List<string> playerNames)
    {
        if (_entryContainer == null || _entryPrefab == null) return;

        foreach (Transform child in _entryContainer)
            Destroy(child.gameObject);

        int count = Mathf.Min(playerNames.Count, MaxEntries);
        for (int i = 0; i < count; i++)
        {
            TextMeshProUGUI entry = Instantiate(_entryPrefab, _entryContainer);
            bool isHost = i == 0; // host is always first in list — same convention as MinigameStation
            entry.text = isHost ? $"{playerNames[i]} (Host)" : playerNames[i];
        }
    }

    /// Called by MinigameStation.UpdateCountdown (forwarded from GameRoomManager's countdown RPC).
    public void UpdateCountdown(int secondsRemaining)
    {
        if (_countdownText == null) return;
        _countdownText.gameObject.SetActive(true);
        _countdownText.text = $"Starting in {secondsRemaining}...";
    }

    /// Called by ConsoleButton's SelectGame action — computes which minigame
    /// a host-only cycle press should request next, wrapping around the
    /// registry's active entries. Returns null if there's nothing to select
    /// (empty/missing registry). The server re-validates host authority
    /// regardless — this only decides what to ask for.
    public string GetNextGameId()
    {
        if (_registry == null) return null;

        List<MiniGameRegistryEntry> entries = _registry.GetActiveEntries();
        if (entries.Count == 0) return null;

        int currentIndex = entries.FindIndex(e => e.MiniGameId == _selectedGameId);
        int nextIndex = (currentIndex + 1) % entries.Count;
        return entries[nextIndex].MiniGameId;
    }
}
