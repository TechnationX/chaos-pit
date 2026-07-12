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

    private Dictionary<int, MinigameStation> _stations = new Dictionary<int, MinigameStation>();
    private Dictionary<int, GameRoomSession> _sessions = new Dictionary<int, GameRoomSession>();

    private int _gameRoomLayer;
    private int _playerLayer;
    private int _sessionToken = 0;
    private int _clientSessionToken = 0;

    [SerializeField] private MiniGameRegistry _registry;

    private static List<MinigameStation> _pendingStations = new List<MinigameStation>();
    private Dictionary<int, HashSet<int>> _loadedClients = new Dictionary<int, HashSet<int>>();
    private Dictionary<int, int> _unloadedClientCounts = new Dictionary<int, int>();
    private Dictionary<int, System.Action<ClientPresenceChangeEventArgs>> _unloadListeners
        = new Dictionary<int, System.Action<ClientPresenceChangeEventArgs>>();

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        _gameRoomLayer = LayerMask.NameToLayer("GameRoom");
        _playerLayer = LayerMask.NameToLayer("Player");

        foreach (var station in _pendingStations)
            RegisterStation(station);
        _pendingStations.Clear();
    }

    // ─── Station Registration ─────────────────────────────────────────────────

    public static void RequestRegistration(MinigameStation station)
    {
        if (Instance != null) Instance.RegisterStation(station);
        else _pendingStations.Add(station);
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

        if (session.Players.Count == 1)
            session.HostPlayer = player;

        session.State = GameRoomState.Waiting;

        TeleportToWaitingArea(stationIndex, player);
        player.Movement.SetMovementLocked(true, "lobby_session");
        SetPlayerMovementLocked(player.Owner, player.NetworkObject, true, "lobby_session");
        player.Interaction.SetInteractionEnabled(false);
        RpcDisableInteraction(player.Owner, player.NetworkObject);

        _stations[stationIndex].OnSessionUpdated(session);
        SyncSessionToClients(stationIndex, session);
    }

    [ServerRpc(RequireOwnership = false)]
    public void RequestLeave(int stationIndex, PlayerObject player)
    {
        if (!_sessions.TryGetValue(stationIndex, out GameRoomSession session)) return;
        if (!session.Players.Contains(player)) return;

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
        if (!_sessions.TryGetValue(stationIndex, out GameRoomSession session)) return;
        if (session.SelectedGame == null) return;
        if (args.Scene.name != session.SelectedGame.SceneName) return;
        if (!args.Added) return;

        if (!_loadedClients.ContainsKey(stationIndex))
            _loadedClients[stationIndex] = new HashSet<int>();

        _loadedClients[stationIndex].Add(args.Connection.ClientId);

        int expected = session.Players.Count;
        int loaded = _loadedClients[stationIndex].Count;

        if (loaded < expected) return;

        InstanceFinder.NetworkManager.SceneManager.OnClientPresenceChangeEnd -=
            (a) => OnClientPresenceChangeEnd(a, stationIndex);

        _loadedClients.Remove(stationIndex);
        StartCoroutine(StartGameAfterLoad(stationIndex));
    }

    private IEnumerator StartGameAfterLoad(int stationIndex)
    {
        _sessionToken++;
        int token = _sessionToken;
        RpcSyncSessionToken(_sessionToken);

        //yield return new WaitForSeconds(0.5f);

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
            RpcInitMinigame(player.Owner, player.NetworkObject);

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
                RpcTeleportToPoint(player.Owner, player.NetworkObject, pos, rot);
            }
            RpcReinitializeCamera(player.Owner, player.NetworkObject);
            RpcUnlockPlayer(player.Owner, player.NetworkObject, token);
        }

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
        _stations[stationIndex].OnSessionUpdated(session);
    }

    [ServerRpc(RequireOwnership = false)]
    public void CancelCountdown(int stationIndex)
    {
        if (!_sessions.TryGetValue(stationIndex, out GameRoomSession session)) return;
        if (session.State != GameRoomState.Countdown) return;

        if (session.CountdownCoroutine != null)
            StopCoroutine(session.CountdownCoroutine);

        foreach (PlayerObject player in session.Players.ToList())
            ReturnPlayerToLobby(player);

        ResetSession(stationIndex);
        _stations[stationIndex].OnSessionUpdated(session);
        SyncSessionToClients(stationIndex, session);
    }

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
        if (!_sessions.TryGetValue(stationIndex, out GameRoomSession session))
        {
            Debug.LogError("[GameRoomManager] BeginTransition — session not found.");
            return;
        }

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
            RpcSetLobbyCanvasVisible(player.Owner, false);
        }

        InstanceFinder.NetworkManager.SceneManager.OnClientPresenceChangeEnd +=
            (args) => OnClientPresenceChangeEnd(args, stationIndex);

        SceneLoadData sld = new SceneLoadData(session.SelectedGame.SceneName)
        {
            ReplaceScenes = ReplaceOption.None,
            Options = new LoadOptions { AllowStacking = true }
        };

        InstanceFinder.NetworkManager.SceneManager.LoadConnectionScenes(connections, sld);
    }

    [ObserversRpc]
    private void RpcSetPlayerLayerObservers(NetworkObject playerNetObj, int layer)
    {
        if (playerNetObj == null) return;
        playerNetObj.gameObject.layer = layer;
        foreach (Transform child in playerNetObj.GetComponentsInChildren<Transform>())
            child.gameObject.layer = layer;
    }

    // ─── Results ──────────────────────────────────────────────────────────────

    [Server]
    public void OnGameComplete(int stationIndex, List<RoundResult> results)
    {
        if (!_sessions.TryGetValue(stationIndex, out GameRoomSession session)) return;

        session.State = GameRoomState.Results;
        string sessionId = GetSessionId(stationIndex);

        ScoreManager.Instance.SubmitResults(sessionId, results);
        ResultsData data = BuildResultsData(sessionId, results);
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

        data.Entries.Sort((a, b) => a.Standing.CompareTo(b.Standing));
        return data;
    }

    [TargetRpc]
    public void RpcSetLocalPlayerName(NetworkConnection conn, string name)
    {
        LobbyUIManager ui = FindFirstObjectByType<LobbyUIManager>(FindObjectsInactive.Include);
        ui?.SetPlayerName(name);
    }

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
    private void ReturnToLobby(int stationIndex)
    {
        if (!_sessions.TryGetValue(stationIndex, out GameRoomSession session)) return;

        _sessionToken++;
        RpcSyncSessionToken(_sessionToken);

        session.State = GameRoomState.Returning;
        SyncSessionToClients(stationIndex, session);

        string sessionId = GetSessionId(stationIndex);
        session.ActiveController?.CleanUp();

        NetworkConnection[] connections = session.Players
            .Select(p => p.Owner)
            .Where(c => c != null)
            .ToArray();

        List<PlayerObject> players = session.Players.ToList();
        string sceneName = session.SelectedGame.SceneName;

        _unloadedClientCounts[stationIndex] = 0;

        void listener(ClientPresenceChangeEventArgs args) =>
            OnMinigameSceneUnloaded(args, players, sceneName, sessionId, stationIndex);

        _unloadListeners[stationIndex] = listener;
        InstanceFinder.NetworkManager.SceneManager.OnClientPresenceChangeEnd += listener;

        SceneUnloadData sud = new SceneUnloadData(sceneName);
        InstanceFinder.NetworkManager.SceneManager.UnloadConnectionScenes(connections, sud);
    }

    private void OnMinigameSceneUnloaded(ClientPresenceChangeEventArgs args, List<PlayerObject> players,
        string sceneName, string sessionId, int stationIndex)
    {
        if (args.Scene.name != sceneName) return;
        if (args.Added) return;

        if (!_unloadedClientCounts.ContainsKey(stationIndex))
            _unloadedClientCounts[stationIndex] = 0;

        _unloadedClientCounts[stationIndex]++;

        Debug.Log($"[GameRoomManager] Scene unload progress — {_unloadedClientCounts[stationIndex]}/{players.Count}");

        if (_unloadedClientCounts[stationIndex] < players.Count) return;

        // All players unloaded — clean up listener
        _unloadedClientCounts.Remove(stationIndex);

        if (_unloadListeners.TryGetValue(stationIndex, out var storedListener))
        {
            InstanceFinder.NetworkManager.SceneManager.OnClientPresenceChangeEnd -= storedListener;
            _unloadListeners.Remove(stationIndex);
        }

        StartCoroutine(ReturnPlayersDelayed(players, sessionId, stationIndex));
    }

    private IEnumerator ReturnPlayersDelayed(List<PlayerObject> players, string sessionId, int stationIndex)
    {
        yield return new WaitForSeconds(0.3f);

        foreach (PlayerObject player in players)
            ReturnPlayerToLobby(player);

        GameRoomManager.Instance?.SyncLeaderboardToClients();
        ScoreManager.Instance.UnregisterSession(sessionId);
        ResetSession(stationIndex);

        if (_stations.TryGetValue(stationIndex, out MinigameStation station))
            SyncSessionToClients(stationIndex, _sessions[stationIndex]);
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
        else player.transform.position = pos;

        RpcTeleportToPoint(player.Owner, player.NetworkObject, pos, rot);
    }

    private void ReturnPlayerToLobby(PlayerObject player)
    {
        SetPlayerLayer(player, _playerLayer);
        //RpcSetPlayerLayerObservers(player.GetComponent<NetworkObject>(), 0);
        player.Movement.ClearAllMovementLocks();
        RpcClearMovementLocks(player.Owner, player.NetworkObject);
        player.Interaction.SetInteractionEnabled(true);

        if (LobbySpawner.Instance != null &&
            LobbySpawner.Instance.TryGetReturnSpawnPoint(out Vector3 pos, out Quaternion rot))
        {
            NetworkTransform nt = player.GetComponent<NetworkTransform>();
            if (nt != null) nt.Teleport();
            else player.transform.position = pos;

            RpcTeleportAndUnlockPlayer(player.Owner, player.NetworkObject, pos, rot);
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
        if (session.HostPlayer == player)
            MigrateHost(session, stationIndex);
    }

    private void MigrateHost(GameRoomSession session, int stationIndex)
    {
        if (session.Players.Count == 0) { session.HostPlayer = null; return; }

        session.HostPlayer = session.Players[0];
        Debug.Log($"[GameRoomManager] Host migrated to {session.HostPlayer.name} on station {stationIndex}");
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

    public void HandlePlayerDisconnected(NetworkConnection conn)
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
                if (session.Players.Count == 0) ResetSession(stationIndex);
                else SyncSessionToClients(stationIndex, session);
                _stations[stationIndex].OnSessionUpdated(session);
            }
            else if (session.State == GameRoomState.Countdown)
            {
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
                    session.State = GameRoomState.Waiting;
                    SyncSessionToClients(stationIndex, session);
                    _stations[stationIndex].OnSessionUpdated(session);
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
                    ReturnToLobby(stationIndex);
                }
                else if (wasHost)
                {
                    if (session.HostPlayer != null)
                        SyncSessionToClients(stationIndex, session);
                    else
                        ReturnToLobbyNoPoints(stationIndex);
                }
            }

            break;
        }
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private void ResetSession(int stationIndex)
    {
        _sessions[stationIndex] = new GameRoomSession(stationIndex, _countdownDuration);
    }

    private string GetSessionId(int stationIndex) => $"station_{stationIndex}";

    public GameRoomSession GetSession(int stationIndex)
    {
        _sessions.TryGetValue(stationIndex, out GameRoomSession session);
        return session;
    }

    // ─── RPCs ─────────────────────────────────────────────────────────────────
    // All TargetRpcs pass NetworkObject directly to avoid conn.FirstObject
    // resolving to the wrong player on the host.

    [TargetRpc]
    private void RpcTeleportToPoint(NetworkConnection conn, NetworkObject playerNetObj, Vector3 position, Quaternion rotation)
    {
        if (playerNetObj == null) return;
        PlayerObject player = playerNetObj.GetComponent<PlayerObject>();
        if (player == null) return;

        NetworkTransform nt = player.GetComponent<NetworkTransform>();
        if (nt != null) nt.enabled = false;
        player.transform.position = position;
        player.transform.rotation = rotation;
        StartCoroutine(ReenableNetworkTransform(nt));
    }

    [TargetRpc]
    private void RpcTeleportAndUnlockPlayer(NetworkConnection conn, NetworkObject playerNetObj, Vector3 position, Quaternion rotation)
    {
        if (playerNetObj == null) return;
        PlayerObject player = playerNetObj.GetComponent<PlayerObject>();
        if (player == null) return;

        NetworkTransform nt = player.GetComponent<NetworkTransform>();
        if (nt != null) nt.Teleport();

        player.transform.position = position;
        player.transform.rotation = rotation;
        player.Movement.ClearAllMovementLocks();

        //Debug.Log($"[GameRoomManager] RpcTeleportAndUnlockPlayer — enabling interaction, frame: {Time.frameCount}");
        player.Interaction.SetInteractionEnabled(true);
        player.Interaction.enabled = true;
        player.Interaction.SetJinxedTagActive(false);
        player.Interaction.SetBombPassActive(false);
        player.Interaction.SetThiefsMarketPunchActive(false);
        player.Interaction.RestartUpdateLoop();
        //Debug.Log($"[GameRoomManager] Player scene: {player.gameObject.scene.name}, " +
        //  $"interaction scene: {player.Interaction.gameObject.scene.name}");

        player.ReinitializeCamera();
        player.Movement.SetStaminaLimited(false);

        LobbyUIManager ui = FindFirstObjectByType<LobbyUIManager>(FindObjectsInactive.Include);
        if (ui != null) ui.SetVisible(true);

        //Debug.Log($"[GameRoomManager] RpcTeleportAndUnlockPlayer DONE — " +
        //  $"interactionEnabled: {player.Interaction.enabled}, " +
        //  $"componentEnabled: {player.GetComponent<InteractionManager>()?.enabled}");
    }

    [TargetRpc]
    private void RpcUnlockPlayer(NetworkConnection conn, NetworkObject playerNetObj, int token)
    {
        if (playerNetObj == null) return;
        PlayerObject player = playerNetObj.GetComponent<PlayerObject>();
        if (player == null) return;

        if (token != _clientSessionToken)
        {
            Debug.Log($"[GameRoomManager] RpcUnlockPlayer — stale token {token} vs {_sessionToken}, ignoring");
            return;
        }

        player.Movement.ClearAllMovementLocks();

        //Debug.Log($"[GameRoomManager] RpcUnlockPlayer — disabling interaction, frame: {Time.frameCount}");
        player.Interaction.SetInteractionEnabled(false);
        player.Movement.SetStaminaLimited(true);
    }

    [TargetRpc]
    private void RpcReinitializeCamera(NetworkConnection conn, NetworkObject playerNetObj)
    {
        if (playerNetObj == null) return;
        PlayerObject player = playerNetObj.GetComponent<PlayerObject>();
        if (player == null) return;
        player.ReinitializeCamera();
    }

    [TargetRpc]
    private void RpcInitMinigame(NetworkConnection conn, NetworkObject playerNetObj)
    {
        MiniGameController controller = FindActiveMinigameController();
        controller?.ClientInit();
    }

    [TargetRpc]
    private void RpcSetLobbyCanvasVisible(NetworkConnection conn, bool visible)
    {
        LobbyUIManager ui = FindFirstObjectByType<LobbyUIManager>();
        if (ui != null) ui.SetVisible(visible);
    }

    [TargetRpc]
    public void SetPlayerTagMode(NetworkConnection conn, NetworkObject playerNetObj, bool active)
    {
        if (playerNetObj == null) return;
        PlayerObject player = playerNetObj.GetComponent<PlayerObject>();
        if (player == null) return;
        player.Interaction.SetJinxedTagActive(active);
    }

    [TargetRpc]
    public void SetPlayerThiefsMarketPunchMode(NetworkConnection conn, NetworkObject playerNetObj, bool active)
    {
        //Debug.Log($"[TM-DEBUG] TargetRpc received on this client — active: {active}, playerNetObj null: {playerNetObj == null}");
        if (playerNetObj == null) return;
        PlayerObject player = playerNetObj.GetComponent<PlayerObject>();
        //Debug.Log($"[TM-DEBUG] Resolved player: {player?.name}, IsOwner: {player?.IsOwner}");
        if (player == null) return;
        player.Interaction.SetThiefsMarketPunchActive(active);
    } 

    [TargetRpc]
    public void SetPlayerMovementLocked(NetworkConnection conn, NetworkObject playerNetObj, bool locked, string source)
    {
        if (playerNetObj == null) return;
        PlayerObject player = playerNetObj.GetComponent<PlayerObject>();
        if (player == null) return;
        player.Movement.SetMovementLocked(locked, source);
    }

    [TargetRpc]
    public void RpcClearMovementLocks(NetworkConnection conn, NetworkObject playerNetObj)
    {
        if (playerNetObj == null) return;
        PlayerObject player = playerNetObj.GetComponent<PlayerObject>();
        if (player == null) return;
        player.Movement.ClearAllMovementLocks();
    }

    [TargetRpc]
    private void RpcDisableInteraction(NetworkConnection conn, NetworkObject playerNetObj)
    {
        if (playerNetObj == null) return;
        PlayerObject player = playerNetObj.GetComponent<PlayerObject>();
        if (player == null) return;
        player.Interaction.SetInteractionEnabled(false);
    }

    [ObserversRpc]
    private void RpcSyncSessionToken(int token)
    {
        _clientSessionToken = token;
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

    [ObserversRpc]
    private void RpcSyncLeaderboard(List<SessionLeaderboardEntry> careerEntries,
                                     List<SessionLeaderboardEntry> sessionEntries)
    {
        LeaderboardManager.Instance?.SetCachedData(careerEntries, sessionEntries);
        LeaderboardManager.Instance?.Refresh();
    }

    [ObserversRpc]
    public void RpcMinigameMessage(string messageType, string payload)
    {
        if (IsServerInitialized && !IsClientInitialized) return;
        MiniGameController controller = FindActiveMinigameController();
        controller?.OnNetworkMessage(messageType, payload);
    }

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

    [ServerRpc(RequireOwnership = false)]
    public void RequestMinigameAction(string messageType, string payload, NetworkConnection sender = null)
    {
        MiniGameController controller = FindActiveMinigameController();
        controller?.OnClientAction(messageType, payload, sender);
    }

    public void TeleportPlayer(NetworkConnection conn, Vector3 pos, Quaternion rot)
    {
        // Find the player object for this connection to pass NetworkObject
        PlayerObject player = null;
        foreach (var session in _sessions.Values)
        {
            player = session.Players.FirstOrDefault(p => p.Owner == conn);
            if (player != null) break;
        }

        if (player != null)
            RpcTeleportToPoint(conn, player.NetworkObject, pos, rot);
    }

    public void SyncLeaderboardToClients()
    {
        var careerEntries = ScoreManager.Instance.GetCareerLeaderboard();
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

        foreach (var profile in PlayerProfileManager.Instance.GetAllProfiles())
        {
            if (!sessionScoreMap.ContainsKey(profile.ClientId))
                sessionScoreMap[profile.ClientId] = 0;
        }

        var sorted = sessionScoreMap.OrderByDescending(kvp => kvp.Value).ToList();
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

        string sessionId = GetSessionId(stationIndex);
        List<PlayerObject> players = session.Players.ToList();

        StartCoroutine(ReturnPlayersDelayed(players, sessionId, stationIndex));
    }
}