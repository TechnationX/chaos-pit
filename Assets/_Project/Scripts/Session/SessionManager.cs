// SessionManager.cs
// Place in: Assets/_Project/Scripts/Session/
// Owns: session state, join code, host tracking, player count.
// Communicates with: RelayManager, LobbyManager, UIManager.
// Attach to: SessionManager GameObject in Bootstrap scene under _Managers.

using FishNet;
using FishNet.Connection;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Threading.Tasks;


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

            // Start FishNet's server, waiting for actual confirmation
            // instead of assuming success — same fix as JoinSession below,
            // applied here for consistency. This hadn't shown symptoms
            // (host's server+client are the same local process, so there
            // was never a real round-trip to lose the race against), but
            // it was the same unguarded "call StartConnection then assume
            // it worked" shape.
            bool serverStarted = await WaitForServerConnectionAsync(timeoutSeconds: 5f);
            if (!serverStarted)
            {
                Debug.LogError("[SessionManager] StartHostSession failed: server did not start in time.");
                InstanceFinder.ServerManager.StopConnection(sendDisconnectMessage: false);
                SetState(SessionState.Idle);
                return;
            }

            // Host plays through its own local ClientManager same as any
            // other peer — wait for that connection too. The old
            // `await Task.Delay(100)` that used to sit between starting
            // the server and starting the client was a guess at "give the
            // server enough time to be ready" — now that we wait for a
            // real Started event from the server above before ever
            // calling ClientManager.StartConnection() here, that guess is
            // replaced by an actual guarantee, so the delay is gone.
            bool clientConnected = await WaitForClientConnectionAsync(timeoutSeconds: 5f);
            if (!clientConnected)
            {
                Debug.LogError("[SessionManager] StartHostSession failed: host's own client did not connect in time.");
                InstanceFinder.ClientManager.StopConnection();
                InstanceFinder.ServerManager.StopConnection(sendDisconnectMessage: true);
                SetState(SessionState.Idle);
                return;
            }

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

    // Same pattern as WaitForClientConnectionAsync below, for the host's
    // own ServerManager — StartHostSession needs to confirm BOTH the
    // server coming up and the host's own local client connecting before
    // it's safe to call the session Active.
    private Task<bool> WaitForServerConnectionAsync(float timeoutSeconds)
    {
        var tcs = new TaskCompletionSource<bool>();
        System.Action<FishNet.Transporting.ServerConnectionStateArgs> handler = null;
        handler = (args) =>
        {
            if (args.ConnectionState == FishNet.Transporting.LocalConnectionState.Started)
            {
                InstanceFinder.ServerManager.OnServerConnectionState -= handler;
                tcs.TrySetResult(true);
            }
            else if (args.ConnectionState == FishNet.Transporting.LocalConnectionState.Stopped)
            {
                InstanceFinder.ServerManager.OnServerConnectionState -= handler;
                tcs.TrySetResult(false);
            }
        };

        InstanceFinder.ServerManager.OnServerConnectionState += handler;
        InstanceFinder.ServerManager.StartConnection();

        _ = TimeoutAndUnsubscribeServer(tcs, timeoutSeconds, handler);

        return tcs.Task;
    }

    private async Task TimeoutAndUnsubscribeServer(TaskCompletionSource<bool> tcs, float seconds, System.Action<FishNet.Transporting.ServerConnectionStateArgs> handler)
    {
        await Task.Delay(System.TimeSpan.FromSeconds(seconds));
        if (!tcs.Task.IsCompleted)
        {
            InstanceFinder.ServerManager.OnServerConnectionState -= handler;
            tcs.TrySetResult(false);
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

            // Connect client, then ACTUALLY WAIT for the connection to
            // finish before declaring the session Active. StartConnection()
            // is fire-and-forget — it only kicks off the relay/UDP
            // handshake and returns immediately, long before the real
            // round-trip completes. This used to call SetState(Active)
            // on the very next line with no wait at all, which raced
            // JoinSessionScreen's scene load (SetState(Active) is what
            // triggers SceneManager.LoadScene(lobbySceneName)) against the
            // handshake actually finishing. On a fast/local connection the
            // race happened to resolve in our favor, which is why this
            // looked fine in testing; under real network conditions it
            // sometimes lost the race — Lobby would load before the
            // client was actually connected, which is what caused scene
            // NetworkObjects (pool pockets/buttons, BowlingGameController)
            // to never register for that client, and — when the handshake
            // was slow enough — the join to just never complete at all.
            bool connected = await WaitForClientConnectionAsync(timeoutSeconds: 5f);

            if (!connected)
            {
                Debug.LogError("[SessionManager] JoinSession failed: client did not connect in time.");
                InstanceFinder.ClientManager.StopConnection();
                SetState(SessionState.Idle);
                return;
            }

            Debug.Log($"[SessionManager] Joined session with code: {joinCode}");

            // Connected — session is now active
            SetState(SessionState.Active);
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[SessionManager] JoinSession failed: {e.Message}");
            SetState(SessionState.Idle);
        }
    }

    // Starts the client connection and resolves once FishNet reports the
    // outcome: true on Started, false on Stopped (handshake rejected/relay
    // dropped) or on timeout (handshake never resolved either way). The
    // handler is subscribed BEFORE StartConnection() is called so a
    // same-frame Started event (e.g. local/host testing) can't be missed,
    // and it always unsubscribes itself so it never double-fires or leaks
    // across repeated join attempts.
    private Task<bool> WaitForClientConnectionAsync(float timeoutSeconds)
    {
        var tcs = new TaskCompletionSource<bool>();
        System.Action<FishNet.Transporting.ClientConnectionStateArgs> handler = null;
        handler = (args) =>
        {
            if (args.ConnectionState == FishNet.Transporting.LocalConnectionState.Started)
            {
                InstanceFinder.ClientManager.OnClientConnectionState -= handler;
                tcs.TrySetResult(true);
            }
            else if (args.ConnectionState == FishNet.Transporting.LocalConnectionState.Stopped)
            {
                InstanceFinder.ClientManager.OnClientConnectionState -= handler;
                tcs.TrySetResult(false);
            }
        };

        InstanceFinder.ClientManager.OnClientConnectionState += handler;
        InstanceFinder.ClientManager.StartConnection();

        _ = TimeoutAndUnsubscribe(tcs, timeoutSeconds, handler);

        return tcs.Task;
    }

    private async Task TimeoutAndUnsubscribe(TaskCompletionSource<bool> tcs, float seconds, System.Action<FishNet.Transporting.ClientConnectionStateArgs> handler)
    {
        await Task.Delay(System.TimeSpan.FromSeconds(seconds));
        if (!tcs.Task.IsCompleted)
        {
            InstanceFinder.ClientManager.OnClientConnectionState -= handler;
            tcs.TrySetResult(false);
        }
    }

    public void EndSession()
    {
        SetState(SessionState.Ending);

        InstanceFinder.ServerManager.StopConnection(sendDisconnectMessage: true);
        InstanceFinder.ClientManager.StopConnection();

        _connectedPlayers.Clear();
        JoinCode = string.Empty;

        RelayManager.Instance?.ClearAllocation();

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
                GameRoomManager.Instance?.HandlePlayerDisconnected(conn);
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
        //Debug.Log($"[SessionManager] OnClientConnectionState — state: {args.ConnectionState}, CurrentState: {CurrentState}");

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