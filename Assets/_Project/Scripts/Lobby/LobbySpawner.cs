// LobbySpawner.cs

using FishNet;
using FishNet.Object;
using FishNet.Transporting;
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

    private void Start()
    {
        if (InstanceFinder.ServerManager.Started)
        {
            RegisterSpawnPoints();
            SpawnFurniture();
            SpawnProps();
            RegisterSpawnListener();
        }
        else
        {
            InstanceFinder.ServerManager.OnServerConnectionState += OnServerStarted;
        }
    }

    private void OnServerStarted(ServerConnectionStateArgs args)
    {
        if (args.ConnectionState != LocalConnectionState.Started) return;
        InstanceFinder.ServerManager.OnServerConnectionState -= OnServerStarted;

        RegisterSpawnPoints();
        SpawnFurniture();
        SpawnProps();
        RegisterSpawnListener();
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
        //Debug.Log($"[LobbySpawner] Fired — ConnId: {conn.ClientId}, asServer: {asServer}, HostConnId: {InstanceFinder.ClientManager.Connection?.ClientId}, IsHost: {conn == InstanceFinder.ClientManager.Connection}");
        if (!asServer) return;

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
    }

    private void OnDestroy()
    {
        if (InstanceFinder.ServerManager != null)
            InstanceFinder.ServerManager.OnServerConnectionState -= OnServerStarted;
        if (InstanceFinder.SceneManager != null)
            InstanceFinder.SceneManager.OnClientLoadedStartScenes -= OnClientLoadedStartScenes;
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
        if (_availableSpawnPoints.Count == 0)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            return false;
        }

        position = _availableSpawnPoints[0];
        rotation = _availableSpawnRotations[0];
        _availableSpawnPoints.RemoveAt(0);
        _availableSpawnRotations.RemoveAt(0);
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