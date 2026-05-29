using FishNet;
using System.Collections;
using UnityEditor.SceneManagement;
using UnityEngine;

public class EditorBootstrap : MonoBehaviour
{
    //#if UNITY_EDITOR

    private static bool _launchedFromBootstrap = false;

    [Header("Editor Settings")]
    [SerializeField] private bool _autoStartOnPlay = true;

    private void Start()
    {
        //Debug.Log("[EditorBootstrap] Start fired.");
        if (_autoStartOnPlay && !_launchedFromBootstrap)
        {
            _launchedFromBootstrap = true;
            UnityEngine.SceneManagement.SceneManager.LoadScene("Bootstrap");
            Debug.Log("[EditorBootstrap] Launching from Bootstrap scene.");
            return;
        }
        ;
        StartCoroutine(StartAndSpawn());
    }

    private IEnumerator StartAndSpawn()
    {
        //Debug.Log("[EditorBootstrap] Starting connections...");

        var utpTransport = FindFirstObjectByType<FishNet.Transporting.UTP.UnityTransport>();
        if (utpTransport != null)
            utpTransport.SetConnectionData("127.0.0.1", 7777);

        InstanceFinder.ServerManager.StartConnection();
        InstanceFinder.ClientManager.StartConnection();

        //Debug.Log("[EditorBootstrap] Waiting for client connection...");
        float timeout = 5f;
        while ((InstanceFinder.ClientManager.Connection == null ||
                InstanceFinder.ClientManager.Connection.ClientId == -1) && timeout > 0f)
        {
            timeout -= Time.deltaTime;
            yield return null;
        }

        if (timeout <= 0f)
        {
            Debug.LogError("[EditorBootstrap] Timed out waiting for client connection.");
            yield break;
        }

        //Debug.Log($"[EditorBootstrap] Connected. ClientId: {InstanceFinder.ClientManager.Connection.ClientId}");
        // Pre-register immediately — no delay
        var lobbySpawner = FindFirstObjectByType<LobbySpawner>();
        if (lobbySpawner != null)
            lobbySpawner.PreRegisterConnection(InstanceFinder.ClientManager.Connection);

        yield return new WaitForSeconds(0.3f);

        //Debug.Log($"[EditorBootstrap] LobbySpawner found: {lobbySpawner != null}");

        lobbySpawner = FindFirstObjectByType<LobbySpawner>();
        if (lobbySpawner != null)
            lobbySpawner.EditorTriggerHostSpawn(InstanceFinder.ClientManager.Connection);
    }
//#endif
}