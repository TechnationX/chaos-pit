// MinigameStation.cs

using FishNet.Object;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class MinigameStation : MonoBehaviour
{
    [Header("Station Settings")]
    [SerializeField] private int _stationIndex;
    [SerializeField] private Transform[] _waitingAreaPoints;
    [SerializeField] private MiniGameRegistry _registry;

    [Header("UI — Exterior Display")]
    [SerializeField] private TextMeshProUGUI _exteriorHeaderText;   // "DisplayText" object — static "Game Room N"
    [SerializeField] private TextMeshProUGUI _exteriorPlayingText;  // "GameText" object — "Playing: X"
    [SerializeField] private Transform _exteriorEntryContainer;     // "EntryContainer"
    [SerializeField] private TextMeshProUGUI _exteriorEntryPrefab;  // per-player name slot prefab
    [SerializeField] private Image _exteriorGameImage;              // "GameImage"
    [SerializeField] private Sprite _privateModeSprite;             // shown instead of a minigame thumbnail while in Private mode
    [SerializeField] private KioskJoinButton _kioskJoinButton;      // the station display's own Join button — greys out via CanJoin below
    [SerializeField] private TextMeshProUGUI _countdownLabel;       // "Starting in N..." — kept visible to everyone, not gated on any menu

    [Header("UI — Room Console")]
    [SerializeField] private GameRoomConsole _console; // physical console in the waiting room, fed the same synced data as the exterior display

    // State
    private List<string> _syncedPlayerNames = new List<string>();
    private GameRoomState _syncedState = GameRoomState.Idle;
    private List<int> _syncedClientIds = new List<int>();
    private string _syncedSelectedGameId = string.Empty;
    private bool _syncedIsPrivateMode = true; // matches GameRoomSession's default so a fresh room's kiosk shows "Private" before any sync arrives
    private bool _syncedIsLocked = false;

    private const int MaxExteriorEntries = 6;

    public int StationIndex => _stationIndex;

    public int _stationVal = 0;
    public Transform[] WaitingAreaPoints => _waitingAreaPoints;

    // Whether the kiosk's Join button should currently be interactable —
    // same rule the old panel's Join button used
    // (isIdle || isWaiting) && !locked — just no longer buried inside a
    // panel-only refresh. KioskJoinButton reads this to grey itself out.
    public bool CanJoin =>
        (_syncedState == GameRoomState.Idle || _syncedState == GameRoomState.Waiting) && !_syncedIsLocked;

    // ─── Lifecycle ────────────────────────────────────────────────────────────

    private void Start()
    {
        // Register with GameRoomManager
        GameRoomManager.RequestRegistration(this);
    }

    private void Awake()
    {
        _stationVal = _stationIndex + 1;

        // Hand this room's own console its station index directly, rather
        // than relying on a second manually-entered copy living on
        // GameRoomConsole itself — see GameRoomConsole.SetStationIndex.
        _console?.SetStationIndex(_stationIndex);

        _exteriorHeaderText.text = $"Game Room {_stationVal}";
        RefreshExteriorDisplay();
        _kioskJoinButton?.Refresh();
    }

    // ─── Join ─────────────────────────────────────────────────────────────────

    // Called directly by KioskJoinButton.OnInteract — the station no longer
    // opens any panel/menu, so there is nothing else for interacting with
    // this station to do.
    public void RequestJoin(PlayerObject player)
    {
        if (_syncedClientIds.Contains(player.OwnerId)) return; // already in this room
        GameRoomManager.Instance.RequestJoin(_stationIndex, player);
    }

    // Previously force-closed the join panel from the server right after a
    // successful join (see GameRoomManager's RpcForceCloseStationPanel).
    // There's no panel left to close, but the method is kept — with this
    // empty body — so that existing call site keeps compiling without
    // touching GameRoomManager.cs.
    public void ForceClosePanel() { }

    // ─── Session Sync ─────────────────────────────────────────────────────────

    // Called directly (not via RPC) by GameRoomManager on the server's own
    // station instance whenever session state changes. Previously used to
    // give the host's own panel a zero-latency refresh ahead of the RPC
    // broadcast; now that there's no panel, UpdateSessionState — driven by
    // the authoritative SyncSessionToClients RPC that reaches every client,
    // including the host — is the only path that needs to touch the kiosk's
    // visuals. Kept as a no-op stub so GameRoomManager's many existing calls
    // into every station still compile.
    public void OnSessionUpdated(GameRoomSession session) { }

    private void RefreshExteriorDisplay()
    {
        MiniGameRegistryEntry entry = !_syncedIsPrivateMode && !string.IsNullOrEmpty(_syncedSelectedGameId) && _registry != null
            ? _registry.GetById(_syncedSelectedGameId)
            : null;

        if (_syncedIsPrivateMode)
            _exteriorPlayingText.text = _syncedIsLocked ? "Playing: Private (Locked)" : "Playing: Private";
        else
            _exteriorPlayingText.text = entry != null
                ? $"Playing: {entry.MiniGameName}"
                : "Playing: Selecting Game...";

        if (_exteriorGameImage != null)
        {
            Sprite sprite = _syncedIsPrivateMode ? _privateModeSprite : entry?.Thumbnail;
            _exteriorGameImage.sprite = sprite;
            _exteriorGameImage.enabled = sprite != null;
        }

        BuildExteriorEntries();
    }

    private void BuildExteriorEntries()
    {
        foreach (Transform child in _exteriorEntryContainer)
            Destroy(child.gameObject);

        int count = Mathf.Min(_syncedPlayerNames.Count, MaxExteriorEntries);
        for (int i = 0; i < count; i++)
        {
            TextMeshProUGUI entry = Instantiate(_exteriorEntryPrefab, _exteriorEntryContainer);
            bool isHost = i == 0; // host is always first in list — same convention as BuildPlayerList()
            entry.text = isHost ? $"{_syncedPlayerNames[i]} (Host)" : _syncedPlayerNames[i];
        }
    }

    // ─── Countdown Display ────────────────────────────────────────────────────

    /// Called by GameRoomManager each countdown tick via RPC.
    public void UpdateCountdown(int secondsRemaining)
    {
        _countdownLabel.gameObject.SetActive(true);
        _countdownLabel.text = $"Starting in {secondsRemaining}...";
        _console?.UpdateCountdown(secondsRemaining);
    }

    // ─── Sync Entry Point ─────────────────────────────────────────────────────

    public void UpdateSessionState(int hostClientId, List<string> playerNames,
    List<int> clientIds, GameRoomState state, bool gameSelected, int minPlayers, string selectedGameId,
    bool isPrivateMode, bool isLocked)
    {
        _syncedPlayerNames = playerNames;
        _syncedClientIds = clientIds;
        _syncedState = state;
        _syncedSelectedGameId = selectedGameId;
        _syncedIsPrivateMode = isPrivateMode;
        _syncedIsLocked = isLocked;

        RefreshExteriorDisplay();
        _console?.Refresh(state, playerNames, clientIds, selectedGameId, _registry, isPrivateMode, isLocked);
        _kioskJoinButton?.Refresh();
    }
}
