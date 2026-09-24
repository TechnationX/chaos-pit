// LobbySpawner
using FishNet;
using FishNet.Broadcast;
using FishNet.Connection;
using FishNet.Object;
using FishNet.Observing;
using FishNet.Transporting;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
public class LobbySpawner : MonoBehaviour
{
    [Header("Configs")]
    [SerializeField] private PlayerSpawnConfig _playerSpawnConfig;
    [Tooltip("Scene anchor Transforms marking where players spawn in. Any count works — drag in as many empty GameObjects as you want spawn locations. Order doesn't matter; spawn selection is randomized (see RegisterSpawnPoints/TryGetSpawnPoint).")]
    [SerializeField] private List<Transform> _playerSpawnPoints;
    [SerializeField] private FurnitureSpawnConfig _furnitureSpawnConfig;
    [SerializeField] private PropSpawnConfig _propSpawnConfig;
    [Tooltip("Each entry pairs a Prop Setup Config (piece list/patterns) with the scene Transform that anchors it — e.g. an empty GameObject placed where the chess table sits.")]
    [SerializeField] private List<PropSetupInstance> _propSetups;
    [Tooltip("Pool rack setups — the rack prefab (e.g. BillardBall_Triangle) supplies its own numbered ball-slot children, unlike the piece-list based Prop Setups above.")]
    [SerializeField] private List<PoolSetupInstance> _poolSetups;
    [Tooltip("Bowling lane setups — pins are generated procedurally from a head-pin anchor + spacing rather than authored per-slot like the pool rack, see BowlingPinConfig.")]
    [SerializeField] private List<BowlingLaneInstance> _bowlingLanes;
    [Tooltip("Stage setups — each pairs a Stage Setup Config (the act list: mic stand + optional instrument per act) with the scene Anchor Transform and SpeakerAnchors list for that physical stage.")]
    [SerializeField] private List<StageSetupInstance> _stageSetups;
    [Header("Parents")]
    [SerializeField] private Transform _furnitureParent;
    [SerializeField] private Transform _propParent;
    [Tooltip("Root Transform whose whole hierarchy gets scanned for scene-placed NetworkObjects that carry a NetworkObserver (see RegisterSceneManualRevealRoutine) — assign LobbyRoot. These objects (bowling lanes, sittable furniture, the pool rack) are scattered across BowlingRoot/PoolRoot/Bar&TableRoom rather than all living under one dedicated parent like _furnitureParent, so this scans broadly instead of per-area.")]
    [SerializeField] private Transform _manualRevealSceneRoot;
    private List<Vector3> _availableSpawnPoints = new List<Vector3>();
    private List<Quaternion> _availableSpawnRotations = new List<Quaternion>();
    private bool _spawnListenerRegistered = false;
    public static LobbySpawner Instance { get; private set; }
    private int _spawnPointIndex = 0;
    private HashSet<int> _spawnedConnections = new HashSet<int>();
    // Two independent "is this connection actually safe to spawn for" signals
    // — a connection's player only spawns once BOTH are true for it. See
    // OnClientLoadedStartScenes / OnLobbyReadyBroadcast / TrySpawnIfReady for
    // why neither one alone is sufficient (each was tried alone first and
    // each broke a different case).
    private HashSet<int> _startScenesLoadedConnections = new HashSet<int>();
    private HashSet<int> _lobbyReadyConnections = new HashSet<int>();
    // Every currently-spawned NetworkObject that uses ManualRevealCondition
    // (chess pieces; pool — rack, cue ball, cues, holding rack, numbered
    // balls; bowling pins and balls; see RegisterManualRevealObject). A new
    // connection starts with none of these visible and RevealManualObjectsToConnection
    // gradually reveals them one at a time, instead of letting FishNet's
    // automatic new-connection catch-up burst (ServerManager.Objects.RebuildObservers)
    // send all of them at once, which was found to permanently stall that
    // connection's reliable channel — see the comment on RevealManualObjectsToConnection.
    private List<NetworkObject> _manualRevealObjects = new List<NetworkObject>();
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
            RunServerSpawnSequence();
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
            RunServerSpawnSequence();
        }
        else
        {
            InstanceFinder.ServerManager.OnServerConnectionState += OnServerStarted;
        }
    }
    // Shared by all three entry points (Start, WaitForServerManager, OnServerStarted) so the
    // spawn sequencing only has to live in one place.
    //
    // RegisterSpawnListener() and the OnRemoteConnectionState subscription run
    // FIRST now, before any spawning starts — this used to run last, which was
    // fine back when every Spawn*() call below was synchronous (so "last" still
    // meant "the same frame"). Now that spawning is spread across several
    // frames (see SpawnFurnitureRoutine etc. below), leaving the listener
    // registration for last would open a window where a fast-connecting host's
    // own LobbyReadyBroadcast could arrive before anything is listening for
    // it — reintroducing the exact client-join race LobbyReadyBroadcast was
    // built to close (see LobbyReadyBroadcast.cs). Registering first closes
    // that window regardless of how long the spawn sequence below takes.
    private void RunServerSpawnSequence()
    {
        RegisterSpawnPoints();
        RegisterSpawnListener();
        InstanceFinder.ServerManager.OnRemoteConnectionState += OnRemoteConnectionState;
        StartCoroutine(SpawnAllRoutine());
    }

    // Runs the furniture/props/prop-setups/pool spawn passes one after another
    // (each spread across frames internally — see each Routine below), then
    // kicks off bowling last. Bowling isn't chained with yield return here
    // since SpawnBowlingSetups() already fires an independent coroutine per
    // lane (SpawnBowlingLaneRoutine) rather than doing its own work inline —
    // nothing else in this sequence depends on bowling finishing first.
    //
    // WHY THIS EXISTS: every one of these used to run as a single synchronous
    // burst of ServerManager.Spawn() calls — furniture + generic props + all
    // chess pieces + an entire pool setup (rack, cue ball, cues, holding rack,
    // up to 15 racked balls) could easily total 60-100+ reliable network
    // messages in one server frame. SwitchPoolPatternRoutine's own comment
    // (below) already documented that bursting just ~30 such messages in one
    // frame reliably stalled a connected client's reliable channel entirely —
    // this was the same bug, just bigger, and firing on every Lobby load
    // instead of only on a manual pattern switch. Spreading each pass across
    // frames (one spawn per frame, same granularity as SpawnBowlingLaneRoutine)
    // avoids the burst the same way bowling already did.
    private IEnumerator SpawnAllRoutine()
    {
        yield return StartCoroutine(RegisterSceneFurnitureRoutine());
        yield return StartCoroutine(RegisterSceneManualRevealRoutine());
        yield return StartCoroutine(SpawnFurnitureRoutine());
        yield return StartCoroutine(SpawnPropsRoutine());
        yield return StartCoroutine(SpawnPropSetupsRoutine());
        yield return StartCoroutine(SpawnPoolSetupsRoutine());
        yield return StartCoroutine(SpawnStageSetupsRoutine());
        SpawnBowlingSetups();
    }
    // --- Scene-Placed Furniture (manual reveal registration) ---
    // FurnitureSpawnConfig/SpawnFurnitureRoutine below exist for
    // runtime-driven furniture spawning, but that config is currently empty
    // (not in use yet — confirmed 2026-09-17) — today's furniture is
    // hand-placed directly in the Lobby scene as prefab instances under
    // _furnitureParent instead. As FishNet scene objects they're already
    // spawned by the time the server starts, but a late-joining connection
    // still catches up on all of them (plus every other already-spawned
    // NetworkObject) in one automatic, un-paced burst — the exact same class
    // of bug already fixed for chess/pool/bowling (see the Manual Reveal
    // region above and RegisterManualRevealObject's own comment). Only
    // Armchair_Classic, Coffee_table_1, Sofa_2Seat, and Table currently carry
    // a NetworkObject component (and now NetworkObserver +
    // ManualRevealCondition, added alongside this) — Stool_1_New,
    // Stool_4_New, and TV_Modern_New have no NetworkObject at all, so they're
    // untouched by any of this either way.
    //
    // IMPORTANT ASSUMPTION: this only finds furniture that's actually parented
    // under _furnitureParent in the scene. If the hand-placed instances live
    // somewhere else in the hierarchy, this scan finds nothing — meaning the
    // NetworkObserver added to those 4 prefabs would default them to
    // invisible-until-revealed with nothing ever revealing them, hiding that
    // furniture from everyone. Worth confirming in the Editor after this
    // lands (select _furnitureParent in the Inspector and check its children)
    // before relying on this fix.
    private IEnumerator RegisterSceneFurnitureRoutine()
    {
        // One frame so any same-frame FishNet scene-object spawning (part of
        // the server starting) has finished before we go looking.
        yield return null;

        if (_furnitureParent == null) yield break;

        foreach (NetworkObject netObj in _furnitureParent.GetComponentsInChildren<NetworkObject>(true))
            RegisterManualRevealObject(netObj);
    }
    // --- Scene-Placed Manual-Reveal Objects (bowling lanes, sittable
    // furniture, pool rack) ---
    // RegisterSceneFurnitureRoutine above only scans _furnitureParent, which
    // only covers furniture that's actually parented there — an audit of
    // every prefab placed in the Lobby (2026-09-18) found several more
    // networked, scene-placed objects that carry NO NetworkObserver at all
    // and are scattered under BowlingRoot, PoolRoot, and Bar&TableRoom
    // instead: BowlingLaneL/R, LanePanel's buttons (nested inside the
    // lanes), the MedievalTavernPack bar stool (Stool_02, has a Sittable
    // component), the MegaSportPack pool ball rack (Billard_Pool_8Ball — 9
    // separate NetworkObjects, one per ball), and three FoundryStudios sofas
    // (Sofa2, Sofa2_Small, Sofa_Circular, also Sittable). All of them are
    // now fixed at the prefab level (NetworkObserver + ManualRevealCondition
    // added, same as the furniture fix above) — this routine is what wires
    // them into the reveal system, since none of them live under
    // _furnitureParent.
    //
    // Scans from _manualRevealSceneRoot (assign LobbyRoot in the Inspector)
    // instead of a narrow per-area parent, since these objects don't share
    // one — LobbyRoot is the closest common ancestor for all of them. To
    // avoid the same "silently invisible forever" risk this fix is patching,
    // and to avoid re-registering things that aren't part of this system on
    // purpose (LobbyRoot's own NetworkObject, GameRoomManager, and other
    // core scene NetworkObjects have no NetworkObserver at all, by design —
    // they need to stay visible to everyone immediately), the scan only acts
    // on a NetworkObject that already HAS a NetworkObserver component. That
    // makes it safe to scan broadly: anything without a NetworkObserver is
    // silently skipped rather than logging RegisterManualRevealObject's
    // "check the prefab" warning, and anything that DOES have one (meaning
    // it's meant to be manually revealed) still gets that warning if its
    // NetworkObserver isn't actually configured with ManualRevealCondition —
    // a real misconfiguration worth flagging.
    private IEnumerator RegisterSceneManualRevealRoutine()
    {
        yield return null;

        if (_manualRevealSceneRoot == null) yield break;

        foreach (NetworkObject netObj in _manualRevealSceneRoot.GetComponentsInChildren<NetworkObject>(true))
        {
            if (netObj.GetComponent<NetworkObserver>() == null) continue;
            RegisterManualRevealObject(netObj);
        }
    }
    private void Awake()
    {
        Instance = this;
        PrewarmConvexMeshColliders();
    }
    // --- Physics Warm-up ---
    // Pre-bakes convex hull data for every convex MeshCollider referenced by
    // any Prop Setup pattern (chess pieces are the practical case — their
    // MeshCollider sits directly on the real high-poly render mesh, see the
    // collider-placement fix history in the project docs). Physics.BakeMesh
    // cooks the hull once per unique mesh and caches the result; any later
    // MeshCollider that references that same mesh (with a matching convex
    // flag) reuses the cached hull instead of PhysX re-running hull
    // generation from scratch on every single Instantiate. Without this, a
    // client who joins after the lobby is already set up has to eat that
    // cook cost for all 32 chess-piece instances back-to-back — on top of
    // every other prop it's catching up on — which is what turned into a
    // many-second stall on join. Runs in Awake() (every peer, not just the
    // server — see the class-level note on LobbySpawner not being a
    // NetworkBehaviour) so it happens before any spawn, whether the
    // server's own initial spawn or a late-joining client's catch-up burst,
    // can hit the uncached cost.
    private void PrewarmConvexMeshColliders()
    {
        if (_propSetups == null) return;

        var bakedMeshIds = new HashSet<int>();

        foreach (var instance in _propSetups)
        {
            PropSetupConfig setup = instance?.Config;
            if (setup?.Patterns == null) continue;

            foreach (var pattern in setup.Patterns)
            {
                if (pattern?.Entries == null) continue;

                foreach (var entry in pattern.Entries)
                {
                    if (entry?.Prefab == null) continue;

                    foreach (var col in entry.Prefab.GetComponentsInChildren<MeshCollider>(true))
                    {
                        if (!col.convex || col.sharedMesh == null) continue;

                        int meshId = col.sharedMesh.GetInstanceID();
                        if (bakedMeshIds.Contains(meshId)) continue;

                        Physics.BakeMesh(meshId, true, col.cookingOptions);
                        bakedMeshIds.Add(meshId);
                    }
                }
            }
        }
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
        RunServerSpawnSequence();
    }
    private void OnRemoteConnectionState(NetworkConnection conn, RemoteConnectionStateArgs args)
    {
        if (args.ConnectionState == RemoteConnectionState.Stopped)
            PlayerProfileManager.Instance.UnregisterPlayer(conn);
    }
    private void RegisterSpawnListener()
    {
        if (_spawnListenerRegistered) return;
        _spawnListenerRegistered = true;
        InstanceFinder.SceneManager.OnClientLoadedStartScenes += OnClientLoadedStartScenes;
        InstanceFinder.ServerManager.RegisterBroadcast<LobbyReadyBroadcast>(OnLobbyReadyBroadcast);
    }
    // One of two required signals — see TrySpawnIfReady. FishNet fires this
    // once IT internally considers a connection to have loaded its start
    // scenes, which turned out to be a genuine precondition for
    // ServerManager.Spawn() to be safe (skipping it is what caused the
    // SyncTypes_Preinitialize NullReferenceException flood the first time
    // this project hit it) — NOT just an event we could freely replace.
    // Originally this method did the actual spawning by itself, which broke
    // for a remote client (see OnLobbyReadyBroadcast's comment). Swapping the
    // trigger to LobbyReadyBroadcast alone then broke the HOST's own spawn
    // instead: on the host's loopback connection, LobbyReadyBroadcast can
    // arrive before FishNet's own internal start-scenes-loaded flag flips
    // true, so spawning off that alone re-triggered the exact same NRE with
    // a new cause. Requiring both together is what actually closes both gaps.
    private void OnClientLoadedStartScenes(FishNet.Connection.NetworkConnection conn, bool asServer)
    {
        if (!asServer) return;
        // Diagnostic for the intermittent "client hangs on join" bug — see
        // TrySpawnIfReady's own comment for why this and the broadcast below
        // are each only half of what's needed to spawn a connection's player.
        Debug.Log($"[LobbySpawner] OnClientLoadedStartScenes — ClientId: {conn.ClientId}");
        _startScenesLoadedConnections.Add(conn.ClientId);
        TrySpawnIfReady(conn);
        if (!_spawnedConnections.Contains(conn.ClientId))
            StartCoroutine(WarnIfStillNotSpawned(conn));
    }
    // The other required signal — see TrySpawnIfReady. Fires once a
    // connection's own client confirms that its local Lobby scene has
    // genuinely finished loading (see JoinSessionScreen.LoadLobby /
    // CreateSessionScreen.LoadLobby for where this gets sent). Needed
    // because nothing in this project loads Lobby through FishNet's own
    // scene system — both screens use plain SceneManager.LoadSceneAsync — so
    // OnClientLoadedStartScenes above has no relationship to whether the
    // client's Lobby scene actually exists locally yet. In testing, relying
    // on OnClientLoadedStartScenes alone let the server spawn/reveal objects
    // for a remote client about a second before that client's Lobby scene
    // existed, which is what caused the original "client joins, then
    // silently receives nothing ever again" symptom.
    private void OnLobbyReadyBroadcast(NetworkConnection conn, LobbyReadyBroadcast msg, Channel channel)
    {
        // Diagnostic for the intermittent "client hangs on join" bug — see
        // TrySpawnIfReady's own comment for why this and
        // OnClientLoadedStartScenes above are each only half of what's
        // needed to spawn a connection's player.
        Debug.Log($"[LobbySpawner] OnLobbyReadyBroadcast — ClientId: {conn.ClientId}");
        _lobbyReadyConnections.Add(conn.ClientId);
        TrySpawnIfReady(conn);
        if (!_spawnedConnections.Contains(conn.ClientId))
            StartCoroutine(WarnIfStillNotSpawned(conn));
    }

    // Diagnostic-only watchdog for the intermittent "second player hangs on
    // join" bug — TrySpawnIfReady requires BOTH OnClientLoadedStartScenes and
    // OnLobbyReadyBroadcast to have fired for a connection (see its own
    // comment), and until now gave no indication at all if one of the two
    // just never arrived — the connection would sit there forever with
    // nothing in the console to explain why. This doesn't fix or retry
    // anything; a few seconds after the FIRST of the two signals comes in,
    // it checks whether the connection is still waiting on the other one and
    // names exactly which is missing, so the next time this happens the
    // console says what's actually stuck instead of the join just silently
    // never completing.
    private IEnumerator WarnIfStillNotSpawned(NetworkConnection conn)
    {
        int clientId = conn.ClientId;
        yield return new WaitForSeconds(8f);

        if (_spawnedConnections.Contains(clientId)) yield break; // resolved itself — nothing to report
        if (!InstanceFinder.ServerManager.Clients.ContainsKey(clientId)) yield break; // disconnected in the meantime

        bool startScenesLoaded = _startScenesLoadedConnections.Contains(clientId);
        bool lobbyReady = _lobbyReadyConnections.Contains(clientId);

        string missing = !lobbyReady
            ? "the client's own LobbyReadyBroadcast never arrived — its Lobby scene load may have stalled, or the broadcast was sent before this server finished registering its handler (see RegisterSpawnListener)"
            : !startScenesLoaded
                ? "FishNet's own OnClientLoadedStartScenes never fired for this connection"
                : "both signals ARE present but the spawn still didn't happen — check for a swallowed exception inside TrySpawnIfReady";

        Debug.LogWarning($"[LobbySpawner] ClientId {clientId} still not spawned 8s after its first ready signal — " +
            $"startScenesLoaded: {startScenesLoaded}, lobbyReady: {lobbyReady}. Likely cause: {missing}.");
    }
    // Spawns a connection's player (and starts its manual-reveal coroutine)
    // the moment BOTH OnClientLoadedStartScenes and OnLobbyReadyBroadcast
    // have fired for it, regardless of which one happens to arrive second —
    // see the comments on each for why neither alone is sufficient.
    private void TrySpawnIfReady(NetworkConnection conn)
    {
        // Diagnostic for the intermittent "client hangs on join" bug — logs
        // exactly which of the two gate checks is holding a connection back,
        // instead of the method just silently returning. See
        // WarnIfStillNotSpawned for the higher-level "still stuck after 8s"
        // summary that reads off the same two HashSets.
        if (_spawnedConnections.Contains(conn.ClientId))
        {
            Debug.Log($"[LobbySpawner] TrySpawnIfReady — ClientId {conn.ClientId} already spawned, skipping.");
            return;
        }
        if (!_startScenesLoadedConnections.Contains(conn.ClientId))
        {
            Debug.Log($"[LobbySpawner] TrySpawnIfReady — ClientId {conn.ClientId} waiting on OnClientLoadedStartScenes (lobbyReady: {_lobbyReadyConnections.Contains(conn.ClientId)}).");
            return;
        }
        if (!_lobbyReadyConnections.Contains(conn.ClientId))
        {
            Debug.Log($"[LobbySpawner] TrySpawnIfReady — ClientId {conn.ClientId} waiting on OnLobbyReadyBroadcast (startScenesLoaded: {_startScenesLoadedConnections.Contains(conn.ClientId)}).");
            return;
        }

        Debug.Log($"[LobbySpawner] TrySpawnIfReady — ClientId {conn.ClientId} has both signals, spawning now.");
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

        Debug.Log($"[LobbySpawner] TrySpawnIfReady — ClientId {conn.ClientId} spawn complete, displayName: {displayName}.");

        // Chess pieces are hidden from this connection by default (see the
        // Manual Reveal region above) — reveal them gradually now instead of
        // letting FishNet's own automatic catch-up burst try to send all of
        // them at once.
        StartCoroutine(RevealManualObjectsToConnection(conn));
    }
    private void OnDestroy()
    {
        if (InstanceFinder.ServerManager != null)
            InstanceFinder.ServerManager.OnServerConnectionState -= OnServerStarted;
        if (InstanceFinder.SceneManager != null)
            InstanceFinder.SceneManager.OnClientLoadedStartScenes -= OnClientLoadedStartScenes;
        if (InstanceFinder.ServerManager != null)
        {
            InstanceFinder.ServerManager.OnRemoteConnectionState -= OnRemoteConnectionState;
            InstanceFinder.ServerManager.UnregisterBroadcast<LobbyReadyBroadcast>(OnLobbyReadyBroadcast);
        }
        _spawnedConnections.Clear();
        _startScenesLoadedConnections.Clear();
        _lobbyReadyConnections.Clear();
    }
    // --- Manual Reveal (chess pieces, pool, bowling pins/balls) ---
    // Chess pieces, pool objects (rack, cue ball, cues, holding rack, numbered
    // balls), and bowling pins/balls carry a NetworkObserver +
    // ManualRevealCondition on their prefab (see ManualRevealCondition.cs)
    // instead of using FishNet's default always-visible behavior. That
    // condition starts an object invisible to every connection until
    // something explicitly reveals it. This exists because of a confirmed
    // failure mode: FishNet syncs every already-spawned NetworkObject to a
    // newly-joined connection in one automatic burst
    // (ServerManager.Objects.RebuildObservers, fired off
    // SceneManager.OnClientLoadedStartScenes) — with ~32 chess pieces and/or
    // ~20 pool objects already on the tables, that burst was found to
    // permanently stall the joining client's reliable channel (it received
    // literally nothing afterward, not even its own player object).
    // SwitchPoolPatternRoutine below hit the same class of problem for a live
    // pattern switch and fixed it by spreading the spawn/despawn calls across
    // frames — this does the equivalent for the catch-up burst, which isn't
    // project-authored code and so can't be paced directly: the objects are
    // held back from a new connection by default, then revealed to it one at
    // a time in RevealManualObjectsToConnection.
    //
    // Bowling pins/balls were added to this list even though 2 lanes' worth
    // (20 pins + 8 balls) is well under the volume that caused the original
    // stall on its own — the point is to keep every dynamically-spawned
    // object off FishNet's automatic catch-up path entirely, so a lane's
    // objects can never compound with a still-active chess/pool burst on the
    // same connection. The bowling LANES themselves (BowlingLaneL/R) are
    // NOT on this list — they're placed directly in the scene, not spawned
    // via ServerManager.Spawn(), and FishNet syncs scene objects through a
    // different (lighter-weight) registration path that this mechanism
    // doesn't apply to. Furniture is also NOT on this list — it hasn't shown
    // this failure mode, so it doesn't carry the extra prefab complexity
    // unless that changes.
    //
    // Called right after spawning any object that carries a
    // ManualRevealCondition. Immediately reveals the object to every
    // currently-connected connection (host included) so nothing changes for
    // clients that are already here — only a connection that joins AFTER this
    // object already exists goes through the gradual reveal below.
    private void RegisterManualRevealObject(NetworkObject netObj)
    {
        if (netObj == null) return;
        // Guards against double-registration — needed now that furniture can
        // reach this method two ways: once via RegisterSceneFurnitureRoutine
        // for whatever's already hand-placed under _furnitureParent, and
        // again via SpawnFurnitureRoutine if/when FurnitureSpawnConfig is
        // ever populated and that runtime-spawn path is turned on. Without
        // this, the same NetworkObject could pile up multiple entries in
        // _manualRevealObjects, wasting a frame per duplicate in
        // RevealManualObjectsToConnection for every future joiner.
        if (_manualRevealObjects.Contains(netObj)) return;
        ManualRevealCondition condition = GetManualRevealCondition(netObj);
        if (condition == null)
        {
            Debug.LogWarning($"[LobbySpawner] {netObj.name} has no NetworkObserver + ManualRevealCondition — it will be invisible to everyone. Check the prefab.");
            return;
        }

        _manualRevealObjects.Add(netObj);

        foreach (NetworkConnection conn in InstanceFinder.ServerManager.Clients.Values)
            condition.Reveal(conn);
    }
    private ManualRevealCondition GetManualRevealCondition(NetworkObject netObj)
    {
        NetworkObserver observer = netObj.GetComponent<NetworkObserver>();
        if (observer == null) return null;
        return observer.GetObserverCondition<ManualRevealCondition>() as ManualRevealCondition;
    }
    // Gradually reveals every currently-tracked manual-reveal object (chess
    // pieces, pool) to a newly-joined connection, one object per frame — see
    // the class comment above for why this needs to be gradual at all, and
    // SwitchPoolPatternRoutine's comment below for the original version of
    // this same "don't burst reliable messages in one frame" lesson. Started
    // from OnClientLoadedStartScenes once that connection's own player object
    // has been spawned.
    private IEnumerator RevealManualObjectsToConnection(NetworkConnection conn)
    {
        // Snapshot to an array — SwitchPoolPattern (or a future equivalent
        // for chess) could add/remove entries in _manualRevealObjects while
        // this is running for an unrelated connection.
        NetworkObject[] objects = _manualRevealObjects.ToArray();
        foreach (NetworkObject netObj in objects)
        {
            if (netObj == null) continue;
            ManualRevealCondition condition = GetManualRevealCondition(netObj);
            condition?.Reveal(conn);
            yield return null;
        }
    }
    // --- Player Spawn Points ---
    // Anchors now live as real Transforms in the scene (_playerSpawnPoints) instead of raw
    // Position/Rotation data on PlayerSpawnConfig. _availableSpawnPoints/_availableSpawnRotations
    // hold a shuffled copy of the anchors; TryGetSpawnPoint() actually consumes that shuffled
    // order now (previously it cycled the unshuffled source list directly, so the shuffle below
    // was computed but never used).
    private void RegisterSpawnPoints()
    {
        _availableSpawnPoints.Clear();
        _availableSpawnRotations.Clear();
        foreach (var anchor in _playerSpawnPoints)
        {
            if (anchor == null) continue;
            _availableSpawnPoints.Add(anchor.position);
            _availableSpawnRotations.Add(anchor.rotation);
        }
        _spawnPointIndex = 0;
        ShuffleSpawnPoints();
    }
    public bool TryGetSpawnPoint(out Vector3 position, out Quaternion rotation)
    {
        //Debug.Log($"[LobbySpawner] TryGetSpawnPoint — index: {_spawnPointIndex}, caller: {new System.Diagnostics.StackTrace().ToString().Split('\n')[1].Trim()}");
        if (_availableSpawnPoints == null || _availableSpawnPoints.Count == 0)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            return false;
        }
        position = _availableSpawnPoints[_spawnPointIndex];
        rotation = _availableSpawnRotations[_spawnPointIndex];
        _spawnPointIndex = (_spawnPointIndex + 1) % _availableSpawnPoints.Count;
        // Every point has been handed out once — reshuffle so the next lap isn't the same order.
        if (_spawnPointIndex == 0)
            ShuffleSpawnPoints();
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
        // Picks a fresh random anchor each call — doesn't consume/advance _spawnPointIndex,
        // so it can't collide with the new-join cycling above.
        if (_playerSpawnPoints == null || _playerSpawnPoints.Count == 0)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            return false;
        }
        int index = Random.Range(0, _playerSpawnPoints.Count);
        Transform anchor = _playerSpawnPoints[index];
        position = anchor.position;
        rotation = anchor.rotation;
        return true;
    }
    // --- Furniture ---
    // Spread across frames — see SpawnAllRoutine's comment for why a
    // synchronous burst of ServerManager.Spawn() calls is unsafe here.
    private IEnumerator SpawnFurnitureRoutine()
    {
        if (_furnitureSpawnConfig == null) yield break;
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
            yield return null;
        }
    }
    // --- Props (single-item spawns) ---
    // Spread across frames — see SpawnAllRoutine's comment.
    private IEnumerator SpawnPropsRoutine()
    {
        if (_propSpawnConfig == null) yield break;
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
            yield return null;
        }
    }
    // --- Prop Setups (grouped mini setups — chess, bowling, pool, etc.) ---
    // Spread across frames — see SpawnAllRoutine's comment. Chess alone is 13
    // pieces; bursting all of them (plus every other configured prop setup)
    // in one frame was the same class of bug SpawnBowlingLaneRoutine and
    // SwitchPoolPatternRoutine were already fixed for.
    private IEnumerator SpawnPropSetupsRoutine()
    {
        if (_propSetups == null) yield break;
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
                {
                    InstanceFinder.ServerManager.Spawn(netObj);
                    RegisterManualRevealObject(netObj);
                }
                yield return null;
            }
        }
    }
    // --- Pool Setups (rack prefab supplies its own ball-slot positions, assigned per-slot in the Inspector) ---
    // Spread across frames — see SpawnAllRoutine's comment. A single pool
    // setup (rack + cue ball + cues + holding rack + up to 15 racked balls)
    // is comfortably over the ~30-message burst SwitchPoolPatternRoutine's
    // own comment documents as reliably stalling a connected client, and this
    // runs once per configured setup at Lobby load. The racked-balls loop is
    // inlined here rather than calling a shared helper, same reasoning
    // SwitchPoolPatternRoutine's comment gives: a synchronous helper has no
    // way to yield mid-loop. (This replaces the old SpawnRackedBalls() —
    // removed, since after this change it has no callers left.)
    private IEnumerator SpawnPoolSetupsRoutine()
    {
        if (_poolSetups == null) yield break;
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
            {
                InstanceFinder.ServerManager.Spawn(rackNetObj);
                RegisterManualRevealObject(rackNetObj);
            }
            yield return null;

            int patternIndex = setup.RandomizePattern
                ? Random.Range(0, setup.Patterns.Count)
                : Mathf.Clamp(setup.ActivePatternIndex, 0, setup.Patterns.Count - 1);
            PoolRackPattern pattern = setup.Patterns[patternIndex];

            // Racked balls — inlined (see method comment for why, same as
            // SwitchPoolPatternRoutine's own ball loop below).
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
                {
                    InstanceFinder.ServerManager.Spawn(ballNetObj);
                    RegisterManualRevealObject(ballNetObj);
                }

                // Lock it completely fixed right away — nothing should be able
                // to nudge the rack formation before a player deliberately
                // lifts the rack off. PoolRackGrabbable unlocks it on grab.
                PoolBall poolBall = ball.GetComponent<PoolBall>();
                if (poolBall != null)
                {
                    poolBall.ServerSetLocked(true);
                    rackedBalls.Add(poolBall);
                }
                yield return null;
            }

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
                {
                    InstanceFinder.ServerManager.Spawn(cueBallNetObj);
                    RegisterManualRevealObject(cueBallNetObj);
                }
                instance.SpawnedCueBall = cueBall.GetComponent<CueBall>();
                yield return null;
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
                    {
                        InstanceFinder.ServerManager.Spawn(cueNetObj);
                        RegisterManualRevealObject(cueNetObj);
                    }
                    yield return null;
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
                {
                    InstanceFinder.ServerManager.Spawn(holdingRackNetObj);
                    RegisterManualRevealObject(holdingRackNetObj);
                }
                else
                    Debug.LogWarning($"[LobbySpawner] Pool setup '{setup.SetupLabel}' HoldingRackPrefab has no NetworkObject — it will only appear on the host/server, not on remote clients.");
                instance.SpawnedHoldingRack = holdingRack.transform;
                yield return null;
            }
            else if (instance.HoldingRackPrefab != null || instance.HoldingRackAnchor != null)
            {
                Debug.LogWarning($"[LobbySpawner] Pool setup '{setup.SetupLabel}' has only one of HoldingRackPrefab/HoldingRackAnchor assigned — both are required to spawn the holding rack.");
            }
        }
    }


    // Used by PoolPocket to find the spawned holding rack without needing a
    // direct Inspector reference (which can't exist at design time for a
    // runtime-spawned object — see the comment above where it's spawned).
    // Index-based since a pocket needs to resolve its OWN table's holding
    // rack, not just "the first one" — same reasoning as GetBowlingLane()
    // below. Each PoolPocket instance is told which pool setup it belongs
    // to via its own _poolSetupIndex field (same pattern PoolResetButton
    // already uses).
    public Transform GetHoldingRack(int setupIndex)
    {
        if (_poolSetups == null || setupIndex < 0 || setupIndex >= _poolSetups.Count)
        {
            Debug.LogWarning($"[LobbySpawner] GetHoldingRack — invalid setup index {setupIndex}.");
            return null;
        }
        return _poolSetups[setupIndex].SpawnedHoldingRack;
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
    // frames avoids the burst entirely. This same bug turned out to also
    // apply to the initial pool spawn (SpawnPoolSetupsRoutine) — see that
    // method's comment — which duplicates this same ball-spawn loop rather
    // than sharing it with this one, since a shared helper would need to be
    // its own coroutine either way once both call sites yield mid-loop.
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
                // Drop it from the manual-reveal tracking list too — otherwise
                // a repeatedly-switched pattern would leave stale (destroyed)
                // entries piling up in _manualRevealObjects forever. Harmless
                // either way (RevealManualObjectsToConnection already skips
                // null entries), but there's no reason to let it grow.
                if (ballNetObj != null)
                    _manualRevealObjects.Remove(ballNetObj);
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
            {
                InstanceFinder.ServerManager.Spawn(ballNetObj);
                RegisterManualRevealObject(ballNetObj);
            }

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

    // --- Stage Setups (mic stand + optional instrument per act, one act live at a time) ---
    // Spread across frames for the same reason as every other Spawn*Routine
    // above — see SpawnAllRoutine's comment. A stage spawn is small (at most
    // two objects: mic stand + instrument) so the burst risk here is low, but
    // there's no reason to special-case it out of the shared convention.
    private IEnumerator SpawnStageSetupsRoutine()
    {
        if (_stageSetups == null) yield break;
        foreach (var instance in _stageSetups)
        {
            StageSetupConfig setup = instance?.Config;
            if (setup == null) continue;
            if (instance.Anchor == null)
            {
                Debug.LogWarning($"[LobbySpawner] Stage setup '{setup.SetupLabel}' has no Anchor Transform assigned — skipping.");
                continue;
            }
            if (setup.Acts == null || setup.Acts.Count == 0)
            {
                Debug.LogWarning($"[LobbySpawner] Stage setup '{setup.SetupLabel}' has no acts defined.");
                continue;
            }

            int actIndex = Mathf.Clamp(setup.DefaultActIndex, 0, setup.Acts.Count - 1);
            StageAct defaultAct = setup.Acts[actIndex];
            int variantIndex = (defaultAct.InstrumentVariants != null && defaultAct.InstrumentVariants.Count > 0)
                ? Mathf.Clamp(defaultAct.DefaultVariantIndex, 0, defaultAct.InstrumentVariants.Count - 1)
                : 0;
            yield return StartCoroutine(SpawnStageActRoutine(instance, setup, actIndex, variantIndex));
        }
    }

    // Does the actual spawn work for a single act — shared by both the
    // initial Lobby-load spawn above and SwitchStageActRoutine below, unlike
    // SpawnPoolSetupsRoutine/SwitchPoolPatternRoutine (which duplicate their
    // ball-spawn loop rather than share it) since a stage spawn is only ever
    // "mic stand, then optionally an instrument" — simple enough that a
    // shared coroutine doesn't lose anything by being reused from both call
    // sites.
    //
    // variantIndex picks which of the act's (purely visual) InstrumentVariants
    // spawns — ignored/harmless if the act has none. Callers are responsible
    // for clamping it against the act's actual variant count; this just
    // re-clamps defensively before indexing.
    private IEnumerator SpawnStageActRoutine(StageSetupInstance instance, StageSetupConfig setup, int actIndex, int variantIndex)
    {
        StageAct act = setup.Acts[actIndex];
        Vector3 anchorPosition = instance.Anchor.position;
        Quaternion anchorRotation = instance.Anchor.rotation;

        if (act.MicStandPrefab != null)
        {
            Vector3 worldPosition = anchorPosition + (anchorRotation * act.MicStandPosition);
            Quaternion worldRotation = anchorRotation * Quaternion.Euler(act.MicStandRotation);
            GameObject micStand = Instantiate(act.MicStandPrefab, worldPosition, worldRotation, _propParent);
            micStand.transform.localScale = act.MicStandScale;
            micStand.name = $"{setup.SetupLabel}_{act.ActName}_MicStand";
            NetworkObject micStandNetObj = micStand.GetComponent<NetworkObject>();
            if (micStandNetObj != null)
            {
                InstanceFinder.ServerManager.Spawn(micStandNetObj);
                RegisterManualRevealObject(micStandNetObj);
            }
            instance.SpawnedMicStand = micStand;
            yield return null;

            // Mic attaches to a named child on the STAND'S SPAWNED INSTANCE,
            // not on the stand prefab asset — the anchor only exists once
            // Instantiate() above has actually created it, same reasoning as
            // PoolSetupConfig's rack-slot resolution (Transform.Find by name
            // against the spawned rack, not the prefab).
            if (act.MicPrefab != null)
            {
                if (string.IsNullOrEmpty(act.MicAnchorName))
                {
                    Debug.LogWarning($"[LobbySpawner] Stage setup '{setup.SetupLabel}' act '{act.ActName}' has a MicPrefab but no MicAnchorName set.");
                }
                else
                {
                    Transform micAnchor = micStand.transform.Find(act.MicAnchorName);
                    if (micAnchor == null)
                    {
                        Debug.LogWarning($"[LobbySpawner] Stage setup '{setup.SetupLabel}' act '{act.ActName}' — couldn't find a child named '{act.MicAnchorName}' on the spawned mic stand. Check spelling/casing against MicStandPrefab's Hierarchy.");
                    }
                    else
                    {
                        GameObject mic = Instantiate(act.MicPrefab, micAnchor.position, micAnchor.rotation, _propParent);
                        mic.transform.localScale = act.MicScale;
                        mic.name = $"{setup.SetupLabel}_{act.ActName}_Mic";
                        NetworkObject micNetObj = mic.GetComponent<NetworkObject>();
                        if (micNetObj != null)
                        {
                            InstanceFinder.ServerManager.Spawn(micNetObj);
                            RegisterManualRevealObject(micNetObj);
                        }
                        instance.SpawnedMic = mic;
                    }
                }
                yield return null;
            }
        }

        GameObject instrumentPrefab = null;
        if (act.InstrumentVariants != null && act.InstrumentVariants.Count > 0)
        {
            int clampedVariant = Mathf.Clamp(variantIndex, 0, act.InstrumentVariants.Count - 1);
            instrumentPrefab = act.InstrumentVariants[clampedVariant]?.Prefab;
            instance.ActiveVariantIndex = clampedVariant;
        }
        else
        {
            instance.ActiveVariantIndex = -1;
        }

        if (instrumentPrefab != null)
        {
            Vector3 worldPosition = anchorPosition + (anchorRotation * act.InstrumentPosition);
            Quaternion worldRotation = anchorRotation * Quaternion.Euler(act.InstrumentRotation);
            GameObject instrument = Instantiate(instrumentPrefab, worldPosition, worldRotation, _propParent);
            instrument.transform.localScale = act.InstrumentScale;
            instrument.name = $"{setup.SetupLabel}_{act.ActName}_Instrument";
            NetworkObject instrumentNetObj = instrument.GetComponent<NetworkObject>();
            if (instrumentNetObj != null)
            {
                InstanceFinder.ServerManager.Spawn(instrumentNetObj);
                RegisterManualRevealObject(instrumentNetObj);
            }
            else
            {
                Debug.LogWarning($"[LobbySpawner] Stage setup '{setup.SetupLabel}' act '{act.ActName}' instrument variant prefab has no NetworkObject — it will only appear on the host/server, not on remote clients.");
            }

            // Scene references (the stage's speakers) that the instrument
            // prefab can't hold at design time — same pattern as
            // BowlingBall.SetHolderAnchor(). Empty (falls back to the
            // instrument's own position — see InstrumentInteractable) if
            // this stage has no SpeakerAnchors assigned.
            InstrumentInteractable interactable = instrument.GetComponent<InstrumentInteractable>();
            if (interactable != null)
            {
                List<StageSpeaker> speakers = new List<StageSpeaker>();
                if (instance.SpeakerAnchors != null)
                {
                    foreach (Transform speakerAnchor in instance.SpeakerAnchors)
                    {
                        StageSpeaker speaker = speakerAnchor != null ? speakerAnchor.GetComponent<StageSpeaker>() : null;
                        if (speaker != null) speakers.Add(speaker);
                        else if (speakerAnchor != null) Debug.LogWarning($"[LobbySpawner] Stage setup '{setup.SetupLabel}' has a SpeakerAnchor entry with no StageSpeaker component: '{speakerAnchor.name}'.");
                    }
                }
                interactable.SetSpeakerAnchors(speakers);
            }

            instance.SpawnedInstrument = instrument;
            yield return null;
        }

        instance.ActiveActIndex = actIndex;
    }

    // Switches this stage to a different Act — e.g. Talk Mic switching to
    // Piano + Mic — despawning whatever's currently spawned for this stage
    // and spawning the new act's prefabs in their place. Validates
    // synchronously, then hands off to a coroutine that does the actual
    // despawn/spawn work spread across frames, same convention as
    // SwitchPoolPattern/SwitchPoolPatternRoutine.
    // variantIndex selects which (purely visual) InstrumentVariant of the
    // target act spawns — harmless/ignored if that act has none or the index
    // is out of range (re-clamped in SpawnStageActRoutine). Defaults to 0 so
    // existing callers that only care about the act itself don't need to
    // change.
    public void SwitchStageAct(int setupIndex, int actIndex, int variantIndex = 0)
    {
        if (_stageSetups == null || setupIndex < 0 || setupIndex >= _stageSetups.Count)
        {
            Debug.LogWarning($"[LobbySpawner] SwitchStageAct — invalid setup index {setupIndex}.");
            return;
        }
        StageSetupInstance instance = _stageSetups[setupIndex];
        StageSetupConfig setup = instance.Config;
        if (setup == null || setup.Acts == null || actIndex < 0 || actIndex >= setup.Acts.Count)
        {
            Debug.LogWarning($"[LobbySpawner] SwitchStageAct — invalid act index {actIndex} for setup {setupIndex}.");
            return;
        }

        // Stop any switch already in progress for this stage before starting
        // a new one — same reasoning as SwitchPoolPattern: otherwise two
        // overlapping coroutines could both end up despawning/spawning
        // against the same instance at once.
        if (instance.ActiveSwitchRoutine != null)
            StopCoroutine(instance.ActiveSwitchRoutine);
        instance.ActiveSwitchRoutine = StartCoroutine(SwitchStageActRoutine(instance, setup, actIndex, variantIndex));
    }

    private IEnumerator SwitchStageActRoutine(StageSetupInstance instance, StageSetupConfig setup, int actIndex, int variantIndex)
    {
        if (instance.SpawnedInstrument != null)
        {
            NetworkObject instrumentNetObj = instance.SpawnedInstrument.GetComponent<NetworkObject>();
            if (instrumentNetObj != null && instrumentNetObj.IsSpawned)
            {
                _manualRevealObjects.Remove(instrumentNetObj);
                InstanceFinder.ServerManager.Despawn(instrumentNetObj);
            }
            instance.SpawnedInstrument = null;
            yield return null;
        }

        if (instance.SpawnedMic != null)
        {
            NetworkObject micNetObj = instance.SpawnedMic.GetComponent<NetworkObject>();
            if (micNetObj != null && micNetObj.IsSpawned)
            {
                _manualRevealObjects.Remove(micNetObj);
                InstanceFinder.ServerManager.Despawn(micNetObj);
            }
            instance.SpawnedMic = null;
            yield return null;
        }

        if (instance.SpawnedMicStand != null)
        {
            NetworkObject micStandNetObj = instance.SpawnedMicStand.GetComponent<NetworkObject>();
            if (micStandNetObj != null && micStandNetObj.IsSpawned)
            {
                _manualRevealObjects.Remove(micStandNetObj);
                InstanceFinder.ServerManager.Despawn(micStandNetObj);
            }
            instance.SpawnedMicStand = null;
            yield return null;
        }

        yield return StartCoroutine(SpawnStageActRoutine(instance, setup, actIndex, variantIndex));

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
            {
                InstanceFinder.ServerManager.Spawn(netObj);
                RegisterManualRevealObject(netObj);
            }

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
                {
                    InstanceFinder.ServerManager.Spawn(ballNetObj);
                    RegisterManualRevealObject(ballNetObj);
                }

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

            // Same moment every server-confirmed-fallen pin in the lane
            // clears — also resync every pin the server still considers
            // standing. See ReaffirmStandingPins()/Pin.ServerReaffirmStanding()
            // for why this is needed.
            ReaffirmStandingPins(lane);

            lane.LastAwaitingHideCount = 0;
        }
    }

    // Re-broadcasts every still-standing pin's transform in this lane —
    // see Pin.ServerReaffirmStanding() for the full reasoning: physics is
    // simulated independently per client, so a pin can topple over on one
    // client's own local physics without the server's copy falling the
    // same way. That pin's SyncVar never changes as a result (the server
    // still thinks it's standing), so nothing else would ever put it back
    // up for that one client.
    //
    // Called from two places, covering both ways a roll can go:
    //  - UpdateBowlingPinGroupHide() above, alongside the normal
    //    fallen-pin hide pass — covers the common case, at the same
    //    moment pins are already being resynced.
    //  - BowlingGameController.ServerResolveRoll(), unconditionally, once
    //    per roll — covers the edge case UpdateBowlingPinGroupHide() can't:
    //    a roll where the SERVER itself sees zero pins fall (e.g. a
    //    gutter ball) short-circuits before ever reaching the hide pass
    //    above, but a pin could still have phantom-fallen on a client's
    //    own physics that same roll. ServerResolveRoll() runs every roll
    //    regardless of outcome, so this is the backstop that always fires.
    private void ReaffirmStandingPins(BowlingLaneInstance lane)
    {
        if (lane?.SpawnedPins == null) return;
        foreach (Pin pin in lane.SpawnedPins)
            if (pin != null && pin.IsStanding) pin.ServerReaffirmStanding();
    }

    /// Public, index-based wrapper for external callers (e.g.
    /// BowlingGameController) — same lookup convention as
    /// ResetBowlingLane(int)/SwitchBowlingPattern(int, int) above.
    public void ReaffirmStandingPins(int laneIndex)
    {
        if (_bowlingLanes == null || laneIndex < 0 || laneIndex >= _bowlingLanes.Count) return;
        ReaffirmStandingPins(_bowlingLanes[laneIndex]);
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
    // same index-based lookup pattern as GetHoldingRack(int) above.
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
    // it via LobbySpawner.Instance.GetHoldingRack(_poolSetupIndex).
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

[System.Serializable]
public class StageSetupInstance
{
    [Tooltip("The act list for this stage (e.g. StageSetup) — act names and mic stand/instrument prefabs, offsets, and clips live on this config's Acts.")]
    public StageSetupConfig Config;
    [Tooltip("Scene Transform marking where this stage's mic stand/instrument offsets are measured from — an empty GameObject placed at the stage's center.")]
    public Transform Anchor;
    [Tooltip("Scene Transforms, each carrying a StageSpeaker component — this stage's physical speaker props (e.g. two left, two right, one center). All of them are assigned onto each spawned InstrumentInteractable, which plays sound from every one simultaneously rather than a single point, so a listener naturally hears it loudest from whichever speaker(s) are physically closest to them. Optional — leave empty and instruments fall back to playing from their own Transform.")]
    public List<Transform> SpeakerAnchors = new List<Transform>();

    // Runtime-only, populated by SpawnStageSetupsRoutine()/SwitchStageActRoutine()
    // — used so a switch or a future reset doesn't need to re-discover the
    // spawned objects each time.
    [System.NonSerialized] public GameObject SpawnedMicStand;
    [System.NonSerialized] public GameObject SpawnedMic;
    [System.NonSerialized] public GameObject SpawnedInstrument;
    [System.NonSerialized] public int ActiveActIndex = -1;
    // Which InstrumentVariant of the active act is currently spawned, or -1
    // if that act has no variants (nothing to track). Set by
    // SpawnStageActRoutine so StageActSelector can default its variant
    // dropdown to what's actually live when the menu reopens.
    [System.NonSerialized] public int ActiveVariantIndex = -1;

    // Tracks the in-progress SwitchStageAct coroutine, if any — same
    // reasoning as PoolSetupInstance.ActiveSwitchRoutine: lets a re-press
    // stop a still-running switch cleanly instead of letting two coroutines
    // overlap against the same instance.
    [System.NonSerialized] public Coroutine ActiveSwitchRoutine;
}
