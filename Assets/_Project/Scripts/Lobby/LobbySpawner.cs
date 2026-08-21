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
            if (instance.RackPrefab == null)
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
            GameObject rack = Instantiate(instance.RackPrefab, instance.Anchor.position, instance.Anchor.rotation, _propParent);
            rack.transform.localScale = instance.RackScale;
            rack.name = $"{setup.SetupLabel}_Rack";
            NetworkObject rackNetObj = rack.GetComponent<NetworkObject>();
            if (rackNetObj != null)
                InstanceFinder.ServerManager.Spawn(rackNetObj);

            int patternIndex = setup.RandomizePattern
                ? Random.Range(0, setup.Patterns.Count)
                : Mathf.Clamp(setup.ActivePatternIndex, 0, setup.Patterns.Count - 1);
            PoolRackPattern pattern = setup.Patterns[patternIndex];

            List<PoolBall> rackedBalls = SpawnRackedBalls(instance, setup, rack, pattern);

            PoolRackGrabbable rackGrabbable = rack.GetComponent<PoolRackGrabbable>();
            if (rackGrabbable != null)
                rackGrabbable.SetRackedBalls(rackedBalls);
            else
                Debug.LogWarning($"[LobbySpawner] Pool setup '{setup.SetupLabel}' rack has no PoolRackGrabbable — racked balls will stay locked forever, since nothing will unlock them on grab.");

            // Cached for ResetPoolSetup()/SwitchPoolPattern() — see the field
            // comments on PoolSetupInstance for why these are runtime-only,
            // not Inspector references.
            instance.SpawnedRack = rackGrabbable;
            instance.SpawnedNumberedBalls = rackedBalls;

            // Cue ball and cues don't vary by Pattern — same spot every time this setup spawns.
            if (instance.CueBallPrefab != null && instance.CueBallAnchor != null)
            {
                GameObject cueBall = Instantiate(instance.CueBallPrefab, instance.CueBallAnchor.position, instance.CueBallAnchor.rotation, _propParent);
                cueBall.transform.localScale = instance.CueBallScale;
                cueBall.name = $"{setup.SetupLabel}_CueBall";
                NetworkObject cueBallNetObj = cueBall.GetComponent<NetworkObject>();
                if (cueBallNetObj != null)
                    InstanceFinder.ServerManager.Spawn(cueBallNetObj);
                instance.SpawnedCueBall = cueBall.GetComponent<CueBall>();
            }
            else if (instance.CueBallPrefab != null || instance.CueBallAnchor != null)
            {
                Debug.LogWarning($"[LobbySpawner] Pool setup '{setup.SetupLabel}' has only one of CueBallPrefab/CueBallAnchor assigned — both are required to spawn the cue ball.");
            }

            if (instance.CueSlots != null)
            {
                for (int i = 0; i < instance.CueSlots.Count; i++)
                {
                    PoolCueSlot cueSlot = instance.CueSlots[i];
                    if (cueSlot.CuePrefab == null || cueSlot.Anchor == null)
                    {
                        Debug.LogWarning($"[LobbySpawner] Pool setup '{setup.SetupLabel}' cue slot index {i} is missing CuePrefab or Anchor — skipping.");
                        continue;
                    }
                    GameObject cue = Instantiate(cueSlot.CuePrefab, cueSlot.Anchor.position, cueSlot.Anchor.rotation, _propParent);
                    cue.transform.localScale = cueSlot.Scale;
                    cue.name = $"{setup.SetupLabel}_Cue_{i}";
                    NetworkObject cueNetObj = cue.GetComponent<NetworkObject>();
                    if (cueNetObj != null)
                        InstanceFinder.ServerManager.Spawn(cueNetObj);
                }
            }

            // Holding rack ("tray") — where PoolPocket sends pocketed numbered
            // balls (see PoolBall.HoldingSlotName). Doesn't vary by Pattern,
            // spawned once per setup just like the cue ball. The spawned
            // instance's Transform is cached on SpawnedHoldingRack so
            // PoolPocket can find it at runtime — a design-time Inspector
            // reference can't point at something that doesn't exist until
            // the server spawns it, unlike CueBallAnchor/Anchor which are
            // pre-placed empty GameObjects that already exist in the scene.
            if (instance.HoldingRackPrefab != null && instance.HoldingRackAnchor != null)
            {
                Quaternion holdingRackRotation = instance.HoldingRackAnchor.rotation * Quaternion.Euler(instance.HoldingRackRotation);
                GameObject holdingRack = Instantiate(instance.HoldingRackPrefab, instance.HoldingRackAnchor.position, holdingRackRotation, _propParent);
                holdingRack.transform.localScale = instance.HoldingRackScale;
                holdingRack.name = $"{setup.SetupLabel}_HoldingRack";
                NetworkObject holdingRackNetObj = holdingRack.GetComponent<NetworkObject>();
                if (holdingRackNetObj != null)
                    InstanceFinder.ServerManager.Spawn(holdingRackNetObj);
                else
                    Debug.LogWarning($"[LobbySpawner] Pool setup '{setup.SetupLabel}' HoldingRackPrefab has no NetworkObject — it will only appear on the host/server, not on remote clients.");
                instance.SpawnedHoldingRack = holdingRack.transform;
            }
            else if (instance.HoldingRackPrefab != null || instance.HoldingRackAnchor != null)
            {
                Debug.LogWarning($"[LobbySpawner] Pool setup '{setup.SetupLabel}' has only one of HoldingRackPrefab/HoldingRackAnchor assigned — both are required to spawn the holding rack.");
            }
        }
    }

    // Spawns and locks the balls for one pattern onto an already-spawned
    // rack. Shared by the initial spawn (SpawnPoolSetups) and pattern
    // switching (SwitchPoolPattern) below so both paths spawn balls
    // identically instead of duplicating the loop. Does NOT touch
    // PoolRackGrabbable/instance caching itself — callers do that with the
    // returned list, since the two call sites need slightly different
    // surrounding cleanup (SwitchPoolPattern also despawns the old balls first).
    private List<PoolBall> SpawnRackedBalls(PoolSetupInstance instance, PoolSetupConfig setup, GameObject rack, PoolRackPattern pattern)
    {
        List<PoolBall> rackedBalls = new List<PoolBall>();

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
            ball.transform.localScale = assignment.Scale;
            ball.name = $"{setup.SetupLabel}_{runtimeSlot.name}";
            NetworkObject ballNetObj = ball.GetComponent<NetworkObject>();
            if (ballNetObj != null)
                InstanceFinder.ServerManager.Spawn(ballNetObj);

            // Lock it completely fixed right away — nothing should be able
            // to nudge the rack formation before a player deliberately
            // lifts the rack off. PoolRackGrabbable unlocks it on grab.
            PoolBall poolBall = ball.GetComponent<PoolBall>();
            if (poolBall != null)
            {
                poolBall.ServerSetLocked(true);
                rackedBalls.Add(poolBall);
            }
        }

        return rackedBalls;
    }

    // Used by PoolPocket to find the spawned holding rack without needing a
    // direct Inspector reference (which can't exist at design time for a
    // runtime-spawned object — see the comment above where it's spawned).
    // Returns the first pool setup's holding rack, which is fine as long as
    // there's a single pool table; if a second pool setup is ever added,
    // this would need a way to say which table a given pocket belongs to
    // (e.g. matching by PoolSetupConfig reference instead of just "the first one").
    public Transform GetHoldingRack()
    {
        if (_poolSetups == null) return null;
        foreach (var setup in _poolSetups)
        {
            if (setup.SpawnedHoldingRack != null)
                return setup.SpawnedHoldingRack;
        }
        return null;
    }

    // --- Pool Reset (called by PoolResetButton) ---
    // Neither method carries FishNet's [Server] attribute — LobbySpawner is a
    // plain MonoBehaviour, not a NetworkBehaviour, and that attribute only
    // works on NetworkBehaviour methods. Same contract as GetHoldingRack()
    // above and the ServerSetLocked() call already inside SpawnPoolSetups():
    // it's on the caller (PoolResetButton, a NetworkBehaviour) to confirm
    // IsServerInitialized before calling in, not on these methods to check it.

    // Re-racks whatever balls are CURRENTLY tracked in SpawnedNumberedBalls
    // in place — rack back to its spawn spot, each ball back onto its own
    // original rack slot and re-locked, cue ball back to its anchor. Not
    // currently called by PoolResetButton — its FullReset/NineBallReset
    // modes both go through SwitchPoolPattern() instead, since each reset
    // button needs to always land on its OWN specific game mode rather than
    // whatever pattern happens to be racked at the moment (see the class
    // comment on PoolResetButton for why). Left here as a general-purpose
    // "re-rack without changing pattern" utility in case something needs
    // that distinction later.
    public void ResetPoolSetup(int setupIndex)
    {
        if (_poolSetups == null || setupIndex < 0 || setupIndex >= _poolSetups.Count)
        {
            Debug.LogWarning($"[LobbySpawner] ResetPoolSetup — invalid setup index {setupIndex}.");
            return;
        }
        PoolSetupInstance instance = _poolSetups[setupIndex];

        // Rack: drop it if held, then snap back to its spawn position/rotation.
        instance.SpawnedRack?.ForceReset();

        // Numbered balls: each back to the rack slot it was originally
        // spawned into (PoolBall captures this itself in Awake), gravity
        // restored, then re-locked and handed back to the rack so grabbing
        // it unlocks them again next break.
        if (instance.SpawnedNumberedBalls != null)
        {
            foreach (PoolBall ball in instance.SpawnedNumberedBalls)
            {
                if (ball == null) continue;
                ball.ServerResetToSpawn();
                ball.ServerSetLocked(true);
            }
            instance.SpawnedRack?.SetRackedBalls(instance.SpawnedNumberedBalls);
        }

        ResetCueBall(setupIndex);
    }

    // Cue-ball-only reset — for when it flies off the table entirely and
    // there's no pocket collider out there to catch it and trigger
    // PoolPocket's normal reset. Also used by ResetPoolSetup() above.
    public void ResetCueBall(int setupIndex)
    {
        if (_poolSetups == null || setupIndex < 0 || setupIndex >= _poolSetups.Count)
        {
            Debug.LogWarning($"[LobbySpawner] ResetCueBall — invalid setup index {setupIndex}.");
            return;
        }
        PoolSetupInstance instance = _poolSetups[setupIndex];
        if (instance.SpawnedCueBall == null || instance.CueBallAnchor == null)
        {
            Debug.LogWarning($"[LobbySpawner] ResetCueBall — setup {setupIndex} has no spawned cue ball or no CueBallAnchor assigned.");
            return;
        }
        instance.SpawnedCueBall.ServerResetTo(instance.CueBallAnchor.position, instance.CueBallAnchor.rotation);
    }

    // Switches this setup to a different Pattern in its PoolSetupConfig —
    // e.g. the default 8-ball rack switching to a 9-ball rack — and does a
    // full re-rack under that pattern. Unlike ResetPoolSetup(), this can't
    // just reset the existing balls in place: a different pattern can use a
    // different ball count or different prefabs entirely, so the current
    // numbered balls are despawned first and the new pattern's balls are
    // spawned fresh in their place. Despawns whichever balls are currently
    // tracked in SpawnedNumberedBalls regardless of where they physically
    // are right now (on the table, pocketed, sitting in the holding rack —
    // that list isn't updated by PoolPocket, so it always reflects every
    // ball from the setup that's currently live), so nothing gets left
    // behind floating in the holding rack after a mode switch.
    public void SwitchPoolPattern(int setupIndex, int patternIndex)
    {
        if (_poolSetups == null || setupIndex < 0 || setupIndex >= _poolSetups.Count)
        {
            Debug.LogWarning($"[LobbySpawner] SwitchPoolPattern — invalid setup index {setupIndex}.");
            return;
        }
        PoolSetupInstance instance = _poolSetups[setupIndex];
        PoolSetupConfig setup = instance.Config;
        if (setup == null || setup.Patterns == null || patternIndex < 0 || patternIndex >= setup.Patterns.Count)
        {
            Debug.LogWarning($"[LobbySpawner] SwitchPoolPattern — invalid pattern index {patternIndex} for setup {setupIndex}.");
            return;
        }
        if (instance.SpawnedRack == null)
        {
            Debug.LogWarning($"[LobbySpawner] SwitchPoolPattern — setup {setupIndex} has no spawned rack to re-rack onto.");
            return;
        }

        // Rack back to its spawn spot — a pattern switch starts a fresh
        // rack, not wherever a player last left it.
        instance.SpawnedRack.ForceReset();

        // Despawn the current balls before spawning the new pattern's —
        // reset-in-place (like ResetPoolSetup) isn't enough since the new
        // pattern may not have the same ball count or prefabs.
        if (instance.SpawnedNumberedBalls != null)
        {
            foreach (PoolBall ball in instance.SpawnedNumberedBalls)
            {
                if (ball == null) continue;
                NetworkObject ballNetObj = ball.GetComponent<NetworkObject>();
                if (ballNetObj != null && ballNetObj.IsSpawned)
                    InstanceFinder.ServerManager.Despawn(ballNetObj);
            }
        }

        PoolRackPattern pattern = setup.Patterns[patternIndex];
        List<PoolBall> rackedBalls = SpawnRackedBalls(instance, setup, instance.SpawnedRack.gameObject, pattern);
        instance.SpawnedRack.SetRackedBalls(rackedBalls);
        instance.SpawnedNumberedBalls = rackedBalls;

        // Fresh rack means a fresh break — cue ball goes back to its spot too.
        ResetCueBall(setupIndex);
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
    [Tooltip("The pattern data for this setup (e.g. PoolSetup) — SetupLabel and ball patterns only, no prefab reference.")]
    public PoolSetupConfig Config;
    [Tooltip("Scene Transform marking where the rack is placed — an empty GameObject positioned/rotated at the table's rack spot.")]
    public Transform Anchor;
    [Tooltip("The triangle/rack prefab, e.g. BillardBall_Triangle. Lives here rather than on PoolSetupConfig — see the note at the top of PoolSetupConfig.cs.")]
    public GameObject RackPrefab;
    public Vector3 RackScale = Vector3.one;

    [Header("Cue Ball")]
    [Tooltip("There's only ever one cue ball, so it's a single prefab/anchor pair rather than a list. Doesn't vary per Pattern — always spawns at the same spot regardless of which rack pattern is active.")]
    public GameObject CueBallPrefab;
    [Tooltip("Scene Transform marking the cue ball's spawn spot (typically the table's head spot).")]
    public Transform CueBallAnchor;
    public Vector3 CueBallScale = Vector3.one;

    [Header("Cues")]
    [Tooltip("One entry per cue stick. Each has its own prefab + anchor, so cues can differ from each other if needed, or just use the same prefab for both.")]
    public List<PoolCueSlot> CueSlots = new List<PoolCueSlot>();

    [Header("Holding Rack (Pockets)")]
    [Tooltip("The tray/rack prefab that pocketed numbered balls teleport to — same numbered-child-slot convention as RackPrefab (e.g. BallHolder_8), resolved by PoolPocket via Transform.Find. Needs a NetworkObject like the other pool prefabs, or remote clients won't see it. Doesn't vary by Pattern.")]
    public GameObject HoldingRackPrefab;
    [Tooltip("Scene Transform marking where the holding rack spawns.")]
    public Transform HoldingRackAnchor;
    [Tooltip("Euler offset applied on top of HoldingRackAnchor's own rotation — leave at zero to just use the anchor's rotation as-is, or nudge this to fine-tune facing without touching the anchor Transform itself.")]
    public Vector3 HoldingRackRotation = Vector3.zero;
    public Vector3 HoldingRackScale = Vector3.one;

    // Set by SpawnPoolSetups() once the holding rack is actually spawned —
    // not an Inspector field, this only exists at runtime. PoolPocket reads
    // it via LobbySpawner.Instance.GetHoldingRack().
    [System.NonSerialized] public Transform SpawnedHoldingRack;

    // Runtime-only, populated by SpawnPoolSetups() — used by
    // LobbySpawner.ResetPoolSetup()/ResetCueBall() so a reset doesn't need
    // to re-discover the spawned objects (Transform.Find by name, tag
    // search, etc.) each time it's triggered.
    [System.NonSerialized] public PoolRackGrabbable SpawnedRack;
    [System.NonSerialized] public CueBall SpawnedCueBall;
    [System.NonSerialized] public List<PoolBall> SpawnedNumberedBalls = new List<PoolBall>();
}

[System.Serializable]
public class PoolCueSlot
{
    public GameObject CuePrefab;
    [Tooltip("Scene Transform marking where this cue spawns — e.g. a spot on a wall-mounted cue rack.")]
    public Transform Anchor;
    public Vector3 Scale = Vector3.one;
}
