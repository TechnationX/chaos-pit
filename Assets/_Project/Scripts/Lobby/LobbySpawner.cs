// LobbySpawner.cs

using UnityEngine;
using System.Collections.Generic;
using FishNet;
using FishNet.Object;

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

    private void Awake()
    {
        InstanceFinder.ServerManager.OnServerConnectionState += OnServerStarted;
        //Debug.Log("LobbySpawner: Subscribed in Awake");
    }

    private void OnServerStarted(FishNet.Transporting.ServerConnectionStateArgs args)
    {
        //Debug.Log($"LobbySpawner: OnServerStarted fired: {args.ConnectionState}");
        if (args.ConnectionState != FishNet.Transporting.LocalConnectionState.Started) return;
        InstanceFinder.ServerManager.OnServerConnectionState -= OnServerStarted;

        RegisterSpawnPoints();
        SpawnFurniture();
        SpawnProps();
    }

    private void OnDestroy()
    {
        if (InstanceFinder.ServerManager != null)
            InstanceFinder.ServerManager.OnServerConnectionState -= OnServerStarted;
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

        // Shuffle for random assignment
        ShuffleSpawnPoints();

        //Debug.Log($"LobbySpawner: Registered {_availableSpawnPoints.Count} player spawn points");
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
        //Debug.Log($"LobbySpawner: SpawnFurniture called. Config null: {_furnitureSpawnConfig == null}");
        if (_furnitureSpawnConfig == null) return;
        //Debug.Log($"LobbySpawner: Furniture entries count: {_furnitureSpawnConfig.Entries.Length}");

        foreach (var entry in _furnitureSpawnConfig.Entries)
        {
            if (entry.Prefab == null)
            {
                Debug.LogWarning($"LobbySpawner: Furniture entry '{entry.Label}' has no prefab assigned");
                continue;
            }

            GameObject obj = Instantiate(
                entry.Prefab,
                entry.Position,
                Quaternion.Euler(entry.Rotation),
                _furnitureParent
            );

            //Debug.Log($"LobbySpawner: '{entry.Label}' active after Instantiate: {obj.activeSelf}");
            obj.transform.localScale = entry.Scale;
            obj.name = entry.Label;
            //Debug.Log($"LobbySpawner: '{entry.Label}' active after scale/name: {obj.activeSelf}");

            //Debug.Log($"LobbySpawner: Placed furniture '{entry.Label}'");

            NetworkObject netObj = obj.GetComponent<NetworkObject>();
            if (netObj != null)
            {
                //Debug.Log($"LobbySpawner: '{entry.Label}' has NetworkObject, spawning through Fish-Net");
                InstanceFinder.ServerManager.Spawn(netObj);
                //Debug.Log($"LobbySpawner: '{entry.Label}' active after Fish-Net spawn: {obj.activeSelf}");
            }
        }
    }

    // --- Props ---

    private void SpawnProps()
    {
        //Debug.Log($"LobbySpawner: SpawnProps called. Config null: {_propSpawnConfig == null}");
        if (_propSpawnConfig == null) return;
        //Debug.Log($"LobbySpawner: Prop entries count: {_propSpawnConfig.Entries.Length}");

        foreach (var entry in _propSpawnConfig.Entries)
        {
            if (entry.Prefab == null)
            {
                Debug.LogWarning($"LobbySpawner: Prop entry '{entry.Label}' has no prefab assigned");
                continue;
            }

            GameObject obj = Instantiate(
                entry.Prefab,
                entry.Position,
                Quaternion.Euler(entry.Rotation),
                _propParent
            );

            obj.transform.localScale = entry.Scale;
            obj.name = entry.Label;

            // Spawn networked props through Fish-Net
            NetworkObject netObj = obj.GetComponent<NetworkObject>();
            if (netObj != null)
            {
                //Debug.Log($"LobbySpawner: '{entry.Label}' has NetworkObject, spawning through Fish-Net");
                InstanceFinder.ServerManager.Spawn(netObj);
                //Debug.Log($"LobbySpawner: '{entry.Label}' active after Fish-Net spawn: {obj.activeSelf}");
            }
            else
            {
                Debug.Log($"LobbySpawner: Spawned prop '{entry.Label}' as {entry.Type}");
            }
        }
    }
}