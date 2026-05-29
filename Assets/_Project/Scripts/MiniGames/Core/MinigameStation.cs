// MinigameStation.cs

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class MinigameStation : MonoBehaviour, IInteractable
{
    [Header("Station Settings")]
    [SerializeField] private int _stationIndex;
    [SerializeField] private Transform[] _waitingAreaPoints;
    [SerializeField] private MiniGameRegistry _registry;

    [Header("UI — Panel")]
    [SerializeField] private GameObject _panel;

    [Header("UI — Game Selection")]
    [SerializeField] private Transform _gameButtonContainer;
    [SerializeField] private Button _gameButtonPrefab;

    [Header("UI — Player List")]
    [SerializeField] private Transform _playerListContainer;
    [SerializeField] private TextMeshProUGUI _playerListEntryPrefab;

    [Header("UI — Status")]
    [SerializeField] private TextMeshProUGUI _statusLabel;
    [SerializeField] private TextMeshProUGUI _countdownLabel;

    [Header("UI — Buttons")]
    [SerializeField] private Button _joinButton;
    [SerializeField] private Button _leaveButton;
    [SerializeField] private Button _startButton;
    [SerializeField] private Button _closeButton;

    // State
    private GameRoomSession _currentSession;
    private PlayerObject _localPlayer;
    private bool _isPanelOpen = false;
    private int _hostClientId = -1;
    private List<string> _syncedPlayerNames = new List<string>();
    private GameRoomState _syncedState = GameRoomState.Idle;
    private int _syncedPlayerCount = 0;
    private bool _syncedGameSelected = false;
    private int _syncedMinPlayers = 0;
    private List<int> _syncedClientIds = new List<int>();

    public int StationIndex => _stationIndex;
    public Transform[] WaitingAreaPoints => _waitingAreaPoints;

    // IInteractable
    public string PromptLabel => GetPromptLabel();

    // ─── Lifecycle ────────────────────────────────────────────────────────────

    private void Start()
    {
        // Register with GameRoomManager
        GameRoomManager.RequestRegistration(this);
    }

    private void Awake()
    {
        _panel.SetActive(false);

        _joinButton.onClick.AddListener(OnJoinPressed);
        _leaveButton.onClick.AddListener(OnLeavePressed);
        _startButton.onClick.AddListener(OnStartPressed);
        _closeButton.onClick.AddListener(OnClosePressed);

    }

    private void OnDestroy()
    {
        _joinButton.onClick.RemoveAllListeners();
        _leaveButton.onClick.RemoveAllListeners();
        _startButton.onClick.RemoveAllListeners();
        _closeButton.onClick.RemoveAllListeners();
    }

    // ─── IInteractable ────────────────────────────────────────────────────────

    public void OnInteract(PlayerObject player)
    {
        _localPlayer = player;
        OpenPanel();
    }

    // ─── Panel ────────────────────────────────────────────────────────────────

    private void OpenPanel()
    {
        _isPanelOpen = true;
        _panel.SetActive(true);
        RefreshUI();

        // Lock player movement while panel is open
        _localPlayer?.Movement.SetMovementLocked(true);
        _localPlayer?.Camera.ReleaseCursor();
    }

    private void OnClosePressed()
    {
        if (_localPlayer == null) return;

        bool localIsHost = _currentSession != null && _currentSession.HostPlayer == _localPlayer;
        bool isCountdown = _currentSession?.State == GameRoomState.Countdown;
        bool localInSession = IsLocalPlayerInSession();

        if (localIsHost && (isCountdown || localInSession))
        {
            if (isCountdown)
                GameRoomManager.Instance.CancelCountdown(_stationIndex);
            else
                GameRoomManager.Instance.RequestLeave(_stationIndex, _localPlayer);
        }
        else if (localInSession)
        {
            GameRoomManager.Instance.RequestLeave(_stationIndex, _localPlayer);
        }

        ClosePanel();
    }

    private void ClosePanel()
    {
        _isPanelOpen = false;
        _panel.SetActive(false);

        // Restore movement if player is not in session
        if (!IsLocalPlayerInSession())
        {
            _localPlayer?.Movement.SetMovementLocked(false);
            _localPlayer?.Camera.LockCursor();
        }

        _localPlayer = null;
    }

    // ─── Button Handlers ──────────────────────────────────────────────────────

    private void OnJoinPressed()
    {
        if (_localPlayer == null) return;
        GameRoomManager.Instance.RequestJoin(_stationIndex, _localPlayer);
        RefreshUI();
    }

    private void OnLeavePressed()
    {
        if (_localPlayer == null) return;
        GameRoomManager.Instance.RequestLeave(_stationIndex, _localPlayer);
        ClosePanel();
    }

    private void OnStartPressed()
    {
        if (_localPlayer == null) return;
        GameRoomManager.Instance.RequestStartCountdown(_stationIndex, _localPlayer);
    }

    private void OnGameSelected(string miniGameId)
    {
        GameRoomManager.Instance.SelectGame(_stationIndex, miniGameId);
    }

    // ─── UI Refresh ───────────────────────────────────────────────────────────

    /// Called by GameRoomManager whenever session state changes.
    public void OnSessionUpdated(GameRoomSession session)
    {
        _currentSession = session;

        if (session.State == GameRoomState.Loading ||
            session.State == GameRoomState.InProgress ||
            session.State == GameRoomState.Returning ||
            session.State == GameRoomState.Results)
        {
            if (_isPanelOpen)
                ClosePanel();
            return;
        }

        if (_isPanelOpen)
            RefreshUI();
    }

    private void RefreshUI()
    {
        _joinButton.gameObject.SetActive(false);
        _leaveButton.gameObject.SetActive(false);
        _startButton.gameObject.SetActive(false);
        _closeButton.gameObject.SetActive(false);

        int localClientId = _localPlayer != null ? _localPlayer.OwnerId : -1;
        bool localIsHost = localClientId != -1 && localClientId == _hostClientId;
        bool localInSession = _syncedPlayerNames.Count > 0 &&
                              IsLocalPlayerInSession();
        bool isCountdown = _syncedState == GameRoomState.Countdown;
        bool isWaiting = _syncedState == GameRoomState.Waiting;
        bool isIdle = _syncedState == GameRoomState.Idle;
        bool isInProgress = _syncedState == GameRoomState.InProgress;
        bool enoughPlayers = _syncedPlayerCount >= _syncedMinPlayers && _syncedMinPlayers > 0;

        //Debug.Log($"[MinigameStation] RefreshUI — localClientId: {localClientId}, " +
        //          $"hostClientId: {_hostClientId}, isHost: {localIsHost}, state: {_syncedState}");

        // Status label
        _statusLabel.text = _syncedState switch
        {
            GameRoomState.Idle => "Open — waiting for players",
            GameRoomState.Waiting => $"{_syncedPlayerCount} player(s) in queue",
            GameRoomState.Countdown => "Starting...",
            GameRoomState.InProgress => "Game in progress",
            _ => ""
        };

        // Countdown label
        _countdownLabel.gameObject.SetActive(isCountdown);

        // Join
        _joinButton.gameObject.SetActive(!localInSession && !isInProgress);
        _joinButton.interactable = isIdle || isWaiting;

        // Leave — non-host only
        _leaveButton.gameObject.SetActive(localInSession && !localIsHost && !isInProgress);

        // Close
        _closeButton.gameObject.SetActive(!isInProgress);
        _closeButton.interactable = !isInProgress;

        // Start — host only
        _startButton.gameObject.SetActive(localIsHost && isWaiting && _syncedGameSelected && enoughPlayers);

        // Game buttons — host only
        _gameButtonContainer.gameObject.SetActive(localIsHost && isWaiting);
        if (localIsHost && isWaiting)
            BuildGameButtons();

        // Close label
        TextMeshProUGUI closeLabel = _closeButton.GetComponentInChildren<TextMeshProUGUI>();
        if (closeLabel != null)
            closeLabel.text = localIsHost && (isCountdown || localInSession) ? "Cancel" : "Close";

        // Player list
        BuildPlayerList();
    }

    private void BuildGameButtons()
    {
        //Debug.Log("[MinigameStation] BuildGameButtons — start");

        foreach (Transform child in _gameButtonContainer)
            Destroy(child.gameObject);

        if (_registry == null) return;

        //Debug.Log($"[MinigameStation] BuildGameButtons — entries: {_registry.GetActiveEntries().Count}");

        foreach (MiniGameRegistryEntry entry in _registry.GetActiveEntries())
        {
            //Debug.Log($"[MinigameStation] BuildGameButtons — creating button for: {entry?.MiniGameName}");

            Button btn = Instantiate(_gameButtonPrefab, _gameButtonContainer);
            //Debug.Log("[MinigameStation] BuildGameButtons — button instantiated");

            TextMeshProUGUI label = btn.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null) label.text = entry.MiniGameName;

            bool isSelected = _currentSession?.SelectedGame?.MiniGameId == entry.MiniGameId;
            btn.interactable = !isSelected;

            string id = entry.MiniGameId;
            btn.onClick.AddListener(() => OnGameSelected(id));
        }

        //Debug.Log("[MinigameStation] BuildGameButtons — complete");
    }

    private void BuildPlayerList()
    {
        foreach (Transform child in _playerListContainer)
            Destroy(child.gameObject);

        for (int i = 0; i < _syncedPlayerNames.Count; i++)
        {
            TextMeshProUGUI entry = Instantiate(_playerListEntryPrefab, _playerListContainer);
            bool isHost = i == 0; // host is always first in list
            entry.text = isHost ? $"{_syncedPlayerNames[i]} (Host)" : _syncedPlayerNames[i];
        }
    }

    // ─── Countdown Display ────────────────────────────────────────────────────

    /// Called by GameRoomManager each countdown tick via RPC.
    public void UpdateCountdown(int secondsRemaining)
    {
        _countdownLabel.text = $"Starting in {secondsRemaining}...";
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private bool IsLocalPlayerInSession()
    {
        if (_localPlayer == null) return false;
        return _syncedClientIds.Contains(_localPlayer.OwnerId);
    }

    private string GetPromptLabel()
    {
        if (_currentSession == null) return "View Game Room";

        return _currentSession.State switch
        {
            GameRoomState.InProgress => "In Progress",
            GameRoomState.Loading => "Loading...",
            GameRoomState.Returning => "Returning...",
            _ => "View Game Room"
        };
    }

    public void UpdateSessionState(int hostClientId, List<string> playerNames,
        List<int> clientIds, GameRoomState state, bool gameSelected, int minPlayers)
    {
        Debug.Log($"[MinigameStation] UpdateSessionState — hostId: {hostClientId}, state: {state}");
        _hostClientId = hostClientId;
        _syncedPlayerNames = playerNames;
        _syncedClientIds = clientIds;
        _syncedState = state;
        _syncedPlayerCount = playerNames.Count;
        _syncedGameSelected = gameSelected;
        _syncedMinPlayers = minPlayers;

        // Close panel when game is starting or in progress
        if (state == GameRoomState.Loading ||
            state == GameRoomState.InProgress ||
            state == GameRoomState.Returning ||
            state == GameRoomState.Results)
        {
            if (_isPanelOpen)
                ClosePanel();
            return;
        }

        if (_isPanelOpen)
            RefreshUI();
    }
}