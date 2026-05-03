// SessionManager.cs
// Place in: Assets/_Project/Scripts/Session/
// Owns: session state, join code display, host tracking, player count.
// Communicates with: NetworkManager (FishNet), LobbyManager, UIManager.
// Attach to a new GameObject named "SessionManager" in the Bootstrap scene under _Managers.

using UnityEngine;
using FishNet;
using FishNet.Managing;
using FishNet.Connection;
using System.Collections.Generic;

public class SessionManager : SingletonBehaviour<SessionManager>
{
    // ─── Session State ────────────────────────────────────────────────────────

    public enum SessionState
    {
        Idle,       // No session active
        Waiting,    // Session created, waiting for players
        Active,     // Game is running
        Ending      // Session is wrapping up
    }

    public SessionState CurrentState { get; private set; } = SessionState.Idle;

    // ─── Join Code ────────────────────────────────────────────────────────────

    // Set by Relay when a host creates a session.
    // Placeholder until Unity Relay is integrated.
    public string JoinCode { get; private set; } = string.Empty;

    // ─── Host Tracking ────────────────────────────────────────────────────────

    public bool IsHost => InstanceFinder.IsServerStarted;

    // ─── Player Tracking ─────────────────────────────────────────────────────

    // Populated by FishNet connection callbacks
    private List<NetworkConnection> _connectedPlayers = new List<NetworkConnection>();
    public int PlayerCount => _connectedPlayers.Count;

    // ─── Events ───────────────────────────────────────────────────────────────

    // UIManager and LobbyManager listen to these
    public event System.Action<SessionState> OnSessionStateChanged;
    public event System.Action<string> OnJoinCodeReady;
    public event System.Action<int> OnPlayerCountChanged;

    // ─── Lifecycle ────────────────────────────────────────────────────────────

    protected override void Awake()
    {
        base.Awake();
    }

    private void OnEnable()
    {
        // Subscribe to FishNet server connection events
        InstanceFinder.ServerManager.OnRemoteConnectionState += OnRemoteConnectionState;
    }

    private void OnDisable()
    {
        if (InstanceFinder.ServerManager != null)
        {
            InstanceFinder.ServerManager.OnRemoteConnectionState -= OnRemoteConnectionState;
        }
    }

    // ─── Session Control ──────────────────────────────────────────────────────

    // Called when local player creates a new session as host
    public void StartHostSession()
    {
        if (CurrentState != SessionState.Idle)
        {
            Debug.LogWarning("[SessionManager] Cannot start session — not in Idle state.");
            return;
        }

        InstanceFinder.ServerManager.StartConnection();
        InstanceFinder.ClientManager.StartConnection();

        // TODO: Replace with real Relay join code once Unity Relay is integrated
        JoinCode = GeneratePlaceholderCode();

        Debug.Log($"[SessionManager] Host session started. Join code: {JoinCode}");

        SetState(SessionState.Waiting);
        OnJoinCodeReady?.Invoke(JoinCode);
    }

    // Called when local player joins an existing session as client
    public void JoinSession(string joinCode)
    {
        if (CurrentState != SessionState.Idle)
        {
            Debug.LogWarning("[SessionManager] Cannot join session — not in Idle state.");
            return;
        }

        JoinCode = joinCode;

        // TODO: Pass join code to Unity Relay to resolve host address
        // For now, connects to localhost for local testing
        InstanceFinder.ClientManager.StartConnection();

        Debug.Log($"[SessionManager] Joining session with code: {joinCode}");

        SetState(SessionState.Waiting);
    }

    // Called when host ends the session
    public void EndSession()
    {
        SetState(SessionState.Ending);

        InstanceFinder.ServerManager.StopConnection(sendDisconnectMessage: true);
        InstanceFinder.ClientManager.StopConnection();

        _connectedPlayers.Clear();
        JoinCode = string.Empty;

        Debug.Log("[SessionManager] Session ended.");

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
                Debug.Log($"[SessionManager] Player connected. Total: {PlayerCount}");
                OnPlayerCountChanged?.Invoke(PlayerCount);
            }
        }
        else if (args.ConnectionState == FishNet.Transporting.RemoteConnectionState.Stopped)
        {
            if (_connectedPlayers.Contains(conn))
            {
                _connectedPlayers.Remove(conn);
                Debug.Log($"[SessionManager] Player disconnected. Total: {PlayerCount}");
                OnPlayerCountChanged?.Invoke(PlayerCount);

                // Per design doc: session ends if host disconnects
                // Host loss is handled by FishNet — clients will receive disconnect event
            }
        }
    }

    // ─── State Management ─────────────────────────────────────────────────────

    private void SetState(SessionState newState)
    {
        CurrentState = newState;
        Debug.Log($"[SessionManager] State → {newState}");
        OnSessionStateChanged?.Invoke(newState);
    }

    // ─── Placeholder Utilities ────────────────────────────────────────────────

    // Temporary — replaced by Unity Relay join code on integration
    private string GeneratePlaceholderCode()
    {
        return Random.Range(1000, 9999).ToString();
    }
}