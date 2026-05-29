// GameRoomManager.cs

using FishNet;
using FishNet.Component.Transforming;
using FishNet.Connection;
using FishNet.Managing.Scened;
using FishNet.Object;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;
using UnityEngine;

public class GameRoomManager : NetworkBehaviour
{
    public static GameRoomManager Instance { get; private set; }

    [Header("Settings")]
    [SerializeField] private float _countdownDuration = 10f;

    // Station registry — self-registered by MinigameStation on Awake
    private Dictionary<int, MinigameStation> _stations
        = new Dictionary<int, MinigameStation>();

    // Per-station state
    private Dictionary<int, GameRoomSession> _sessions
        = new Dictionary<int, GameRoomSession>();

    // Layer index for GameRoom layer
    private int _gameRoomLayer;

    [SerializeField] private MiniGameRegistry _registry;

    private static List<MinigameStation> _pendingStations = new List<MinigameStation>();
    private Dictionary<int, HashSet<int>> _loadedClients = new Dictionary<int, HashSet<int>>();

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        _gameRoomLayer = LayerMask.NameToLayer("GameRoom");

        // Register any stations that were waiting
        foreach (var station in _pendingStations)
            RegisterStation(station);
        _pendingStations.Clear();
    }

    // ─── Station Registration ─────────────────────────────────────────────────

    public static void RequestRegistration(MinigameStation station)
    {
        if (Instance != null)
            Instance.RegisterStation(station);
        else
            _pendingStations.Add(station);
    }

    public void RegisterStation(MinigameStation station)
    {
        if (_stations.ContainsKey(station.StationIndex))
        {
            Debug.LogWarning($"[GameRoomManager] Station {station.StationIndex} already registered.");
            return;
        }

        _stations[station.StationIndex] = station;
        _sessions[station.StationIndex] = new GameRoomSession(station.StationIndex, _countdownDuration);
        Debug.Log($"[GameRoomManager] Station {station.StationIndex} registered.");
    }

    // ─── Join / Leave ─────────────────────────────────────────────────────────

    [ServerRpc(RequireOwnership = false)]
    public void RequestJoin(int stationIndex, PlayerObject player)
    {
        if (!_sessions.TryGetValue(stationIndex, out GameRoomSession session)) return;
        if (session.State != GameRoomState.Idle && session.State != GameRoomState.Waiting) return;
        if (session.Players.Contains(player)) return;

        MiniGameRegistryEntry entry = session.SelectedGame;
        if (entry != null && session.Players.Count >= entry.MaxPlayers) return;

        session.Players.Add(player);

        // First player becomes host
        if (session.Players.Count == 1)
            session.HostPlayer = player;

        session.State = GameRoomState.Waiting;

        // Teleport to waiting area
        TeleportToWaitingArea(stationIndex, player);

        // Lock movement and interactions
        player.Movement.SetMovementLocked(true);

        player.Interaction.SetInteractionEnabled(false);

        Debug.Log($"[GameRoomManager] Player {player.name} joined station {stationIndex}. " +
                  $"Count: {session.Players.Count}");

        // Notify station to refresh UI
        _stations[stationIndex].OnSessionUpdated(session);
        SyncSessionToClients(stationIndex, session);
        return;
    }

    [ServerRpc(RequireOwnership = false)]
    public void RequestLeave(int stationIndex, PlayerObject player)
    {
        if (!_sessions.TryGetValue(stationIndex, out GameRoomSession session)) return;
        if (!session.Players.Contains(player)) return;

        RemovePlayerFromSession(session, player, stationIndex);

        // Return to lobby
        ReturnPlayerToLobby(player);

        // If session is now empty reset it
        if (session.Players.Count == 0)
            ResetSession(stationIndex);

        _stations[stationIndex].OnSessionUpdated(session);
        SyncSessionToClients(stationIndex, session);
    }

    private void OnClientPresenceChangeEnd(ClientPresenceChangeEventArgs args, int stationIndex)
    {
        if (!_sessions.TryGetValue(stationIndex, out GameRoomSession session)) return;
        if (session.SelectedGame == null) return;

        // Only care about our scene
        if (args.Scene.name != session.SelectedGame.SceneName) return;
        // Only care about additions not removals
        if (!args.Added) return;

        if (!_loadedClients.ContainsKey(stationIndex))
            _loadedClients[stationIndex] = new HashSet<int>();

        _loadedClients[stationIndex].Add(args.Connection.ClientId);

        int expected = session.Players.Count;
        int loaded = _loadedClients[stationIndex].Count;

        Debug.Log($"[GameRoomManager] Client {args.Connection.ClientId} loaded — {loaded}/{expected}");

        if (loaded < expected) return;

        // All clients loaded — unsubscribe and start
        InstanceFinder.NetworkManager.SceneManager.OnClientPresenceChangeEnd -=
            (a) => OnClientPresenceChangeEnd(a, stationIndex);

        _loadedClients.Remove(stationIndex);
        StartCoroutine(StartGameAfterLoad(stationIndex));
    }

    private IEnumerator StartGameAfterLoad(int stationIndex)
    {
        // One frame buffer after all clients confirm loaded
        yield return new WaitForSeconds(0.5f);

        if (!_sessions.TryGetValue(stationIndex, out GameRoomSession session)) yield break;

        session.State = GameRoomState.InProgress;
        SyncSessionToClients(stationIndex, session);

        MiniGameController controller = FindMiniGameController(session.SelectedGame.SceneName);
        if (controller == null)
        {
            Debug.LogError($"[GameRoomManager] No MiniGameController found in {session.SelectedGame.SceneName}");
            yield break;
        }

        session.ActiveController = controller;
        controller.StartGame(session.Players);

        foreach (PlayerObject player in session.Players)
            RpcReinitializeCamera(player.Owner);

        Debug.Log($"[GameRoomManager] Game started for station {stationIndex}");
        _stations[stationIndex].OnSessionUpdated(session);
    }

    // ─── Host Controls ────────────────────────────────────────────────────────

    [ServerRpc(RequireOwnership = false)]
    public void SelectGame(int stationIndex, string miniGameId)
    {
        if (!_sessions.TryGetValue(stationIndex, out GameRoomSession session)) return;
        if (session.State != GameRoomState.Waiting) return;

        MiniGameRegistryEntry entry = session.Registry?.GetById(miniGameId);
        if (entry == null)
        {
            Debug.LogWarning($"[GameRoomManager] MiniGame {miniGameId} not found in registry.");
            return;
        }

        session.SelectedGame = entry;
        Debug.Log($"[GameRoomManager] Station {stationIndex} selected game: {entry.MiniGameName}");
        _stations[stationIndex].OnSessionUpdated(session);
        SyncSessionToClients(stationIndex, session);
    }

    [ServerRpc(RequireOwnership = false)]
    public void RequestStartCountdown(int stationIndex, PlayerObject requestingPlayer)
    {
        if (!_sessions.TryGetValue(stationIndex, out GameRoomSession session)) return;
        if (session.State != GameRoomState.Waiting) return;
        if (session.HostPlayer != requestingPlayer) return;
        if (session.SelectedGame == null) return;
        if (session.Players.Count < session.SelectedGame.MinPlayers) return;

        session.State = GameRoomState.Countdown;
        session.CountdownCoroutine = StartCoroutine(CountdownCoroutine(stationIndex));

        Debug.Log($"[GameRoomManager] Countdown started for station {stationIndex}");
        _stations[stationIndex].OnSessionUpdated(session);
    }

    [ServerRpc(RequireOwnership = false)]
    public void CancelCountdown(int stationIndex)
    {
        if (!_sessions.TryGetValue(stationIndex, out GameRoomSession session)) return;
        if (session.State != GameRoomState.Countdown) return;

        if (session.CountdownCoroutine != null)
            StopCoroutine(session.CountdownCoroutine);

        // Return all players to lobby
        foreach (PlayerObject player in session.Players.ToList())
            ReturnPlayerToLobby(player);

        ResetSession(stationIndex);
        Debug.Log($"[GameRoomManager] Countdown cancelled for station {stationIndex}");
        _stations[stationIndex].OnSessionUpdated(session);
        SyncSessionToClients(stationIndex, session);
    }

    // GameRoomManager.cs

    [ObserversRpc]
    private void RpcUpdateCountdown(int stationIndex, int secondsRemaining)
    {
        if (_stations.TryGetValue(stationIndex, out MinigameStation station))
            station.UpdateCountdown(secondsRemaining);
    }

    private IEnumerator CountdownCoroutine(int stationIndex)
    {
        GameRoomSession session = _sessions[stationIndex];
        int remaining = Mathf.RoundToInt(session.CountdownDuration);

        while (remaining > 0)
        {
            RpcUpdateCountdown(stationIndex, remaining);
            yield return new WaitForSeconds(1f);
            remaining--;
        }

        BeginTransition(stationIndex);
    }

    // ─── Scene Transition ─────────────────────────────────────────────────────

    [Server]
    private void BeginTransition(int stationIndex)
    {
        //Debug.Log($"[GameRoomManager] BeginTransition — stationIndex: {stationIndex}");

        if (!_sessions.TryGetValue(stationIndex, out GameRoomSession session))
        {
            Debug.LogError("[GameRoomManager] BeginTransition — session not found.");
            return;
        }

        //Debug.Log($"[GameRoomManager] BeginTransition — session found, state: {session.State}");
        //Debug.Log($"[GameRoomManager] BeginTransition — SelectedGame: {session.SelectedGame?.MiniGameName}");
        //Debug.Log($"[GameRoomManager] BeginTransition — ScoreManager: {ScoreManager.Instance != null}");
        //Debug.Log($"[GameRoomManager] BeginTransition — Players: {session.Players.Count}");

        session.State = GameRoomState.Loading;
        string sessionId = GetSessionId(stationIndex);

        ScoreManager.Instance.RegisterSession(sessionId);

        NetworkConnection[] connections = session.Players
            .Select(p => p.Owner)
            .Where(c => c != null)
            .ToArray();

        foreach (PlayerObject player in session.Players)
            SetPlayerLayer(player, _gameRoomLayer);

        // Subscribe BEFORE loading
        InstanceFinder.NetworkManager.SceneManager.OnClientPresenceChangeEnd +=
         (args) => OnClientPresenceChangeEnd(args, stationIndex);

        SceneLoadData sld = new SceneLoadData(session.SelectedGame.SceneName)
        {
            ReplaceScenes = ReplaceOption.None,
            Options = new LoadOptions { AllowStacking = true }
        };

        InstanceFinder.NetworkManager.SceneManager.LoadConnectionScenes(connections, sld);

        Debug.Log($"[GameRoomManager] Loading scene {session.SelectedGame.SceneName} " +
                  $"for station {stationIndex}");
    }

    // ─── Results ──────────────────────────────────────────────────────────────

    /// Called by MiniGameController when all rounds are complete.
    [Server]
    public void OnGameComplete(int stationIndex, List<RoundResult> results)
    {
        if (!_sessions.TryGetValue(stationIndex, out GameRoomSession session)) return;

        session.State = GameRoomState.Results;
        string sessionId = GetSessionId(stationIndex);

        // Process scores
        ScoreManager.Instance.SubmitResults(sessionId, results);

        // TODO: load Results Screen scene additively for session connections
        // SceneLoadData resultsScene = new SceneLoadData("ResultsScreen") { ... }

        // TODO: wire Results Screen dismiss to TriggerReturnToLobby()
        // For now auto-return after delay
        StartCoroutine(AutoReturnAfterResults(stationIndex, 5f));

        Debug.Log($"[GameRoomManager] Game complete for station {stationIndex}");
    }

    private IEnumerator AutoReturnAfterResults(int stationIndex, float delay)
    {
        yield return new WaitForSeconds(delay);
        ReturnToLobby(stationIndex);
    }

    // ─── Return to Lobby ──────────────────────────────────────────────────────

    [Server]
    public void ReturnToLobby(int stationIndex)
    {
        if (!_sessions.TryGetValue(stationIndex, out GameRoomSession session)) return;

        session.State = GameRoomState.Returning;
        SyncSessionToClients(stationIndex, session);

        string sessionId = GetSessionId(stationIndex);

        // Clean up mini game
        session.ActiveController?.CleanUp();

        // Unload game room scene for session connections
        NetworkConnection[] connections = session.Players
            .Select(p => p.Owner)
            .Where(c => c != null)
            .ToArray();

        SceneUnloadData sud = new SceneUnloadData(session.SelectedGame.SceneName);
        InstanceFinder.NetworkManager.SceneManager.UnloadConnectionScenes(connections, sud);

        // Return players to lobby
        foreach (PlayerObject player in session.Players.ToList())
            ReturnPlayerToLobby(player);

        // Clear score session
        ScoreManager.Instance.UnregisterSession(sessionId);

        // Refresh lobby leaderboard
        // TODO: LeaderboardManager.Instance.Refresh() — wire up in Step 14

        ResetSession(stationIndex);
        Debug.Log($"[GameRoomManager] Players returned to lobby from station {stationIndex}");
    }

    // ─── Player Helpers ───────────────────────────────────────────────────────

    private void TeleportToWaitingArea(int stationIndex, PlayerObject player)
    {
        Transform[] waitingPoints = _stations[stationIndex].WaitingAreaPoints;
        if (waitingPoints == null || waitingPoints.Length == 0)
        {
            Debug.LogWarning($"[GameRoomManager] No waiting area points on station {stationIndex}");
            return;
        }

        GameRoomSession session = _sessions[stationIndex];
        int index = Mathf.Min(session.Players.Count - 1, waitingPoints.Length - 1);
        Vector3 pos = waitingPoints[index].position;
        Quaternion rot = waitingPoints[index].rotation;

        NetworkTransform nt = player.GetComponent<NetworkTransform>();
        if (nt != null) nt.Teleport();
        else
            player.transform.position = pos;

        RpcTeleportToPoint(player.Owner, pos, rot);
    }

    private void ReturnPlayerToLobby(PlayerObject player)
    {
        SetPlayerLayer(player, 0);
        player.Interaction.SetInteractionEnabled(true);

        if (LobbySpawner.Instance != null &&
            LobbySpawner.Instance.TryGetSpawnPoint(out Vector3 pos, out Quaternion rot))
        {
            Debug.Log($"[GameRoomManager] Returning {player.name} to spawn: {pos}");

            // Use FishNet's built-in teleport — bypasses NT interpolation
            NetworkTransform nt = player.GetComponent<NetworkTransform>();
            if (nt != null) nt.Teleport();
            else
                player.transform.position = pos;

            RpcTeleportAndUnlock(player.Owner, pos, rot);
        }
    }

    private IEnumerator ReenableNetworkTransform(NetworkTransform nt, float delay = 0.2f)
    {
        yield return new WaitForSeconds(delay);
        if (nt != null) nt.enabled = true;
    }

    private void RemovePlayerFromSession(GameRoomSession session, PlayerObject player, int stationIndex)
    {
        session.Players.Remove(player);

        // Migrate host if needed
        if (session.HostPlayer == player)
            MigrateHost(session, stationIndex);
    }

    private void MigrateHost(GameRoomSession session, int stationIndex)
    {
        if (session.Players.Count == 0)
        {
            session.HostPlayer = null;
            return;
        }

        session.HostPlayer = session.Players[0];
        Debug.Log($"[GameRoomManager] Host migrated to {session.HostPlayer.name} " +
                  $"on station {stationIndex}");

        // TODO: RPC to new host client to show host UI controls
    }

    private void SetPlayerLayer(PlayerObject player, int layer)
    {
        player.gameObject.layer = layer;
        foreach (Transform child in player.GetComponentsInChildren<Transform>())
            child.gameObject.layer = layer;
    }

    // ─── Disconnect Handling ──────────────────────────────────────────────────

    public void OnPlayerDisconnected(NetworkConnection conn)
    {
        foreach (var kvp in _sessions)
        {
            GameRoomSession session = kvp.Value;
            PlayerObject player = session.Players.FirstOrDefault(p => p.Owner == conn);
            if (player == null) continue;

            int stationIndex = kvp.Key;

            if (session.State == GameRoomState.Waiting ||
                session.State == GameRoomState.Countdown)
            {
                // Cancel countdown if running
                if (session.State == GameRoomState.Countdown)
                    CancelCountdown(stationIndex);
                else
                    RemovePlayerFromSession(session, player, stationIndex);

                if (session.Players.Count == 0)
                    ResetSession(stationIndex);
            }
            else if (session.State == GameRoomState.InProgress)
            {
                // Notify mini game controller of disconnect
                session.ActiveController?.RemovePlayer(player);
                RemovePlayerFromSession(session, player, stationIndex);
            }

            _stations[stationIndex].OnSessionUpdated(session);
            break;
        }
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private void ResetSession(int stationIndex)
    {
        _sessions[stationIndex] = new GameRoomSession(stationIndex, _countdownDuration);
        Debug.Log($"[GameRoomManager] Session reset for station {stationIndex}");
    }

    private string GetSessionId(int stationIndex) => $"station_{stationIndex}";

    private MiniGameController FindMiniGameController(string sceneName)
    {
        // Search loaded scenes for MiniGameController
        for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
            if (scene.name != sceneName) continue;

            foreach (GameObject obj in scene.GetRootGameObjects())
            {
                MiniGameController controller = obj.GetComponentInChildren<MiniGameController>();
                if (controller != null) return controller;
            }
        }
        return null;
    }

    public GameRoomSession GetSession(int stationIndex)
    {
        _sessions.TryGetValue(stationIndex, out GameRoomSession session);
        return session;
    }

    [TargetRpc]
    private void RpcTeleportToPoint(NetworkConnection conn, Vector3 position, Quaternion rotation)
    {
        PlayerObject player = conn.FirstObject?.GetComponent<PlayerObject>();
        if (player == null) return;

        NetworkTransform nt = player.GetComponent<NetworkTransform>();
        if (nt != null) nt.enabled = false;

        player.transform.position = position;
        player.transform.rotation = rotation;

        StartCoroutine(ReenableNetworkTransform(nt));
    }

    [TargetRpc]
    private void RpcTeleportAndUnlock(NetworkConnection conn, Vector3 position, Quaternion rotation)
    {
        Debug.Log($"[GameRoomManager] RpcTeleportAndUnlock — targeting conn: {conn.ClientId}, pos: {position}");

        PlayerObject player = conn.FirstObject?.GetComponent<PlayerObject>();
        if (player == null) return;

        NetworkTransform nt = player.GetComponent<NetworkTransform>();
        if (nt != null) nt.Teleport();

        player.transform.position = position;
        player.transform.rotation = rotation;
        player.Movement.SetMovementLocked(false);
        player.ReinitializeCamera();  // ADD THIS
    }

    [ObserversRpc]
    private void RpcSyncSessionState(int stationIndex, int hostClientId,
        List<string> playerNames, List<int> clientIds, GameRoomState state, bool gameSelected, int minPlayers)
    {
        if (!_stations.TryGetValue(stationIndex, out MinigameStation station)) return;
        station.UpdateSessionState(hostClientId, playerNames, clientIds, state, gameSelected, minPlayers);
    }

    private void SyncSessionToClients(int stationIndex, GameRoomSession session)
    {
        int hostId = session.HostPlayer?.Owner?.ClientId ?? -1;
        List<string> names = session.Players.Select(p => p.name).ToList();
        List<int> clientIds = session.Players.Select(p => p.Owner?.ClientId ?? -1).ToList();
        bool gameSelected = session.SelectedGame != null;
        int minPlayers = session.SelectedGame?.MinPlayers ?? 0;
        RpcSyncSessionState(stationIndex, hostId, names, clientIds, session.State, gameSelected, minPlayers);
    }

    public void NotifyGameComplete(MiniGameController controller, List<RoundResult> results)
    {
        foreach (var kvp in _sessions)
        {
            if (kvp.Value.ActiveController == controller)
            {
                OnGameComplete(kvp.Key, results);
                return;
            }
        }
        Debug.LogWarning("[GameRoomManager] NotifyGameComplete — no matching session found.");
    }

    [TargetRpc]
    private void RpcReinitializeCamera(NetworkConnection conn)
    {
        PlayerObject player = conn.FirstObject?.GetComponent<PlayerObject>();
        if (player == null) return;
        player.ReinitializeCamera();
    }
}