// SessionManager.cs
// Place in: Assets/_Project/Scripts/Session/
// Owns: session state, join code, host tracking, player count.
// Communicates with: RelayManager, LobbyManager, UIManager.
// Attach to: SessionManager GameObject in Bootstrap scene under _Managers.

using UnityEngine;
using FishNet;
using FishNet.Connection;
using System.Collections.Generic;

public class SessionManager : SingletonBehaviour<SessionManager>
{
    // ─── Config ───────────────────────────────────────────────────────────────

    [Header("Session Settings")]
    [SerializeField] private int maxPlayers = 16;

    // ─── Session State ────────────────────────────────────────────────────────

    public enum SessionState
    {
        Idle,
        Waiting,
        Active,
        Ending
    }

    public SessionState CurrentState { get; private set; } = SessionState.Idle;

    // ─── Join Code ────────────────────────────────────────────────────────────

    public string JoinCode { get; private set; } = string.Empty;

    // ─── Host Tracking ────────────────────────────────────────────────────────

    public bool IsHost => InstanceFinder.IsServerStarted;

    // ─── Player Tracking ─────────────────────────────────────────────────────

    private List<NetworkConnection> _connectedPlayers = new List<NetworkConnection>();
    public int PlayerCount => _connectedPlayers.Count;

    // ─── Events ───────────────────────────────────────────────────────────────

    public event System.Action<SessionState> OnSessionStateChanged;
    public event System.Action<string> OnJoinCodeReady;
    public event System.Action<int> OnPlayerCountChanged;

    public event System.Action<string> OnDisconnectedWithReason;

    // ─── Lifecycle ────────────────────────────────────────────────────────────

    protected override void Awake()
    {
        base.Awake();
    }

    private void OnEnable()
    {
        InstanceFinder.ServerManager.OnRemoteConnectionState += OnRemoteConnectionState;
        InstanceFinder.ClientManager.OnClientConnectionState += OnClientConnectionState;
    }

    private void OnDisable()
    {
        if (InstanceFinder.ServerManager != null)
        {
            InstanceFinder.ServerManager.OnRemoteConnectionState -= OnRemoteConnectionState;
        }

        if (InstanceFinder.ClientManager != null)
            InstanceFinder.ClientManager.OnClientConnectionState -= OnClientConnectionState;
    }

    // ─── Session Control ──────────────────────────────────────────────────────

    public async void StartHostSession()
    {
        if (CurrentState != SessionState.Idle)
        {
            Debug.LogWarning("[SessionManager] Cannot start session — not in Idle state.");
            return;
        }

        if (RelayManager.Instance == null)
        {
            Debug.LogError("[SessionManager] RelayManager not found.");
            return;
        }

        SetState(SessionState.Waiting);

        try
        {
            // Allocate Relay and get join code
            string joinCode = await RelayManager.Instance.CreateRelaySessionAsync(maxPlayers);
            JoinCode = joinCode;

            // Start FishNet host after Relay is configured
            InstanceFinder.ServerManager.StartConnection();
            InstanceFinder.ClientManager.StartConnection();

            //Debug.Log($"[SessionManager] Host session started. Join code: {JoinCode}");
            OnJoinCodeReady?.Invoke(JoinCode);

            // Host is up — session is now active
            SetState(SessionState.Active);
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[SessionManager] StartHostSession failed: {e.Message}");
            SetState(SessionState.Idle);
        }
    }

    public async void JoinSession(string joinCode)
    {
        if (CurrentState != SessionState.Idle)
        {
            Debug.LogWarning("[SessionManager] Cannot join session — not in Idle state.");
            return;
        }

        if (RelayManager.Instance == null)
        {
            Debug.LogError("[SessionManager] RelayManager not found.");
            return;
        }

        SetState(SessionState.Waiting);

        try
        {
            // Resolve join code via Relay and configure transport
            await RelayManager.Instance.JoinRelaySessionAsync(joinCode);
            JoinCode = joinCode;

            // Connect client after Relay is configured
            InstanceFinder.ClientManager.StartConnection();

           // Debug.Log($"[SessionManager] Joined session with code: {joinCode}");

            // Connected — session is now active
            SetState(SessionState.Active);
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[SessionManager] JoinSession failed: {e.Message}");
            SetState(SessionState.Idle);
        }
    }

    public void EndSession()
    {
        SetState(SessionState.Ending);

        InstanceFinder.ServerManager.StopConnection(sendDisconnectMessage: true);
        InstanceFinder.ClientManager.StopConnection();

        _connectedPlayers.Clear();
        JoinCode = string.Empty;

        //Debug.Log("[SessionManager] Session ended.");
        SetState(SessionState.Idle);
    }

    // ─── Connection Callbacks ─────────────────────────────────────────────────

    private void OnRemoteConnectionState(NetworkConnection conn, FishNet.Transporting.RemoteConnectionStateArgs args)
    {
        if (args.ConnectionState == FishNet.Transporting.RemoteConnectionState.Started)
        {
            if (!_connectedPlayers.Contains(conn))
            {
                _connectedPlayers.Add(conn);
                //Debug.Log($"[SessionManager] Player connected. Total: {PlayerCount}");
                OnPlayerCountChanged?.Invoke(PlayerCount);
            }
        }
        else if (args.ConnectionState == FishNet.Transporting.RemoteConnectionState.Stopped)
        {
            if (_connectedPlayers.Contains(conn))
            {
                _connectedPlayers.Remove(conn);
                //Debug.Log($"[SessionManager] Player disconnected. Total: {PlayerCount}");
                OnPlayerCountChanged?.Invoke(PlayerCount);
            }
        }
    }

    // ─── State Management ─────────────────────────────────────────────────────

    private void SetState(SessionState newState)
    {
        CurrentState = newState;
        //Debug.Log($"[SessionManager] State → {newState}");
        OnSessionStateChanged?.Invoke(newState);
    }

    private void OnClientConnectionState(FishNet.Transporting.ClientConnectionStateArgs args)
    {
        if (args.ConnectionState == FishNet.Transporting.LocalConnectionState.Stopped && CurrentState == SessionState.Active)
        {
            Debug.LogWarning("[SessionManager] Client connection stopped unexpectedly.");
            //Debug.Log($"[SessionManager] ClientConnectionState: {args.ConnectionState}, CurrentState: {CurrentState}");
            string reason = IsHost ? "Session lost — Relay connection failed." : "Disconnected — host session ended.";
            _connectedPlayers.Clear();
            JoinCode = string.Empty;
            SetState(SessionState.Idle);
            DisconnectReason.Instance.Set(reason);
            UnityEngine.SceneManagement.SceneManager.LoadScene("MainMenu");
        }
    }
}