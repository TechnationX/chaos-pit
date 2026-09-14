// GameRoomConsole.cs

using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

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
    [SerializeField] private Image _previewImage; // selected minigame's thumbnail — same source as the exterior kiosk's GameImage
    [SerializeField] private Sprite _privateModeSprite; // shown instead of a minigame thumbnail while in Private mode
    [SerializeField] private TextMeshProUGUI _startButtonText; // engraved label on the physical Start/Lock/Unlock button

    [Header("UI — Player List")]
    [SerializeField] private Transform _entryContainer;
    [SerializeField] private TextMeshProUGUI _entryPrefab;
    [SerializeField] private KickButton _kickButtonPrefab; // spawned only next to non-host rows

    private const int MaxEntries = 6;

    private string _selectedGameId = string.Empty;
    private MiniGameRegistry _registry;

    public bool IsPrivateMode { get; private set; } = true;
    public bool IsLocked { get; private set; }

    /// Called by MinigameStation.UpdateSessionState with the same synced
    /// payload it already receives — no separate networking for the console.
    public void Refresh(GameRoomState state, List<string> playerNames, List<int> clientIds,
        string selectedGameId, MiniGameRegistry registry, bool isPrivateMode, bool isLocked)
    {
        _selectedGameId = selectedGameId;
        _registry = registry;
        IsPrivateMode = isPrivateMode;
        IsLocked = isLocked;

        if (_statusText != null)
        {
            _statusText.text = state switch
            {
                GameRoomState.Waiting => isLocked ? "Room locked — private" : $"{playerNames.Count} player(s) in room",
                GameRoomState.Countdown => "Starting...",
                _ => string.Empty
            };
        }

        MiniGameRegistryEntry entry = !isPrivateMode && !string.IsNullOrEmpty(selectedGameId) && registry != null
            ? registry.GetById(selectedGameId)
            : null;

        if (_selectedGameText != null)
        {
            if (isPrivateMode)
                _selectedGameText.text = isLocked ? "Mode: Private (Locked)" : "Mode: Private";
            else
                _selectedGameText.text = entry != null ? $"Selected: {entry.MiniGameName}" : "Selected: None";
        }

        if (_previewImage != null)
        {
            Sprite sprite = isPrivateMode ? _privateModeSprite : entry?.Thumbnail;
            _previewImage.sprite = sprite;
            _previewImage.enabled = sprite != null;
        }

        if (_countdownText != null)
            _countdownText.gameObject.SetActive(state == GameRoomState.Countdown);

        // Matches ConsoleButton.GetStartLabel()'s wording so the engraved
        // button text and the "press E to..." interaction prompt never say
        // different things for the same state.
        if (_startButtonText != null)
            _startButtonText.text = isPrivateMode ? (isLocked ? "Unlock Room" : "Lock Room") : "Start Game";

        BuildEntries(playerNames, clientIds);
    }

    private void BuildEntries(List<string> playerNames, List<int> clientIds)
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

            // Kick button only ever shows up next to a non-host name.
            if (!isHost && _kickButtonPrefab != null && i < clientIds.Count)
            {
                KickButton kickBtn = Instantiate(_kickButtonPrefab, entry.transform);
                kickBtn.Setup(_stationIndex, clientIds[i]);
            }
        }
    }

    /// Called by MinigameStation.UpdateCountdown (forwarded from GameRoomManager's countdown RPC).
    public void UpdateCountdown(int secondsRemaining)
    {
        if (_countdownText == null) return;
        _countdownText.gameObject.SetActive(true);
        _countdownText.text = $"Starting in {secondsRemaining}...";
    }

    /// Called by ConsoleButton's SelectGame action — computes which mode a
    /// host-only cycle press should request next. The cycle is
    /// [Private, game0, game1, ...] and wraps around. Always returns a valid
    /// id (falls back to Private if the registry is empty/missing). The
    /// server re-validates host authority and lock state regardless — this
    /// only decides what to ask for.
    public string GetNextGameId()
    {
        if (_registry == null) return GameRoomManager.PrivateModeId;

        List<MiniGameRegistryEntry> entries = _registry.GetActiveEntries();
        if (entries.Count == 0) return GameRoomManager.PrivateModeId;

        int currentIndex = IsPrivateMode
            ? 0
            : Mathf.Max(0, entries.FindIndex(e => e.MiniGameId == _selectedGameId) + 1);

        int nextIndex = (currentIndex + 1) % (entries.Count + 1);
        return nextIndex == 0 ? GameRoomManager.PrivateModeId : entries[nextIndex - 1].MiniGameId;
    }
}
