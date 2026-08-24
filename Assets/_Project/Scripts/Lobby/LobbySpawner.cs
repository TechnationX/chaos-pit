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
    [Tooltip("Bowling lane setups — pins are generated procedurally from a head-pin anchor + spacing rather than authored per-slot like the pool rack, see BowlingPinConfig.")]
    [SerializeField] private List<BowlingLaneInstance> _bowlingLanes;
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
            SpawnBowlingSetups();
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
            SpawnBowlingSetups();
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
    private void Update()
    {
        // Only the server drives pin hiding — same guard SpawnFurniture()
        // etc. rely on indirectly via Start()'s ServerManager.Started check,
        // since LobbySpawner is a plain MonoBehaviour (not a
        // NetworkBehaviour) and exists on every peer, not just the server.
        if (InstanceFinder.ServerManager == null || !InstanceFinder.ServerManager.Started) return;
        if (_bowlingLanes == null) return;

        foreach (var lane in _bowlingLanes)
            UpdateBowlingPinGroupHide(lane);
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
        SpawnBowlingSetups();
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
    //
    // Validates synchronously, then hands off to a coroutine (below) that
    // does the actual despawn/spawn work spread across multiple frames —
    // see SwitchPoolPatternRoutine for why.
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

        // Stop any switch already in progress for this setup before starting
        // a new one — otherwise two overlapping coroutines could both end up
        // despawning/spawning against the same instance.SpawnedNumberedBalls
        // list at once.
        if (instance.ActiveSwitchRoutine != null)
            StopCoroutine(instance.ActiveSwitchRoutine);
        instance.ActiveSwitchRoutine = StartCoroutine(SwitchPoolPatternRoutine(instance, setup, patternIndex, setupIndex));
    }

    // Does the actual despawn-old/spawn-new work for SwitchPoolPattern, one
    // ball per frame instead of firing every Despawn()/Spawn() in a single
    // synchronous burst. A full re-rack can mean up to ~30 reliable network
    // messages at once (15 despawns + 15 spawns + the rack's ForceReset
    // broadcast + the cue ball's reset broadcast) — bursting all of that in
    // one server frame was found to reliably stall a connected client:
    // the client would receive NONE of it (confirmed via PoolBall's
    // OnStartNetwork/OnStopNetwork never firing on that client), and —
    // notably — grab/drop RPCs for completely unrelated props would then
    // also stop working for that same client afterward, which points at the
    // reliable channel getting stuck/backed up rather than any single
    // message being malformed. Spreading the same work across multiple
    // frames avoids the burst entirely. Duplicates SpawnRackedBalls()'s spawn
    // loop rather than reusing it, since that helper runs synchronously and
    // has no way to yield mid-loop.
    private IEnumerator SwitchPoolPatternRoutine(PoolSetupInstance instance, PoolSetupConfig setup, int patternIndex, int setupIndex)
    {
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
                yield return null;
            }
        }

        PoolRackPattern pattern = setup.Patterns[patternIndex];
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
            Transform runtimeSlot = instance.SpawnedRack.transform.Find(assignment.SlotName);
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

            PoolBall poolBall = ball.GetComponent<PoolBall>();
            if (poolBall != null)
            {
                poolBall.ServerSetLocked(true);
                rackedBalls.Add(poolBall);
            }

            yield return null;
        }

        instance.SpawnedRack.SetRackedBalls(rackedBalls);
        instance.SpawnedNumberedBalls = rackedBalls;

        // Fresh rack means a fresh break — cue ball goes back to its spot too.
        ResetCueBall(setupIndex);

        instance.ActiveSwitchRoutine = null;
    }

    // --- Bowling Setups (pins generated procedurally from a head-pin anchor + spacing) ---

    // Standard bowling triangle, local (row, col) grid indexed 0-9 in
    // standard pin numbering order (see BowlingPinConfig's class comment).
    // Row 0 = pin 1 nearest the head anchor, row 3 = the back row.
    private static readonly (int row, int col)[] _bowlingPinSlotGrid = new (int, int)[]
    {
        (0, 0),                         // 0 = pin 1
        (1, 0), (1, 1),                 // 1 = pin 2,  2 = pin 3
        (2, 0), (2, 1), (2, 2),         // 3 = pin 4,  4 = pin 5,  5 = pin 6
        (3, 0), (3, 1), (3, 2), (3, 3), // 6 = pin 7,  7 = pin 8,  8 = pin 9,  9 = pin 10
    };

    // Local offset (relative to HeadPinAnchor, +Z down the lane) for a given
    // slot index. Row spacing uses the equilateral-triangle height
    // (spacing * sqrt(3)/2) so each row nests properly behind the one in
    // front instead of sitting in a plain square grid.
    private Vector3 GetBowlingPinSlotLocalOffset(int slotIndex, float spacing)
    {
        (int row, int col) = _bowlingPinSlotGrid[slotIndex];
        float rowSpacing = spacing * Mathf.Sqrt(3f) * 0.5f;
        float x = (col - row * 0.5f) * spacing;
        float z = row * rowSpacing;
        return new Vector3(x, 0f, z);
    }

    private void SpawnBowlingSetups()
    {
        if (_bowlingLanes == null) return;
        foreach (var lane in _bowlingLanes)
            StartCoroutine(SpawnBowlingLaneRoutine(lane));
    }

    // Spreads the one-time pin + ball spawn across frames — not because a
    // single lane's ~13 spawns are likely to stall a channel on their own
    // (SwitchPoolPattern's burst was closer to 30 in one frame), but because
    // multiple lanes spawning simultaneously at scene start compounds, and
    // this only runs once per lane ever, so the extra frames cost nothing.
    private IEnumerator SpawnBowlingLaneRoutine(BowlingLaneInstance lane)
    {
        if (lane.HeadPinAnchor == null || lane.PinPrefab == null || lane.PinConfig == null)
        {
            Debug.LogWarning($"[LobbySpawner] Bowling lane '{lane.LaneLabel}' is missing HeadPinAnchor/PinPrefab/PinConfig — skipping.");
            yield break;
        }

        lane.SpawnedPins.Clear();
        for (int i = 0; i < _bowlingPinSlotGrid.Length; i++)
        {
            Vector3 localOffset = GetBowlingPinSlotLocalOffset(i, lane.PinSpacing);
            Vector3 worldPos = lane.HeadPinAnchor.position + lane.HeadPinAnchor.rotation * localOffset;
            Quaternion worldRot = lane.HeadPinAnchor.rotation;

            GameObject pinObj = Instantiate(lane.PinPrefab, worldPos, worldRot, _propParent);
            pinObj.transform.localScale = lane.PinScale;
            pinObj.name = $"{lane.LaneLabel}_Pin_{i}";
            NetworkObject netObj = pinObj.GetComponent<NetworkObject>();
            if (netObj != null)
                InstanceFinder.ServerManager.Spawn(netObj);

            Pin pin = pinObj.GetComponent<Pin>();
            if (pin != null)
            {
                pin.SetSlotTransform(worldPos, worldRot);
                lane.SpawnedPins.Add(pin);
            }
            else
            {
                Debug.LogWarning($"[LobbySpawner] Bowling lane '{lane.LaneLabel}' PinPrefab has no Pin component.");
            }

            yield return null;
        }

        // Balls spawn once and are never despawned — each roll just resets
        // the ball that was thrown back to its own holder anchor (see
        // BowlingBall.ServerRegisterRollComplete).
        lane.SpawnedBalls.Clear();
        if (lane.BallSlots != null)
        {
            foreach (var ballSlot in lane.BallSlots)
            {
                if (ballSlot.BallPrefab == null || ballSlot.HolderAnchor == null)
                {
                    Debug.LogWarning($"[LobbySpawner] Bowling lane '{lane.LaneLabel}' has a ball slot missing BallPrefab or HolderAnchor — skipping.");
                    continue;
                }

                GameObject ballObj = Instantiate(ballSlot.BallPrefab, ballSlot.HolderAnchor.position, ballSlot.HolderAnchor.rotation, _propParent);
                ballObj.transform.localScale = ballSlot.Scale;
                ballObj.name = $"{lane.LaneLabel}_{ballSlot.BallPrefab.name}";
                NetworkObject ballNetObj = ballObj.GetComponent<NetworkObject>();
                if (ballNetObj != null)
                    InstanceFinder.ServerManager.Spawn(ballNetObj);

                BowlingBall ball = ballObj.GetComponent<BowlingBall>();
                if (ball != null)
                {
                    // Both are scene references (an anchor Transform and a
                    // BowlingGameController) that the ball prefab itself
                    // can't hold — assigned here in code instead. See the
                    // field comments on BowlingBall.
                    ball.SetHolderAnchor(ballSlot.HolderAnchor);
                    ball.SetGameController(lane.GameController);
                    lane.SpawnedBalls.Add(ball);
                }
                else
                    Debug.LogWarning($"[LobbySpawner] Bowling lane '{lane.LaneLabel}' ball prefab has no BowlingBall component.");

                yield return null;
            }
        }

        ApplyBowlingPattern(lane, GetActiveBowlingPatternIndex(lane.PinConfig));

        // Signals BowlingGameController.ServerStartGameRoutine that this
        // lane is actually fully spawned AND racked — not just "has at
        // least one pin," which is all a pin-count check could tell it
        // while this coroutine is still mid-spawn across frames.
        lane.IsSetupComplete = true;
    }

    // Hides every fallen pin in a lane together, timed from whichever pin
    // fell LAST rather than each pin's own fall time — without this, pins
    // vanish one at a time as their individual fall is confirmed, which
    // looks staggered/random for a multi-pin hit. Any time a NEW pin joins
    // the "awaiting hide" group this frame, the countdown restarts; once it
    // elapses with no new falls, every awaiting pin in the lane hides at once.
    private void UpdateBowlingPinGroupHide(BowlingLaneInstance lane)
    {
        if (lane.SpawnedPins == null || lane.SpawnedPins.Count == 0) return;

        int awaitingCount = 0;
        foreach (Pin pin in lane.SpawnedPins)
            if (pin != null && pin.IsAwaitingHide) awaitingCount++;

        if (awaitingCount == 0)
        {
            lane.LastAwaitingHideCount = 0;
            return;
        }

        if (awaitingCount > lane.LastAwaitingHideCount)
            lane.PinHideTimer = lane.PinGroupHideDelay;

        lane.LastAwaitingHideCount = awaitingCount;

        lane.PinHideTimer -= Time.deltaTime;
        if (lane.PinHideTimer <= 0f)
        {
            foreach (Pin pin in lane.SpawnedPins)
                if (pin != null && pin.IsAwaitingHide) pin.ServerHideNow();
            lane.LastAwaitingHideCount = 0;
        }
    }

    private int GetActiveBowlingPatternIndex(BowlingPinConfig config)
    {
        if (config == null || config.Patterns == null || config.Patterns.Count == 0) return -1;
        return config.RandomizePattern
            ? Random.Range(0, config.Patterns.Count)
            : Mathf.Clamp(config.ActivePatternIndex, 0, config.Patterns.Count - 1);
    }

    // Sets every pin in this lane standing or hidden according to which
    // slots the given pattern includes — never despawns/spawns a pin, just
    // toggles existing ones. Direct application of the pool-table lesson:
    // where SwitchPoolPattern had to despawn/respawn because ball prefabs
    // and counts vary by pattern, every bowling pattern uses the SAME 10
    // physical pin objects, so a pattern switch is pure state toggling with
    // zero network spawn/despawn calls — there's no burst to spread across
    // frames here because there's no burst-prone work to begin with.
    private void ApplyBowlingPattern(BowlingLaneInstance lane, int patternIndex)
    {
        if (lane.PinConfig == null || lane.PinConfig.Patterns == null ||
            patternIndex < 0 || patternIndex >= lane.PinConfig.Patterns.Count)
        {
            Debug.LogWarning($"[LobbySpawner] Bowling lane '{lane.LaneLabel}' — invalid pattern index {patternIndex}.");
            return;
        }

        BowlingPinPattern pattern = lane.PinConfig.Patterns[patternIndex];
        HashSet<int> activeSlots = new HashSet<int>(pattern.ActiveSlotIndices);

        // Tracked so BowlingGameController (and eventually the scoreboard
        // UI) can know which pattern is actually racked right now without
        // re-deriving it — every path that changes the rack (initial spawn,
        // frame reset, an explicit pattern switch) all funnel through here.
        lane.ActivePatternIndex = patternIndex;

        for (int i = 0; i < lane.SpawnedPins.Count; i++)
        {
            Pin pin = lane.SpawnedPins[i];
            if (pin == null) continue;

            if (activeSlots.Contains(i))
                pin.ServerSetStanding();
            else
                pin.ServerSetHidden();
        }
    }

    // --- Bowling Reset (called by a future BowlingResetStation / end-of-frame logic) ---
    // No [Server] attribute for the same reason as the pool reset methods —
    // LobbySpawner is a plain MonoBehaviour; callers confirm IsServerInitialized.

    /// Re-racks a lane under whichever pattern is currently configured as
    /// active on its PinConfig (or picks a new random one if RandomizePattern
    /// is set) — used for "new frame" resets. Pure state toggling, no
    /// coroutine needed since there's no spawn/despawn involved (see
    /// ApplyBowlingPattern).
    public void ResetBowlingLane(int laneIndex)
    {
        if (_bowlingLanes == null || laneIndex < 0 || laneIndex >= _bowlingLanes.Count)
        {
            Debug.LogWarning($"[LobbySpawner] ResetBowlingLane — invalid lane index {laneIndex}.");
            return;
        }
        BowlingLaneInstance lane = _bowlingLanes[laneIndex];
        ApplyBowlingPattern(lane, GetActiveBowlingPatternIndex(lane.PinConfig));
    }

    /// Switches a lane to a specific pattern by index (e.g. a player-facing
    /// button choosing "Big Four" for a practice round) rather than whatever
    /// PinConfig currently has configured as active/random.
    public void SwitchBowlingPattern(int laneIndex, int patternIndex)
    {
        if (_bowlingLanes == null || laneIndex < 0 || laneIndex >= _bowlingLanes.Count)
        {
            Debug.LogWarning($"[LobbySpawner] SwitchBowlingPattern — invalid lane index {laneIndex}.");
            return;
        }
        ApplyBowlingPattern(_bowlingLanes[laneIndex], patternIndex);
    }

    // Used by BowlingGameController to find its lane's spawned pins/balls —
    // same purpose and pattern as GetHoldingRack() above, just index-based
    // since bowling lanes aren't 1:1 with a single scene setup the way the
    // pool table is.
    public BowlingLaneInstance GetBowlingLane(int laneIndex)
    {
        if (_bowlingLanes == null || laneIndex < 0 || laneIndex >= _bowlingLanes.Count) return null;
        return _bowlingLanes[laneIndex];
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

    // Tracks the in-progress SwitchPoolPattern coroutine, if any — lets a
    // re-press stop a still-running switch cleanly instead of letting two
    // coroutines overlap against the same ball list. See
    // LobbySpawner.SwitchPoolPatternRoutine.
    [System.NonSerialized] public Coroutine ActiveSwitchRoutine;
}

[System.Serializable]
public class PoolCueSlot
{
    public GameObject CuePrefab;
    [Tooltip("Scene Transform marking where this cue spawns — e.g. a spot on a wall-mounted cue rack.")]
    public Transform Anchor;
    public Vector3 Scale = Vector3.one;
}

[System.Serializable]
public class BowlingLaneInstance
{
    [Header("Identity")]
    public string LaneLabel;

    [Header("Pin Deck")]
    [Tooltip("Pattern data — which of the 10 standard slots are active. See BowlingPinConfig's class comment for the index-to-pin map.")]
    public BowlingPinConfig PinConfig;
    [Tooltip("Empty Transform positioned exactly where the #1 (head) pin sits, with local forward pointing down the lane toward the back row. All 10 pin positions are generated from this.")]
    public Transform HeadPinAnchor;
    public GameObject PinPrefab;
    [Tooltip("Distance between adjacent pin centers. Standard bowling spacing is 0.3048m (12 inches) — tune to match your pin prefab's actual footprint.")]
    public float PinSpacing = 0.3048f;
    public Vector3 PinScale = Vector3.one;

    [Header("Balls")]
    [Tooltip("One entry per ball — prefab plus the anchor it returns to after a roll.")]
    public List<BowlingBallSlot> BallSlots = new List<BowlingBallSlot>();

    [Header("Scoring")]
    [Tooltip("Optional — the scene BowlingGameController scoring this lane. Pushed onto each spawned ball at spawn time (see SpawnBowlingLaneRoutine) since a ball prefab can't hold a scene reference directly. Leave unassigned for a pure free-play lane with no scoring.")]
    public BowlingGameController GameController;

    [Header("Pin Cleanup")]
    [Tooltip("Seconds after the LAST pin falls before every fallen pin in this lane disappears together — see LobbySpawner.UpdateBowlingPinGroupHide(). Replaces the old per-pin disappear delay so pins don't vanish one at a time in a staggered order.")]
    public float PinGroupHideDelay = 2f;

    // Runtime-only, populated by SpawnBowlingLaneRoutine() — used by
    // ApplyBowlingPattern()/ResetBowlingLane()/SwitchBowlingPattern() so
    // they don't need to re-discover the spawned pins/balls each time.
    [System.NonSerialized] public List<Pin> SpawnedPins = new List<Pin>();
    [System.NonSerialized] public List<BowlingBall> SpawnedBalls = new List<BowlingBall>();

    // Set true only once SpawnBowlingLaneRoutine has fully finished — pins
    // AND balls spawned, pattern applied. BowlingGameController waits on
    // this rather than just "SpawnedPins.Count > 0," which could be true
    // mid-spawn with the rack only partially set up (see the first-roll
    // 0-pins bug this fixed).
    [System.NonSerialized] public bool IsSetupComplete;

    // Set by ApplyBowlingPattern() every time this lane's rack changes
    // (initial spawn, frame reset, or an explicit pattern switch) — tracks
    // which BowlingPinConfig.Patterns entry is actually racked right now.
    // -1 until the first pattern is applied.
    [System.NonSerialized] public int ActivePatternIndex = -1;

    // Runtime-only state for UpdateBowlingPinGroupHide()'s countdown.
    [System.NonSerialized] public float PinHideTimer;
    [System.NonSerialized] public int LastAwaitingHideCount;
}

[System.Serializable]
public class BowlingBallSlot
{
    public GameObject BallPrefab;
    [Tooltip("Where this ball spawns and returns to after each roll.")]
    public Transform HolderAnchor;
    public Vector3 Scale = Vector3.one;
}
