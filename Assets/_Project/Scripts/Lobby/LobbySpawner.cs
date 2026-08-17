// LobbySpawner
using FishNet;
using FishNet.Connection;
using FishNet.Object;
using FishNet.Transporting;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
public class LobbySpawner : MonoBehaviour
{
    [Header("Configs")]
    [SerializeField] private PlayerSpawnConfig _playerSpawnConfig;
    [SerializeField] private FurnitureSpawnConfig _furnitureSpawnConfig;
    [SerializeField] private PropSpawnConfig _propSpawnConfig;
    [Tooltip("Each entry pairs a Prop Setup Config (piece list/patterns) with the scene Transform that anchors it — e.g. an empty GameObject placed where the chess table sits.")]
    [SerializeField] private List<PropSetupInstance> _propSetups;
    [Tooltip("Pool rack setups — the rack prefab (e.g. BillardBall_Triangle) supplies its own numbered ball-slot children, unlike the piece-list based Prop Setups above.")]
    [SerializeField] private List<PoolSetupInstance> _poolSetups;
    [Header("Parents")]
    [SerializeField] private Transform _furnitureParent;
    [SerializeField] private Transform _propParent;
    private List<Vector3> _availableSpawnPoints = new List<Vector3>();
    private List<Quaternion> _availableSpawnRotations = new List<Quaternion>();
    private bool _spawnListenerRegistered = false;
    public static LobbySpawner Instance { get; private set; }
    private int _spawnPointIndex = 0;
    private HashSet<int> _spawnedConnections = new HashSet<int>();
#if UNITY_EDITOR
    private bool _editorHostSpawnPending = false;
    public void EditorTriggerHostSpawn(FishNet.Connection.NetworkConnection conn)
    {
        if (_playerSpawnConfig.PlayerPrefab == null) return;
        _spawnedConnections.Add(conn.ClientId);
        if (!TryGetSpawnPoint(out Vector3 position, out Quaternion rotation))
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
        }
        GameObject player = Instantiate(_playerSpawnConfig.PlayerPrefab, position, rotation);
        NetworkObject netObj = player.GetComponent<NetworkObject>();
        InstanceFinder.ServerManager.Spawn(netObj, conn);
        //Debug.Log($"[LobbySpawner] EditorTriggerHostSpawn — spawned at {position}");
        PlayerProfileManager.Instance.RegisterPlayer(conn);
        PlayerObject playerObj = player.GetComponent<PlayerObject>();
        PlayerProfile profile = PlayerProfileManager.Instance.GetProfile(conn);
        string displayName = profile?.DisplayName ?? $"Player_{conn.ClientId}";
        playerObj?.SetPlayerData(displayName, conn.ClientId);
        GameRoomManager.Instance?.SyncLeaderboardToClients();
    }
#endif
    private void Start()
    {
        if (InstanceFinder.ServerManager == null)
        {
            Debug.LogWarning("[LobbySpawner] ServerManager not ready — waiting.");
            StartCoroutine(WaitForServerManager());
            return;
        }
        if (InstanceFinder.ServerManager.Started)
        {
            RegisterSpawnPoints();
            SpawnFurniture();
            SpawnProps();
            SpawnPropSetups();
            SpawnPoolSetups();
            RegisterSpawnListener();
            InstanceFinder.ServerManager.OnRemoteConnectionState += OnRemoteConnectionState;
        }
        else
        {
            InstanceFinder.ServerManager.OnServerConnectionState += OnServerStarted;
        }
    }
    private IEnumerator WaitForServerManager()
    {
        while (InstanceFinder.ServerManager == null)
            yield return null;
        // ServerManager exists now — run normal Start logic
        if (InstanceFinder.ServerManager.Started)
        {
            RegisterSpawnPoints();
            SpawnFurniture();
            SpawnProps();
            SpawnPropSetups();
            SpawnPoolSetups();
            RegisterSpawnListener();
            InstanceFinder.ServerManager.OnRemoteConnectionState += OnRemoteConnectionState;
        }
        else
        {
            InstanceFinder.ServerManager.OnServerConnectionState += OnServerStarted;
        }
    }
    private void Awake()
    {
        Instance = this;
    }
    public void PreRegisterConnection(NetworkConnection conn)
    {
        _spawnedConnections.Add(conn.ClientId);
        //Debug.Log($"[LobbySpawner] Pre-registered connection: {conn.ClientId}");
    }
    private void OnServerStarted(ServerConnectionStateArgs args)
    {
        if (args.ConnectionState != LocalConnectionState.Started) return;
        InstanceFinder.ServerManager.OnServerConnectionState -= OnServerStarted;
        RegisterSpawnPoints();
        SpawnFurniture();
        SpawnProps();
        SpawnPropSetups();
        SpawnPoolSetups();
        RegisterSpawnListener();
        InstanceFinder.ServerManager.OnRemoteConnectionState += OnRemoteConnectionState;
    }
    private void OnRemoteConnectionState(NetworkConnection conn, RemoteConnectionStateArgs args)
    {
        if (args.ConnectionState == RemoteConnectionState.Stopped)
            PlayerProfileManager.Instance.UnregisterPlayer(conn);
    }
    private void RegisterSpawnListener()
    {
        //Debug.Log($"[LobbySpawner] RegisterSpawnListener called. AlreadyRegistered: {_spawnListenerRegistered}");
        if (_spawnListenerRegistered) return;
        _spawnListenerRegistered = true;
        InstanceFinder.SceneManager.OnClientLoadedStartScenes += OnClientLoadedStartScenes;
    }
    private void OnClientLoadedStartScenes(FishNet.Connection.NetworkConnection conn, bool asServer)
    {
        //Debug.Log($"[LobbySpawner] OnClientLoadedStartScenes — connId: {conn.ClientId}, asServer: {asServer}, alreadySpawned: {_spawnedConnections.Contains(conn.ClientId)}");
        if (!asServer) return;
        if (_spawnedConnections.Contains(conn.ClientId))
        {
            // Refresh leaderboard for both server and client
            //LeaderboardManager.Instance?.Refresh();
            return;
        }
        _spawnedConnections.Add(conn.ClientId);
        if (_playerSpawnConfig.PlayerPrefab == null)
        {
            Debug.LogError("[LobbySpawner] PlayerPrefab not assigned in PlayerSpawnConfig.");
            return;
        }
        if (!TryGetSpawnPoint(out Vector3 position, out Quaternion rotation))
        {
            Debug.LogWarning("[LobbySpawner] No spawn points available. Spawning at origin.");
            position = Vector3.zero;
            rotation = Quaternion.identity;
        }
        GameObject player = Instantiate(_playerSpawnConfig.PlayerPrefab, position, rotation);
        NetworkObject netObj = player.GetComponent<NetworkObject>();
        InstanceFinder.ServerManager.Spawn(netObj, conn);
        PlayerProfileManager.Instance.RegisterPlayer(conn);
        PlayerObject playerObj = player.GetComponent<PlayerObject>();
        PlayerProfile profile = PlayerProfileManager.Instance.GetProfile(conn);
        string displayName = profile?.DisplayName ?? $"Player_{conn.ClientId}";
        playerObj?.SetPlayerData(displayName, conn.ClientId);
        //Debug.Log($"[LobbySpawner] Calling LeaderboardManager.Refresh — instance: {LeaderboardManager.Instance != null}");
        GameRoomManager.Instance?.SyncLeaderboardToClients();
    }
    private void OnDestroy()
    {
        if (InstanceFinder.ServerManager != null)
            InstanceFinder.ServerManager.OnServerConnectionState -= OnServerStarted;
        if (InstanceFinder.SceneManager != null)
            InstanceFinder.SceneManager.OnClientLoadedStartScenes -= OnClientLoadedStartScenes;
        if (InstanceFinder.ServerManager != null)
            InstanceFinder.ServerManager.OnRemoteConnectionState -= OnRemoteConnectionState;
        _spawnedConnections.Clear();
    }
    // --- Player Spawn Points ---
    private void RegisterSpawnPoints()
    {
        _availableSpawnPoints.Clear();
        _availableSpawnRotations.Clear();
        foreach (var point in _playerSpawnConfig.SpawnPoints)
        {
            _availableSpawnPoints.Add(point.Position);
            _availableSpawnRotations.Add(Quaternion.Euler(point.Rotation));
        }
        ShuffleSpawnPoints();
    }
    public bool TryGetSpawnPoint(out Vector3 position, out Quaternion rotation)
    {
        //Debug.Log($"[LobbySpawner] TryGetSpawnPoint — index: {_spawnPointIndex}, caller: {new System.Diagnostics.StackTrace().ToString().Split('\n')[1].Trim()}");
        if (_playerSpawnConfig == null || _playerSpawnConfig.SpawnPoints.Length == 0)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            return false;
        }
        position = _playerSpawnConfig.SpawnPoints[_spawnPointIndex].Position;
        rotation = Quaternion.Euler(_playerSpawnConfig.SpawnPoints[_spawnPointIndex].Rotation);
        _spawnPointIndex = (_spawnPointIndex + 1) % _playerSpawnConfig.SpawnPoints.Length;
        return true;
    }
    private void ShuffleSpawnPoints()
    {
        for (int i = _availableSpawnPoints.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            var tempPos = _availableSpawnPoints[i];
            _availableSpawnPoints[i] = _availableSpawnPoints[j];
            _availableSpawnPoints[j] = tempPos;
            var tempRot = _availableSpawnRotations[i];
            _availableSpawnRotations[i] = _availableSpawnRotations[j];
            _availableSpawnRotations[j] = tempRot;
        }
    }
    public bool TryGetReturnSpawnPoint(out Vector3 position, out Quaternion rotation)
    {
        // Re-read all spawn points from config each time — doesn't consume the list
        if (_playerSpawnConfig == null || _playerSpawnConfig.SpawnPoints.Length == 0)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            return false;
        }
        // Pick a random spawn point from the full config list
        int index = Random.Range(0, _playerSpawnConfig.SpawnPoints.Length);
        position = _playerSpawnConfig.SpawnPoints[index].Position;
        rotation = Quaternion.Euler(_playerSpawnConfig.SpawnPoints[index].Rotation);
        return true;
    }
    // --- Furniture ---
    private void SpawnFurniture()
    {
        if (_furnitureSpawnConfig == null) return;
        foreach (var entry in _furnitureSpawnConfig.Entries)
        {
            if (entry.Prefab == null)
            {
                Debug.LogWarning($"[LobbySpawner] Furniture entry '{entry.Label}' has no prefab assigned.");
                continue;
            }
            GameObject obj = Instantiate(entry.Prefab, entry.Position, Quaternion.Euler(entry.Rotation), _furnitureParent);
            obj.transform.localScale = entry.Scale;
            obj.name = entry.Label;
            NetworkObject netObj = obj.GetComponent<NetworkObject>();
            if (netObj != null)
                InstanceFinder.ServerManager.Spawn(netObj);
        }
    }
    // --- Props (single-item spawns — unchanged) ---
    private void SpawnProps()
    {
        if (_propSpawnConfig == null) return;
        foreach (var entry in _propSpawnConfig.Entries)
        {
            if (entry.Prefab == null)
            {
                Debug.LogWarning($"[LobbySpawner] Prop entry '{entry.Label}' has no prefab assigned.");
                continue;
            }
            GameObject obj = Instantiate(entry.Prefab, entry.Position, Quaternion.Euler(entry.Rotation), _propParent);
            obj.transform.localScale = entry.Scale;
            obj.name = entry.Label;
            NetworkObject netObj = obj.GetComponent<NetworkObject>();
            if (netObj != null)
                InstanceFinder.ServerManager.Spawn(netObj);
        }
    }
    // --- Prop Setups (grouped mini setups — chess, bowling, pool, etc.) ---
    private void SpawnPropSetups()
    {
        if (_propSetups == null) return;
        foreach (var instance in _propSetups)
        {
            PropSetupConfig setup = instance?.Config;
            if (setup == null) continue;
            if (instance.Anchor == null)
            {
                Debug.LogWarning($"[LobbySpawner] Prop setup '{setup.SetupLabel}' has no Anchor Transform assigned — skipping.");
                continue;
            }
            if (setup.Patterns == null || setup.Patterns.Count == 0)
            {
                Debug.LogWarning($"[LobbySpawner] Prop setup '{setup.SetupLabel}' has no patterns defined.");
                continue;
            }
            int patternIndex = setup.RandomizePattern
                ? Random.Range(0, setup.Patterns.Count)
                : Mathf.Clamp(setup.ActivePatternIndex, 0, setup.Patterns.Count - 1);
            PropSetupPattern pattern = setup.Patterns[patternIndex];
            Vector3 anchorPosition = instance.Anchor.position;
            Quaternion anchorRotation = instance.Anchor.rotation;
            foreach (var entry in pattern.Entries)
            {
                if (entry.Prefab == null)
                {
                    Debug.LogWarning($"[LobbySpawner] Prop setup '{setup.SetupLabel}' pattern '{pattern.PatternName}' entry '{entry.Label}' has no prefab assigned.");
                    continue;
                }
                Vector3 worldPosition = anchorPosition + (anchorRotation * entry.Position);
                Quaternion worldRotation = anchorRotation * Quaternion.Euler(entry.Rotation);
                GameObject obj = Instantiate(entry.Prefab, worldPosition, worldRotation, _propParent);
                obj.transform.localScale = entry.Scale;
                obj.name = $"{setup.SetupLabel}_{entry.Label}";
                NetworkObject netObj = obj.GetComponent<NetworkObject>();
                if (netObj != null)
                    InstanceFinder.ServerManager.Spawn(netObj);
            }
        }
    }
    // --- Pool Setups (rack prefab supplies its own ball-slot positions, assigned per-slot in the Inspector) ---
    private void SpawnPoolSetups()
    {
        if (_poolSetups == null) return;
        foreach (var instance in _poolSetups)
        {
            PoolSetupConfig setup = instance?.Config;
            if (setup == null) continue;
            if (instance.Anchor == null)
            {
                Debug.LogWarning($"[LobbySpawner] Pool setup '{setup.SetupLabel}' has no Anchor Transform assigned — skipping.");
                continue;
            }
            if (setup.RackPrefab == null)
            {
                Debug.LogWarning($"[LobbySpawner] Pool setup '{setup.SetupLabel}' has no RackPrefab assigned — skipping.");
                continue;
            }
            if (setup.Patterns == null || setup.Patterns.Count == 0)
            {
                Debug.LogWarning($"[LobbySpawner] Pool setup '{setup.SetupLabel}' has no patterns defined.");
                continue;
            }

            // Spawn the rack itself at the anchor.
            GameObject rack = Instantiate(setup.RackPrefab, instance.Anchor.position, instance.Anchor.rotation, _propParent);
            rack.name = $"{setup.SetupLabel}_Rack";
            NetworkObject rackNetObj = rack.GetComponent<NetworkObject>();
            if (rackNetObj != null)
                InstanceFinder.ServerManager.Spawn(rackNetObj);

            int patternIndex = setup.RandomizePattern
                ? Random.Range(0, setup.Patterns.Count)
                : Mathf.Clamp(setup.ActivePatternIndex, 0, setup.Patterns.Count - 1);
            PoolRackPattern pattern = setup.Patterns[patternIndex];

            foreach (var assignment in pattern.SlotAssignments)
            {
                if (string.IsNullOrEmpty(assignment.SlotName))
                {
                    Debug.LogWarning($"[LobbySpawner] Pool setup '{setup.SetupLabel}' pattern '{pattern.PatternName}' has a slot assignment with no SlotName set.");
                    continue;
                }
                if (assignment.BallPrefab == null)
                {
                    Debug.LogWarning($"[LobbySpawner] Pool setup '{setup.SetupLabel}' pattern '{pattern.PatternName}' slot '{assignment.SlotName}' has no BallPrefab assigned.");
                    continue;
                }
                Transform runtimeSlot = rack.transform.Find(assignment.SlotName);
                if (runtimeSlot == null)
                {
                    Debug.LogWarning($"[LobbySpawner] Pool setup '{setup.SetupLabel}' — couldn't find a child named '{assignment.SlotName}' on the spawned rack. Check spelling/casing against RackPrefab's Hierarchy.");
                    continue;
                }
                GameObject ball = Instantiate(assignment.BallPrefab, runtimeSlot.position, runtimeSlot.rotation, _propParent);
                ball.name = $"{setup.SetupLabel}_{runtimeSlot.name}";
                NetworkObject ballNetObj = ball.GetComponent<NetworkObject>();
                if (ballNetObj != null)
                    InstanceFinder.ServerManager.Spawn(ballNetObj);
            }
        }
    }
}

[System.Serializable]
public class PropSetupInstance
{
    [Tooltip("The piece list/pattern data for this setup (e.g. ChessSetup, BowlingSetup).")]
    public PropSetupConfig Config;
    [Tooltip("Scene Transform marking where this setup is placed — an empty GameObject positioned/rotated at the table's center. Entries in the active Pattern are spawned relative to this.")]
    public Transform Anchor;
}

[System.Serializable]
public class PoolSetupInstance
{
    [Tooltip("The rack prefab/pattern data for this setup (e.g. PoolSetup).")]
    public PoolSetupConfig Config;
    [Tooltip("Scene Transform marking where the rack is placed — an empty GameObject positioned/rotated at the table's rack spot.")]
    public Transform Anchor;
}