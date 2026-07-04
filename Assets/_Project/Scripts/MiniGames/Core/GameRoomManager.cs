// GameRoomManager.cs

using ChaosPit.Minigames.Jinxed;
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

    private int _playerLayer;

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
        _playerLayer = LayerMask.NameToLayer("Player");

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
        //Debug.Log($"[GameRoomManager] Station {station.StationIndex} registered.");
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
        player.Movement.SetMovementLocked(true, "lobby_session");

        player.Interaction.SetInteractionEnabled(false);

        //Debug.Log($"[GameRoomManager] Player {player.name} joined station {stationIndex}. " +
        //          $"Count: {session.Players.Count}");

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

        bool wasHost = session.HostPlayer == player;

        // If countdown running and this player is leaving — stop it
        if (session.State == GameRoomState.Countdown)
        {
            if (session.CountdownCoroutine != null)
                StopCoroutine(session.CountdownCoroutine);
            session.CountdownCoroutine = null;
        }

        RemovePlayerFromSession(session, player, stationIndex);
        ReturnPlayerToLobby(player);
        RpcForceCloseStationPanel(player.Owner, stationIndex);

        if (session.Players.Count == 0)
        {
            ResetSession(stationIndex);
            if (_stations.TryGetValue(stationIndex, out MinigameStation station))
                SyncSessionToClients(stationIndex, _sessions[stationIndex]);
            return;
        }

        // Return to waiting — new host already set by MigrateHost
        session.State = GameRoomState.Waiting;
        _stations[stationIndex].OnSessionUpdated(session);
        SyncSessionToClients(stationIndex, session);
    }

    [TargetRpc]
    private void RpcForceCloseStationPanel(NetworkConnection conn, int stationIndex)
    {
        if (_stations.TryGetValue(stationIndex, out MinigameStation station))
            station.ForceClosePanel();
    }

    private void OnClientPresenceChangeEnd(ClientPresenceChangeEventArgs args, int stationIndex)
    {
        //Debug.Log($"[GameRoomManager] OnClientPresenceChangeEnd — scene: {args.Scene.name}, " +
        //      $"clientId: {args.Connection.ClientId}, added: {args.Added}, station: {stationIndex}");

        if (!_sessions.TryGetValue(stationIndex, out GameRoomSession session)) return;
        if (session.SelectedGame == null) return;

        //Debug.Log($"[GameRoomManager] OnClientPresenceChangeEnd — scene: {args.Scene.name}, " +
        //  $"clientId: {args.Connection.ClientId}, added: {args.Added}, " +
        //  $"expected: {session.Players.Count}, loaded: {_loadedClients[stationIndex].Count}");

        // Only care about our scene
        if (args.Scene.name != session.SelectedGame.SceneName) return;
        // Only care about additions not removals
        if (!args.Added) return;

        if (!_loadedClients.ContainsKey(stationIndex))
            _loadedClients[stationIndex] = new HashSet<int>();

        _loadedClients[stationIndex].Add(args.Connection.ClientId);

        int expected = session.Players.Count;
        int loaded = _loadedClients[stationIndex].Count;

        //Debug.Log($"[GameRoomManager] OnClientPresenceChangeEnd — scene: {args.Scene.name}, " +
        //  $"clientId: {args.Connection.ClientId}, added: {args.Added}, " +
        //  $"expected: {expected}, loaded: {loaded}, " +
        //  $"playerIds: {string.Join(",", session.Players.Select(p => p.Owner?.ClientId))}");

        if (loaded < expected) return;

        // All clients loaded — unsubscribe and start
        InstanceFinder.NetworkManager.SceneManager.OnClientPresenceChangeEnd -=
            (a) => OnClientPresenceChangeEnd(a, stationIndex);

        _loadedClients.Remove(stationIndex);
        StartCoroutine(StartGameAfterLoad(stationIndex));
    }

    private IEnumerator StartGameAfterLoad(int stationIndex)
    {
        //Debug.Log($"[GameRoomManager] StartGameAfterLoad — stationIndex: {stationIndex}");

        // One frame buffer after all clients confirm loaded
        yield return new WaitForSeconds(0.5f);

        if (!_sessions.TryGetValue(stationIndex, out GameRoomSession session)) yield break;

        session.State = GameRoomState.InProgress;
        SyncSessionToClients(stationIndex, session);

        MiniGameController controller = FindActiveMinigameController();
        if (controller == null)
        {
            Debug.LogError($"[GameRoomManager] No MiniGameController found in {session.SelectedGame.SceneName}");
            yield break;
        }

        session.ActiveController = controller;
        controller.StartGame(session.Players);

        foreach (PlayerObject player in session.Players)
            RpcInitMinigame(player.Owner);

        // Teleport each player to their spawn point on their client
        for (int i = 0; i < session.Players.Count; i++)
        {
            PlayerObject player = session.Players[i];
            Transform[] spawns = controller.SpawnPoints;
            if (spawns != null && spawns.Length > 0)
            {
                int spawnIndex = i % spawns.Length;
                Vector3 pos = spawns[spawnIndex].position;
                Quaternion rot = spawns[spawnIndex].rotation;

                NetworkTransform nt = player.GetComponent<NetworkTransform>();
                if (nt != null) nt.Teleport();

                player.transform.position = pos;
                player.transform.rotation = rot;
                RpcTeleportToPoint(player.Owner, pos, rot);
            }
            RpcReinitializeCamera(player.Owner);
            RpcUnlockPlayer(player.Owner);  // ADD
        }

        //Debug.Log($"[GameRoomManager] Game started for station {stationIndex}");
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
        //Debug.Log($"[GameRoomManager] Station {stationIndex} selected game: {entry.MiniGameName}");
        _stations[stationIndex].OnSessionUpdated(session);
        SyncSessionToClients(stationIndex, session);
    }

    [ServerRpc(RequireOwnership = false)]
    public void RequestStartCountdown(int stationIndex, PlayerObject requestingPlayer)
    {
        if (!_sessions.TryGetValue(stationIndex, out GameRoomSession session)) return;
        if (session.State != GameRoomState.Waiting) return;
        if (session.HostPlayer?.Owner?.ClientId != requestingPlayer?.Owner?.ClientId) return;
        if (session.SelectedGame == null) return;
        if (session.Players.Count < session.SelectedGame.MinPlayers) return;

        session.State = GameRoomState.Countdown;
        session.CountdownCoroutine = StartCoroutine(CountdownCoroutine(stationIndex));

        //Debug.Log($"[GameRoomManager] Countdown started for station {stationIndex}");
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
        {
            SetPlayerLayer(player, _gameRoomLayer);
            RpcSetPlayerLayerObservers(player.GetComponent<NetworkObject>(), _gameRoomLayer);
            Debug.Log($"[GameRoomManager] SetPlayerLayer — player: {player.name}, layer: {_gameRoomLayer}");
            RpcSetLobbyCanvasVisible(player.Owner, false);
        }

        // Subscribe BEFORE loading
        InstanceFinder.NetworkManager.SceneManager.OnClientPresenceChangeEnd +=
         (args) => OnClientPresenceChangeEnd(args, stationIndex);

        SceneLoadData sld = new SceneLoadData(session.SelectedGame.SceneName)
        {
            ReplaceScenes = ReplaceOption.None,
            Options = new LoadOptions { AllowStacking = true }
        };

        InstanceFinder.NetworkManager.SceneManager.LoadConnectionScenes(connections, sld);

        //Debug.Log($"[GameRoomManager] Loading scene {session.SelectedGame.SceneName} " +
        //          $"for station {stationIndex}");
    }

    [ObserversRpc]
    private void RpcSetPlayerLayerObservers(NetworkObject playerNetObj, int layer)
    {
        //Debug.Log($"[GameRoomManager] RpcSetPlayerLayerObservers — target: {playerNetObj?.name ?? "null"}, " +
        //      $"ownerClientId: {playerNetObj?.Owner?.ClientId ?? -1}, layer: {layer}, " +
        //      $"IsServer: {IsServerInitialized}, IsClient: {IsClientInitialized}");

        if (playerNetObj == null) return;
        playerNetObj.gameObject.layer = layer;
        foreach (Transform child in playerNetObj.GetComponentsInChildren<Transform>())
            child.gameObject.layer = layer;

       // Debug.Log($"[GameRoomManager] RpcSetPlayerLayerObservers — applied. New layer on {playerNetObj.name}: {playerNetObj.gameObject.layer}");
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

        // Build results data for display
        ResultsData data = BuildResultsData(sessionId, results);

        // Tell controller to show results screen
        session.ActiveController?.ShowResults(data);

        Debug.Log($"[GameRoomManager] Game complete for station {stationIndex} — showing results");
    }

    private ResultsData BuildResultsData(string sessionId, List<RoundResult> results)
    {
        ResultsData data = new ResultsData { SessionId = sessionId };

        foreach (RoundResult result in results)
        {
            if (result.Player == null) continue;

            PlayerProfile profile = PlayerProfileManager.Instance.GetProfile(result.Player.Owner);
            string displayName = profile?.DisplayName ?? result.Player.name;
            int careerScore = profile?.CareerScore ?? 0;

            data.Entries.Add(new PlayerResultEntry
            {
                DisplayName = displayName,
                Standing = result.Standing,
                ResultLabel = result.ResultLabel,
                PointsEarned = result.ScoreAwarded,
                CareerScore = careerScore,
                CareerLevel = PlayerResultEntry.CalculateLevel(careerScore)
            });
        }

        // Sort by standing
        data.Entries.Sort((a, b) => a.Standing.CompareTo(b.Standing));
        return data;
    }

    [TargetRpc]
    public void RpcSetLocalPlayerName(NetworkConnection conn, string name)
    {
        LobbyUIManager ui = FindFirstObjectByType<LobbyUIManager>(FindObjectsInactive.Include);
        ui?.SetPlayerName(name);
    }

    // Called by MiniGameController when results timer expires
    public void OnResultsDismissed(MiniGameController controller)
    {
        foreach (var kvp in _sessions)
        {
            if (kvp.Value.ActiveController == controller)
            {
                ReturnToLobby(kvp.Key);
                return;
            }
        }
        Debug.LogWarning("[GameRoomManager] OnResultsDismissed — no matching session found.");
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
        {
            ReturnPlayerToLobby(player);
        }

        // Refresh lobby leaderboard
        GameRoomManager.Instance?.SyncLeaderboardToClients();

        // Clear score session
        ScoreManager.Instance.UnregisterSession(sessionId);

        ResetSession(stationIndex);

        // Sync idle state to clients so UI resets
        if (_stations.TryGetValue(stationIndex, out MinigameStation station))
            SyncSessionToClients(stationIndex, _sessions[stationIndex]);

        //Debug.Log($"[GameRoomManager] Players returned to lobby from station {stationIndex}");
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
        Debug.Log($"[GameRoomManager] ReturnPlayerToLobby — player: {player.name}, " +
          $"interactionEnabled: {player.Interaction.enabled}");

        SetPlayerLayer(player, _playerLayer);
        RpcSetPlayerLayerObservers(player.GetComponent<NetworkObject>(), 0);
        player.Movement.ClearAllMovementLocks();
        RpcClearMovementLocks(player.Owner);
        player.Interaction.SetInteractionEnabled(true);

        if (LobbySpawner.Instance != null &&
            LobbySpawner.Instance.TryGetSpawnPoint(out Vector3 pos, out Quaternion rot))
        {
            NetworkTransform nt = player.GetComponent<NetworkTransform>();
            if (nt != null) nt.Teleport();
            else
                player.transform.position = pos;

            RpcTeleportAndUnlock(player.Owner, pos, rot);

            // Host is both server and client — TargetRpc may not fire for host owner
            // Call unlock directly for the host player
            if (player.IsOwner)
            {
                player.transform.position = pos;
                player.transform.rotation = rot;
                player.Movement.SetMovementLocked(false, "jinxed_round");
                player.Interaction.SetInteractionEnabled(true);
                player.Interaction.enabled = true;
                player.ReinitializeCamera();

                LobbyUIManager ui = FindFirstObjectByType<LobbyUIManager>(FindObjectsInactive.Include);
                if (ui != null) ui.SetVisible(true);
            }
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

        // Sync new host to all clients so UI updates correctly
        SyncSessionToClients(stationIndex, session);
    }

    private void SetPlayerLayer(PlayerObject player, int layer)
    {
        player.gameObject.layer = layer;
        foreach (Transform child in player.GetComponentsInChildren<Transform>())
            child.gameObject.layer = layer;

        RpcSyncPlayerLayer(player.NetworkObject, layer);
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

            if (session.State == GameRoomState.Waiting)
            {
                RemovePlayerFromSession(session, player, stationIndex);

                if (session.Players.Count == 0)
                    ResetSession(stationIndex);
                else
                    SyncSessionToClients(stationIndex, session);

                _stations[stationIndex].OnSessionUpdated(session);
            }
            else if (session.State == GameRoomState.Countdown)
            {
                bool wasHost = session.HostPlayer == player;

                // Stop countdown
                if (session.CountdownCoroutine != null)
                    StopCoroutine(session.CountdownCoroutine);
                session.CountdownCoroutine = null;

                RemovePlayerFromSession(session, player, stationIndex);

                if (session.Players.Count == 0)
                {
                    ResetSession(stationIndex);
                }
                else
                {
                    // Migrate host if needed — already done in RemovePlayerFromSession
                    session.State = GameRoomState.Waiting;
                    SyncSessionToClients(stationIndex, session);
                    _stations[stationIndex].OnSessionUpdated(session);
                    Debug.Log($"[GameRoomManager] Countdown cancelled due to disconnect — " +
                              $"migrated to waiting, new host: {session.HostPlayer?.name}");
                }
            }
            else if (session.State == GameRoomState.InProgress ||
                     session.State == GameRoomState.Results)
            {
                bool wasHost = session.HostPlayer == player;

                session.ActiveController?.RemovePlayer(player);
                RemovePlayerFromSession(session, player, stationIndex);

                if (session.Players.Count == 0)
                {
                    // No players left — clean up
                    ReturnToLobby(stationIndex);
                }
                else if (wasHost)
                {
                    // Try to migrate host
                    if (session.HostPlayer != null)
                    {
                        Debug.Log($"[GameRoomManager] Host disconnected during game — " +
                                  $"migrated to {session.HostPlayer.name}");
                        SyncSessionToClients(stationIndex, session);
                    }
                    else
                    {
                        // Migration failed — return everyone to lobby with no points
                        Debug.Log("[GameRoomManager] Host migration failed — returning to lobby.");
                        ReturnToLobbyNoPoints(stationIndex);
                    }
                }
            }

            break;
        }
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private void ResetSession(int stationIndex)
    {
        _sessions[stationIndex] = new GameRoomSession(stationIndex, _countdownDuration);
        //Debug.Log($"[GameRoomManager] Session reset for station {stationIndex}");
    }

    private string GetSessionId(int stationIndex) => $"station_{stationIndex}";

    public GameRoomSession GetSession(int stationIndex)
    {
        _sessions.TryGetValue(stationIndex, out GameRoomSession session);
        return session;
    }

    [TargetRpc]
    private void RpcTeleportToPoint(NetworkConnection conn, Vector3 position, Quaternion rotation)
    {
        //Debug.Log($"[GameRoomManager] RpcTeleportToPoint — clientId: {conn.ClientId}, pos: {position}");

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
        //Debug.Log($"[GameRoomManager] RpcTeleportAndUnlock — targeting conn: {conn.ClientId}, pos: {position}");

        PlayerObject player = conn.FirstObject?.GetComponent<PlayerObject>();
        if (player == null) return;

        NetworkTransform nt = player.GetComponent<NetworkTransform>();
        if (nt != null) nt.Teleport();

        Debug.Log($"[GameRoomManager] RpcTeleportAndUnlock — clientId: {conn.ClientId}, " +
          $"interactionEnabled: {player.Interaction.enabled}");

        player.transform.position = position;
        player.transform.rotation = rotation;
        player.Movement.SetMovementLocked(false, "lobby_session");
        player.Interaction.SetInteractionEnabled(true);
        player.Interaction.enabled = true;
        player.ReinitializeCamera();

        LobbyUIManager ui = FindFirstObjectByType<LobbyUIManager>(FindObjectsInactive.Include);
        Debug.Log($"[GameRoomManager] RpcTeleportAndUnlock — LobbyUIManager found: {ui != null}, active: {ui?.gameObject.activeSelf}");
        if (ui != null) ui.SetVisible(true);
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
        List<string> names = session.Players
            .Select(p => PlayerProfileManager.Instance.GetProfile(p.Owner)?.DisplayName ?? p.name)
            .ToList();
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

    [TargetRpc]
    private void RpcUnlockPlayer(NetworkConnection conn)
    {
        PlayerObject player = conn.FirstObject?.GetComponent<PlayerObject>();
        if (player == null) return;
        player.Movement.ClearAllMovementLocks();
        player.Interaction.SetInteractionEnabled(false);
    }

    [Server]
    private void ReturnToLobbyNoPoints(int stationIndex)
    {
        if (!_sessions.TryGetValue(stationIndex, out GameRoomSession session)) return;

        session.State = GameRoomState.Returning;
        SyncSessionToClients(stationIndex, session);

        session.ActiveController?.CleanUp();

        NetworkConnection[] connections = session.Players
            .Select(p => p.Owner)
            .Where(c => c != null)
            .ToArray();

        if (session.SelectedGame != null)
        {
            SceneUnloadData sud = new SceneUnloadData(session.SelectedGame.SceneName);
            InstanceFinder.NetworkManager.SceneManager.UnloadConnectionScenes(connections, sud);
        }

        foreach (PlayerObject player in session.Players.ToList())
            ReturnPlayerToLobby(player);

        string sessionId = GetSessionId(stationIndex);
        ScoreManager.Instance.UnregisterSession(sessionId);

        ResetSession(stationIndex);

        if (_stations.TryGetValue(stationIndex, out MinigameStation station))
            SyncSessionToClients(stationIndex, _sessions[stationIndex]);

        Debug.Log($"[GameRoomManager] ReturnToLobbyNoPoints — station {stationIndex}");
    }

    public void SyncLeaderboardToClients()
    {
        //Debug.Log($"[GameRoomManager] SyncLeaderboardToClients — profiles: {PlayerProfileManager.Instance.GetAllProfiles().Count}");
        var careerEntries = ScoreManager.Instance.GetCareerLeaderboard();

        // Build aggregate session scores across all active sessions
        var sessionEntries = new List<SessionLeaderboardEntry>();
        var sessionScoreMap = new Dictionary<int, int>();

        foreach (var kvp in _sessions)
        {
            string sessionId = GetSessionId(kvp.Key);
            var entries = ScoreManager.Instance.GetSessionLeaderboard(sessionId);
            foreach (var entry in entries)
            {
                if (!sessionScoreMap.ContainsKey(entry.ClientId))
                    sessionScoreMap[entry.ClientId] = 0;
                sessionScoreMap[entry.ClientId] += entry.SessionScore;
            }
        }

        // Also include all connected players with 0 session score if not already listed
        foreach (var profile in PlayerProfileManager.Instance.GetAllProfiles())
        {
            if (!sessionScoreMap.ContainsKey(profile.ClientId))
                sessionScoreMap[profile.ClientId] = 0;
        }

        // Build sorted list
        var sorted = sessionScoreMap
            .OrderByDescending(kvp => kvp.Value)
            .ToList();

        for (int i = 0; i < sorted.Count; i++)
        {
            PlayerProfile profile = PlayerProfileManager.Instance.GetProfile(sorted[i].Key);
            sessionEntries.Add(new SessionLeaderboardEntry
            {
                ClientId = sorted[i].Key,
                DisplayName = profile?.DisplayName ?? $"Player_{sorted[i].Key}",
                SessionScore = sorted[i].Value,
                CareerScore = profile?.CareerScore ?? 0,
                Standing = i + 1
            });
        }

        RpcSyncLeaderboard(careerEntries, sessionEntries);
    }

    [ObserversRpc]
    private void RpcSyncLeaderboard(List<SessionLeaderboardEntry> careerEntries,
                                     List<SessionLeaderboardEntry> sessionEntries)
    {
        LeaderboardManager.Instance?.SetCachedData(careerEntries, sessionEntries);
        LeaderboardManager.Instance?.Refresh();
    }

    /// Generic RPC for any minigame to send data to all clients.
    /// Called by MiniGameController subclasses on the server.
    /// Clients find the active controller and dispatch the message to it.
    [ObserversRpc]
    public void RpcMinigameMessage(string messageType, string payload)
    {
        //Debug.Log($"[GameRoomManager] RpcMinigameMessage SENT/RECEIVED — type: {messageType}, IsServer: {IsServerInitialized}, IsClient: {IsClientInitialized}");

        if (IsServerInitialized && !IsClientInitialized) return;
        MiniGameController controller = FindActiveMinigameController();
        controller?.OnNetworkMessage(messageType, payload);
    }

    /// Finds the active MiniGameController across all loaded scenes.
    /// Reuses the same scene-search pattern already in GameRoomManager.
    private MiniGameController FindActiveMinigameController()
    {
        for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
            foreach (GameObject obj in scene.GetRootGameObjects())
            {
                MiniGameController ctrl = obj.GetComponentInChildren<MiniGameController>();
                if (ctrl != null) return ctrl;
            }
        }
        return null;
    }

    [TargetRpc]
    private void RpcSetLobbyCanvasVisible(NetworkConnection conn, bool visible)
    {
        LobbyUIManager ui = FindFirstObjectByType<LobbyUIManager>();
        if (ui != null) ui.SetVisible(visible);
    }

    [TargetRpc]
    private void RpcInitMinigame(NetworkConnection conn)
    {
        MiniGameController controller = FindActiveMinigameController();
        controller?.ClientInit();
    }

    [ServerRpc(RequireOwnership = false)]
    public void RequestMinigameAction(string messageType, string payload, NetworkConnection sender = null)
    {
        MiniGameController controller = FindActiveMinigameController();
        controller?.OnClientAction(messageType, payload, sender);
    }
    public void TeleportPlayer(NetworkConnection conn, Vector3 pos, Quaternion rot)
    {
        RpcTeleportToPoint(conn, pos, rot);
    }

    [TargetRpc]
    public void SetPlayerTagMode(NetworkConnection conn, bool active)
    {
        PlayerObject player = conn.FirstObject?.GetComponent<PlayerObject>();
        if (player == null) return;

        JinxedHUD hud = FindFirstObjectByType<JinxedHUD>(FindObjectsInactive.Include);
        player.Interaction.SetJinxedTagActive(active, active ? hud : null);
    }

    [TargetRpc]
    public void SetPlayerMovementLocked(NetworkConnection conn, bool locked, string source)
    {
        PlayerObject player = conn.FirstObject?.GetComponent<PlayerObject>();
        if (player == null) return;
        player.Movement.SetMovementLocked(locked, source);
    }

    [ObserversRpc]
    private void RpcSyncPlayerLayer(NetworkObject playerNetObj, int layer)
    {
        if (playerNetObj == null) return;
        PlayerObject player = playerNetObj.GetComponent<PlayerObject>();
        if (player == null) return;

        player.gameObject.layer = layer;
        foreach (Transform child in player.GetComponentsInChildren<Transform>())
            child.gameObject.layer = layer;
    }

    [TargetRpc]
    public void RpcClearMovementLocks(NetworkConnection conn)
    {
        PlayerObject player = conn.FirstObject?.GetComponent<PlayerObject>();
        if (player == null) return;
        player.Movement.ClearAllMovementLocks();
    }

}