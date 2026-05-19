// EditorPlayerSpawner.cs

using UnityEngine;
using FishNet;
using FishNet.Object;
using System.Collections;

public class EditorPlayerSpawner : MonoBehaviour
{
    [SerializeField] private NetworkObject _playerPrefab;
    [SerializeField] private LobbySpawner _lobbySpawner;

#if UNITY_EDITOR
    private void Awake()
    {
        // Subscribe in Awake so we don't miss the event
        InstanceFinder.ServerManager.OnServerConnectionState += OnServerStarted;
        //Debug.Log("EditorPlayerSpawner subscribed in Awake");
    }

    private void OnServerStarted(FishNet.Transporting.ServerConnectionStateArgs args)
    {
        //Debug.Log($"OnServerStarted fired: {args.ConnectionState}");
        if (args.ConnectionState == FishNet.Transporting.LocalConnectionState.Started)
        {
            StartCoroutine(DelayedSpawn());
        }
    }

    private IEnumerator DelayedSpawn()
    {
        yield return new WaitForSeconds(0.5f);

        while (InstanceFinder.ClientManager.Connection.ClientId == -1)
            yield return null;

        // Get spawn point from LobbySpawner
        Vector3 spawnPosition = new Vector3(0, 1f, 0);
        Quaternion spawnRotation = Quaternion.identity;

        if (_lobbySpawner != null)
        {
            if (!_lobbySpawner.TryGetSpawnPoint(out spawnPosition, out spawnRotation))
                Debug.LogWarning("EditorPlayerSpawner: No spawn points available, using default");
        }
        else
        {
            Debug.LogWarning("EditorPlayerSpawner: LobbySpawner not assigned, using default position");
        }

        var connection = InstanceFinder.ClientManager.Connection;
        Debug.Log($"Spawning player at {spawnPosition}. ClientId: {connection.ClientId}");

        NetworkObject player = Instantiate(
            _playerPrefab,
            spawnPosition,
            spawnRotation
        );

        InstanceFinder.ServerManager.Spawn(player, connection);
    }

    private void OnDestroy()
    {
        if (InstanceFinder.ServerManager != null)
            InstanceFinder.ServerManager.OnServerConnectionState -= OnServerStarted;
    }
#endif
}