// PrivateRoom.cs
using FishNet.Component.Transforming;
using FishNet.Connection;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using FishNet.Transporting;
using UnityEngine;
using UnityEngine.InputSystem;

// Server-authoritative one-occupant room (the dressing room). Holds who is inside,
// the inside/outside spawn points, and the AFK timeout. Players never walk in: the
// room is sealed and PrivateRoomPanel (Enter outside, Exit inside) asks this to
// teleport them in or out, the same coordinator-plus-plain-buttons shape as
// StageActSelector/StageActSelectorButton.
//
// Scene setup: this needs its own NetworkObject + NetworkObserver/ManualRevealCondition
// and must live under LobbyRoot, so LobbySpawner.RegisterSceneManualRevealRoutine
// reveals it to joining clients paced like every other scene NetworkObject (an
// un-gated NetworkObject is what caused the join-hang bug).
public class PrivateRoom : NetworkBehaviour
{
    [Header("Spawn Points")]
    [SerializeField] private Transform _insideSpawn;
    [SerializeField] private Transform _outsideSpawn;

    [Header("AFK")]
    [Tooltip("Seconds with no keyboard/mouse input from the occupant before the server sends them out and frees the room.")]
    [SerializeField] private float _afkTimeoutSeconds = 300f;
    [Tooltip("How often the occupant's client reports that there has been input. Must be well under the AFK timeout.")]
    [SerializeField] private float _activityReportInterval = 30f;

    [Header("Prompts")]
    [SerializeField] private string _enterPrompt = "Enter Dressing Room";
    [SerializeField] private string _occupiedPrompt = "Occupied";
    [SerializeField] private string _exitPrompt = "Leave Room";

    // ClientId of the player inside, -1 when empty. Synced so every client's panel
    // prompt shows the same Occupied state.
    private readonly SyncVar<int> _occupantClientId = new SyncVar<int>(-1);

    // Server-only state.
    private NetworkObject _occupantPlayer;
    private float _lastActivityTime;

    // Occupant-client-only state.
    private bool _inputSinceLastReport;
    private float _reportTimer;

    public bool IsOccupied => _occupantClientId.Value >= 0;

    // True only on the occupant's own machine (host included).
    public bool IsLocalPlayerInside =>
        IsOccupied &&
        ClientManager != null && ClientManager.Connection != null &&
        ClientManager.Connection.ClientId == _occupantClientId.Value;

    public string GetPromptFor(PrivateRoomPanel.PanelRole role)
    {
        if (role == PrivateRoomPanel.PanelRole.Exit)
            return IsLocalPlayerInside ? _exitPrompt : "";

        if (!IsOccupied) return _enterPrompt;
        return IsLocalPlayerInside ? "" : _occupiedPrompt;
    }

    // ---------------- Client entry points (called by PrivateRoomPanel) ----------------

    public void RequestEnter(PlayerObject player)
    {
        if (player == null || IsOccupied) return; // server re-checks; this just saves a round trip
        ServerRequestEnter(player.NetworkObject);
    }

    public void RequestExit(PlayerObject player)
    {
        if (player == null || !IsLocalPlayerInside) return;
        ServerRequestExit(player.NetworkObject);
    }

    // ---------------- Server ----------------

    public override void OnStartServer()
    {
        base.OnStartServer();
        ServerManager.OnRemoteConnectionState += HandleRemoteConnectionState;
    }

    public override void OnStopServer()
    {
        base.OnStopServer();
        if (ServerManager != null)
            ServerManager.OnRemoteConnectionState -= HandleRemoteConnectionState;
    }

    [ServerRpc(RequireOwnership = false)]
    private void ServerRequestEnter(NetworkObject playerNetObj, NetworkConnection sender = null)
    {
        if (sender == null || playerNetObj == null) return;
        if (playerNetObj.Owner != sender) return; // only for your own player
        if (_occupantClientId.Value >= 0) return; // someone got there first
        if (_insideSpawn == null)
        {
            Debug.LogWarning("[PrivateRoom] No Inside Spawn assigned — can't let anyone in.");
            return;
        }

        _occupantClientId.Value = sender.ClientId;
        _occupantPlayer = playerNetObj;
        _lastActivityTime = Time.time;

        ServerTeleport(sender, playerNetObj, _insideSpawn);
    }

    [ServerRpc(RequireOwnership = false)]
    private void ServerRequestExit(NetworkObject playerNetObj, NetworkConnection sender = null)
    {
        if (sender == null || sender.ClientId != _occupantClientId.Value) return;

        if (_outsideSpawn != null && _occupantPlayer != null)
            ServerTeleport(sender, _occupantPlayer, _outsideSpawn);

        ServerRelease();
    }

    [ServerRpc(RequireOwnership = false)]
    private void ServerReportActivity(NetworkConnection sender = null)
    {
        if (sender == null || sender.ClientId != _occupantClientId.Value) return;
        _lastActivityTime = Time.time;
    }

    private void Update()
    {
        if (IsServerInitialized && _occupantClientId.Value >= 0 &&
            Time.time - _lastActivityTime > _afkTimeoutSeconds)
        {
            ServerEvictForInactivity();
        }

        if (IsClientInitialized && IsLocalPlayerInside)
            TrackLocalActivity();
    }

    private void ServerEvictForInactivity()
    {
        NetworkConnection occupantConn = _occupantPlayer != null ? _occupantPlayer.Owner : null;

        if (occupantConn != null && _occupantPlayer != null)
        {
            Vector3 pos = _outsideSpawn != null ? _outsideSpawn.position : _occupantPlayer.transform.position;
            Quaternion rot = _outsideSpawn != null ? _outsideSpawn.rotation : _occupantPlayer.transform.rotation;

            NetworkTransform nt = _occupantPlayer.GetComponent<NetworkTransform>();
            if (nt != null) nt.Teleport();

            TargetEvicted(occupantConn, _occupantPlayer, pos, rot);
        }

        Debug.Log("[PrivateRoom] Occupant inactive past the AFK timeout — room freed.");
        ServerRelease();
    }

    private void HandleRemoteConnectionState(NetworkConnection conn, RemoteConnectionStateArgs args)
    {
        if (args.ConnectionState != RemoteConnectionState.Stopped) return;
        if (conn == null || conn.ClientId != _occupantClientId.Value) return;
        ServerRelease();
    }

    private void ServerRelease()
    {
        _occupantClientId.Value = -1;
        _occupantPlayer = null;
    }

    // Same two-sided teleport GameRoomManager uses: Teleport() on the server's
    // NetworkTransform, then the owning client moves its own transform.
    private void ServerTeleport(NetworkConnection conn, NetworkObject playerNetObj, Transform point)
    {
        NetworkTransform nt = playerNetObj.GetComponent<NetworkTransform>();
        if (nt != null) nt.Teleport();

        TargetTeleport(conn, playerNetObj, point.position, point.rotation);
    }

    // ---------------- Owning client ----------------

    [TargetRpc]
    private void TargetTeleport(NetworkConnection conn, NetworkObject playerNetObj, Vector3 position, Quaternion rotation)
    {
        PlayerObject player = playerNetObj != null ? playerNetObj.GetComponent<PlayerObject>() : null;
        TeleportLocal(player, position, rotation);
    }

    [TargetRpc]
    private void TargetEvicted(NetworkConnection conn, NetworkObject playerNetObj, Vector3 position, Quaternion rotation)
    {
        // Closes the creator without saving if it's open, and restores the lobby view
        // and controls, before moving the player out.
        CustomizationStation.ForceCloseActive();

        PlayerObject player = playerNetObj != null ? playerNetObj.GetComponent<PlayerObject>() : null;
        TeleportLocal(player, position, rotation);
    }

    // Owner-side move, matching GameRoomManager.RpcTeleportAndUnlockPlayer. Also used
    // by CustomizationStation for its optional return point.
    public static void TeleportLocal(PlayerObject player, Vector3 position, Quaternion rotation)
    {
        if (player == null) return;

        NetworkTransform nt = player.GetComponent<NetworkTransform>();
        if (nt != null) nt.Teleport();

        player.transform.position = position;
        player.transform.rotation = rotation;
    }

    // Reports "there was input" every _activityReportInterval seconds, only if there
    // actually was some — so the server's timer only resets for real activity, and
    // traffic stays at one tiny RPC every 30 seconds at most.
    private void TrackLocalActivity()
    {
        if (HasAnyInputThisFrame()) _inputSinceLastReport = true;

        _reportTimer += Time.deltaTime;
        if (_reportTimer < _activityReportInterval) return;
        _reportTimer = 0f;

        if (!_inputSinceLastReport) return;
        _inputSinceLastReport = false;
        ServerReportActivity();
    }

    private static bool HasAnyInputThisFrame()
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard != null && keyboard.anyKey.isPressed) return true;

        Mouse mouse = Mouse.current;
        if (mouse == null) return false;

        return mouse.delta.ReadValue().sqrMagnitude > 0f ||
               mouse.scroll.ReadValue().sqrMagnitude > 0f ||
               mouse.leftButton.isPressed ||
               mouse.rightButton.isPressed;
    }
}
