// LobbySpawner.cs

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
        PlayerProfileManager.Instance.RegisterPlayer(conn);
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
        Debug.Log($"[LobbySpawner] Pre-registered connection: {conn.ClientId}");
    }

    private void OnServerStarted(ServerConnectionStateArgs args)
    {
        if (args.ConnectionState != LocalConnectionState.Started) return;
        InstanceFinder.ServerManager.OnServerConnectionState -= OnServerStarted;

        RegisterSpawnPoints();
        SpawnFurniture();
        SpawnProps();
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
        Debug.Log($"[LobbySpawner] OnClientLoadedStartScenes — ConnId: {conn.ClientId}, already spawned: {_spawnedConnections.Contains(conn.ClientId)}");
        if (!asServer) return;

        if (_spawnedConnections.Contains(conn.ClientId)) return;
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

    // --- Props ---

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
}