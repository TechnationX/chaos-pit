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
using Unity.Cinemachine;
using Unity.Mathematics;
using UnityEngine;

public class GameRoomManager : NetworkBehaviour
{
    public static GameRoomManager Instance { get; private set; }

    // Pseudo minigame id used to select "Private" mode through the same
    // SelectGame/GetNextGameId cycle real minigames use, instead of adding a
    // second parallel path just for this one mode.
    public const string PrivateModeId = "__private__";

    [Header("Settings")]
    [SerializeField] private float _countdownDuration = 10f;
    [SerializeField] private float _introDuration = 6f;
    [SerializeField] private float _stationSceneSpacing = 5000f; // world units apart on Y between concurrent station scenes — see ApplyStationSceneOffset

    private Dictionary<int, MinigameStation> _stations = new Dictionary<int, MinigameStation>();
    private Dictionary<int, GameRoomSession> _sessions = new Dictionary<int, GameRoomSession>();

    private int _gameRoomLayer;
    private int _playerLayer;
    private int _sessionToken = 0;
    private int _clientSessionToken = 0;

    [SerializeField] private MiniGameRegistry _registry;

    private static List<MinigameStation> _pendingStations = new List<MinigameStation>();
    private Dictionary<int, HashSet<int>> _loadedClients = new Dictionary<int, HashSet<int>>();
    private Dictionary<int, System.Action<ClientPresenceChangeEventArgs>> _loadListeners
        = new Dictionary<int, System.Action<ClientPresenceChangeEventArgs>>();
    private Dictionary<int, int> _unloadedClientCounts = new Dictionary<int, int>();
    private Dictionary<int, System.Action<ClientPresenceChangeEventArgs>> _unloadListeners
        = new Dictionary<int, System.Action<ClientPresenceChangeEventArgs>>();
    private Dictionary<int, bool> _introSkipRequested = new Dictionary<int, bool>();

    // ─── Station Scene Positioning ─────────────────────────────────────────────
    // Every station's minigame scene loads with AllowStacking = true (see
    // BeginTransition) so concurrent stations each get their own separate
    // Scene instance — but Unity never offsets a stacked scene from the
    // ones already loaded; every instance's root GameObjects sit at the
    // exact same authored coordinates. See ApplyStationSceneOffset below.
    //
    // Offset is on Y (straight down), by design — the stations themselves
    // are laid out in different directions around the lobby, so any
    // horizontal (X/Z) separation scheme would need per-station math to
    // guarantee it never overlaps the lobby's own layout or another
    // station's footprint. Stacking on Y sidesteps that entirely: every
    // station just drops straight down by its own multiple of
    // _stationSceneSpacing, regardless of which direction its kiosk faces.
    //
    // The tradeoff: every minigame scene carries its own Directional
    // Light, cloned in from Template — harmless with one station active,
    // but a directional light isn't confined to the scene it lives in (it
    // has no position, only a direction, and lights/shadows everything in
    // view regardless of scene membership). With two or more stations
    // loaded in the same process at once (a host, whose server and own
    // client share one scene graph), that's two or more full "suns"
    // simultaneously active, each one able to cast the OTHER station's
    // geometry as a shadow anywhere in the combined world — reported as
    // "the shadow of the bomb from the scene above shows on the scene
    // below." Fixed below by keeping exactly one of these lights enabled
    // per process at a time (see DisableDuplicateStationSun /
    // RemoveStationSun) rather than by the Y offset itself, or by
    // physically blocking light with added geometry — offsetting on Y is
    // what separates the arenas spatially; this is what stops their
    // duplicate lights from fighting over the same world.
    private readonly HashSet<int> _offsetScenesByHandle = new HashSet<int>(); // Scene.handle already shifted, this process
    private readonly Dictionary<int, Light> _stationSuns = new Dictionary<int, Light>(); // this process's loaded stations' own Directional Lights, by stationIndex

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
        if (session.IsLocked) return; // private room — no new joins until the host unlocks it
        if (session.State != GameRoomState.Idle && session.State != GameRoomState.Waiting) return;
        if (session.Players.Contains(player)) return;

        MiniGameRegistryEntry entry = session.SelectedGame;
        if (entry != null && session.Players.Count >= entry.MaxPlayers) return;

        session.Players.Add(player);

        if (session.Players.Count == 1)
            session.HostPlayer = player;

        session.State = GameRoomState.Waiting;

        TeleportToWaitingArea(stationIndex, player);

        // Player is now physically in the waiting room and free to move —
        // no movement lock, no interaction disable. The room's
        // GameRoomConsole (fed by SyncSessionToClients below, same as the
        // exterior GameStation display) takes over from here; the frozen
        // kiosk panel is only ever used for the pre-join Join button.
        RpcForceCloseStationPanel(player.Owner, stationIndex);

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

    // Host-only removal of a specific non-host player, triggered by that
    // player's physical KickButton in the room's console. Mirrors
    // RequestLeave's cleanup for the target player, just initiated by the
    // host instead of by the player themselves.
    [ServerRpc(RequireOwnership = false)]
    public void RequestKickPlayer(int stationIndex, int targetClientId, PlayerObject requestingPlayer)
    {
        if (!_sessions.TryGetValue(stationIndex, out GameRoomSession session)) return;
        if (session.HostPlayer?.Owner?.ClientId != requestingPlayer?.Owner?.ClientId) return;
        if (targetClientId == requestingPlayer?.Owner?.ClientId) return; // host can't kick themselves

        PlayerObject target = session.Players.FirstOrDefault(p => p.Owner?.ClientId == targetClientId);
        if (target == null) return;

        if (session.State == GameRoomState.Countdown)
        {
            if (session.CountdownCoroutine != null)
                StopCoroutine(session.CountdownCoroutine);
            session.CountdownCoroutine = null;
        }

        RemovePlayerFromSession(session, target, stationIndex);
        ReturnPlayerToLobby(target);
        RpcForceCloseStationPanel(target.Owner, stationIndex);

        if (session.Players.Count == 0)
        {
            ResetSession(stationIndex);
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

        if (_loadListeners.TryGetValue(stationIndex, out var storedLoadListener))
        {
            InstanceFinder.NetworkManager.SceneManager.OnClientPresenceChangeEnd -= storedLoadListener;
            _loadListeners.Remove(stationIndex);
        }

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

        // Station-scoped lookup — see GetStationScene's comment. The server
        // keeps every station's minigame scene loaded at once, so grabbing
        // "the first MiniGameController in any loaded scene" (the old,
        // unscoped FindActiveMinigameController()) could silently hand this
        // station's StartGame() call to a DIFFERENT station's controller
        // whenever two stations were mid-load/mid-game together — exactly
        // what was causing two concurrent games to corrupt each other.
        UnityEngine.SceneManagement.Scene scene = GetStationScene(stationIndex);

        // Shift this station's freshly-loaded scene instance away from every
        // other active station BEFORE anything reads a position out of it —
        // spawns[spawnIndex].position below, and every minigame's own
        // Awake/Start-time position caching, all need to see the final,
        // already-offset coordinates. This is the server's own copy of the
        // scene; see ApplyStationSceneOffset's comment for why each
        // participant's own separately-loaded copy also needs this call,
        // from RpcInitMinigame below.
        ApplyStationSceneOffset(scene, stationIndex);

        MiniGameController controller = FindActiveMinigameController(scene);
        if (controller == null)
        {
            Debug.LogError($"[GameRoomManager] No MiniGameController found in {session.SelectedGame.SceneName}");
            yield break;
        }

        // Tags this controller instance with its own station, so every
        // RpcMinigameMessage call it makes during the round (round starts,
        // timer syncs, eliminations, etc.) can identify which station's
        // broadcast it is — see RpcMinigameMessage's own comment.
        controller.SetStationIndex(stationIndex);

        // Show the intro/rules screen and wait it out (or let a player skip
        // it) BEFORE calling StartGame — round timers/coroutines only begin
        // once this returns, so no round time is ever burned while players
        // are reading it.
        yield return StartCoroutine(ShowIntroAndWait(stationIndex, session.SelectedGame));

        session.ActiveController = controller;

        // Send this BEFORE StartGame() below — StartGame() can synchronously
        // kick off in-round network messages (e.g. PaintTheTownController's
        // StartGame -> GameLoopCoroutine -> StartRound sends "round_start"
        // before yielding), and there's no guaranteed ordering between two
        // separately-fired RPCs once both are in flight (see
        // ThiefsMarketController.ClientInit's own comment/workaround for the
        // same race on "tm_players"). Sending RpcInitMinigame first
        // guarantees ClientInit() — and therefore ShowHUD(), which activates
        // the HUD GameObject each scene now starts with disabled — is queued
        // ahead of anything a controller's StartGame() might fire
        // immediately, instead of racing it. Without this, a client could
        // receive an in-round message and try to run a coroutine on its
        // still-inactive HUD (Unity logs "Coroutine couldn't be started
        // because the game object is inactive" and silently drops it).
        foreach (PlayerObject player in session.Players)
            RpcInitMinigame(player.Owner, player.NetworkObject, stationIndex);

        controller.StartGame(session.Players);

        Debug.Log($"[GameRoomManager] GAME LIVE — station {stationIndex}, controller: {controller.GetType().Name}, time: {Time.realtimeSinceStartup:F1}s");

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
            RpcReinitializeCamera(player.Owner, player.NetworkObject, stationIndex);
            RpcUnlockPlayer(player.Owner, player.NetworkObject, token);
        }

        _stations[stationIndex].OnSessionUpdated(session);
    }

    // ─── Intro / Rules Screen ─────────────────────────────────────────────────
    // Shown right after the scene finishes loading and before StartGame() is
    // called, so round timers never burn while players are reading it. Ends
    // early if any player in the session requests a skip.

    private IEnumerator ShowIntroAndWait(int stationIndex, MiniGameRegistryEntry entry)
    {
        _introSkipRequested[stationIndex] = false;

        // TargetRpc looped per player — this used to be a single ObserversRpc,
        // which reaches EVERY connected player regardless of station. Scoping
        // FindActiveIntroScreen to the right scene (below) only fixed WHICH
        // station's rules screen got shown; it still showed that (correct)
        // screen to everyone, including a completely uninvolved host, because
        // ObserversRpc doesn't care who's actually playing — and on a host,
        // the server half of that same process always has every active
        // station's scene loaded for its own authoritative logic, so it was
        // always in range to receive it. TargetRpc, sent only to this
        // station's own players, closes that off at the source instead of
        // filtering after the fact — same shape as RpcReinitializeCamera/
        // RpcInitMinigame below.
        if (_sessions.TryGetValue(stationIndex, out GameRoomSession introSession))
        {
            foreach (PlayerObject player in introSession.Players)
                RpcShowIntroScreen(player.Owner, stationIndex, entry.MiniGameName, entry.Description, _introDuration);
        }

        float remaining = _introDuration;
        while (remaining > 0f)
        {
            if (_introSkipRequested.TryGetValue(stationIndex, out bool skip) && skip) break;
            remaining -= Time.deltaTime;
            yield return null;
        }

        _introSkipRequested.Remove(stationIndex);

        if (_sessions.TryGetValue(stationIndex, out GameRoomSession hideSession))
        {
            foreach (PlayerObject player in hideSession.Players)
                RpcHideIntroScreen(player.Owner, stationIndex);
        }
    }

    [TargetRpc]
    private void RpcShowIntroScreen(NetworkConnection conn, int stationIndex, string title, string rulesText, float duration)
    {
        IntroScreenUI intro = FindActiveIntroScreen(stationIndex);
        intro?.Show(title, rulesText, duration);
    }

    [TargetRpc]
    private void RpcHideIntroScreen(NetworkConnection conn, int stationIndex)
    {
        IntroScreenUI intro = FindActiveIntroScreen(stationIndex);
        intro?.Hide();
    }

    [ServerRpc(RequireOwnership = false)]
    public void RequestSkipIntro(PlayerObject requestingPlayer)
    {
        if (requestingPlayer == null) return;

        foreach (var kvp in _sessions)
        {
            if (kvp.Value.Players.Contains(requestingPlayer))
            {
                _introSkipRequested[kvp.Key] = true;
                return;
            }
        }
    }

    // Station-scoped — tries GetStationScene(stationIndex) first (accurate
    // for the server, and therefore for a host, since a host's own client
    // code and the server it's also running share the exact same
    // SceneConnections bookkeeping). Falls back to the old unscoped "search
    // every loaded scene" only if that lookup can't resolve a scene — the
    // situation a genuine dedicated remote client is normally in, where it's
    // already correct anyway since they only ever have their own station's
    // scene loaded.
    private IntroScreenUI FindActiveIntroScreen(int stationIndex)
    {
        UnityEngine.SceneManagement.Scene scene = GetStationScene(stationIndex);
        if (scene.IsValid())
        {
            foreach (GameObject obj in scene.GetRootGameObjects())
            {
                IntroScreenUI ui = obj.GetComponentInChildren<IntroScreenUI>(true);
                if (ui != null) return ui;
            }
            return null;
        }

        return FindActiveIntroScreenUnscoped();
    }

    private IntroScreenUI FindActiveIntroScreenUnscoped()
    {
        for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
            foreach (GameObject obj in scene.GetRootGameObjects())
            {
                IntroScreenUI ui = obj.GetComponentInChildren<IntroScreenUI>(true);
                if (ui != null) return ui;
            }
        }
        return null;
    }

    // ─── Host Controls ────────────────────────────────────────────────────────

    [ServerRpc(RequireOwnership = false)]
    public void SelectGame(int stationIndex, string miniGameId, PlayerObject requestingPlayer)
    {
        if (!_sessions.TryGetValue(stationIndex, out GameRoomSession session)) return;
        if (session.State != GameRoomState.Waiting) return;

        // Previously unchecked — harmless while the only caller was a UI
        // button the panel hid from non-hosts, but the room's SelectGame
        // console button is a physical, walk-up-and-click interactable
        // anyone can reach, so this needs real server-side enforcement now.
        if (session.HostPlayer?.Owner?.ClientId != requestingPlayer?.Owner?.ClientId) return;

        // Room must be unlocked before its mode can change — host presses
        // Start again to unlock first.
        if (session.IsLocked) return;

        if (miniGameId == PrivateModeId)
        {
            session.SelectedGame = null;
            session.IsPrivateMode = true;
            _stations[stationIndex].OnSessionUpdated(session);
            SyncSessionToClients(stationIndex, session);
            return;
        }

        MiniGameRegistryEntry entry = session.Registry?.GetById(miniGameId);
        if (entry == null)
        {
            Debug.LogWarning($"[GameRoomManager] MiniGame {miniGameId} not found in registry.");
            return;
        }

        session.SelectedGame = entry;
        session.IsPrivateMode = false;
        _stations[stationIndex].OnSessionUpdated(session);
        SyncSessionToClients(stationIndex, session);
    }

    [ServerRpc(RequireOwnership = false)]
    public void RequestStartCountdown(int stationIndex, PlayerObject requestingPlayer)
    {
        if (!_sessions.TryGetValue(stationIndex, out GameRoomSession session)) return;
        if (session.State != GameRoomState.Waiting) return;
        if (session.HostPlayer?.Owner?.ClientId != requestingPlayer?.Owner?.ClientId) return;

        if (session.IsPrivateMode)
        {
            // Private mode never starts a countdown or loads a scene —
            // pressing Start just flips the room's lock instantly. Players
            // stay exactly where they are. Pressing it again unlocks.
            session.IsLocked = !session.IsLocked;
            _stations[stationIndex].OnSessionUpdated(session);
            SyncSessionToClients(stationIndex, session);
            return;
        }

        if (session.SelectedGame == null) return;
        if (session.Players.Count < session.SelectedGame.MinPlayers) return;

        session.State = GameRoomState.Countdown;
        session.CountdownCoroutine = StartCoroutine(CountdownCoroutine(stationIndex));
        _stations[stationIndex].OnSessionUpdated(session);

        // Previously only the countdown-tick RPC kept clients updated (label
        // text only) — _syncedState itself stayed "Waiting" on every client
        // for the whole countdown, so the status label and Join button's
        // interactable state were stale (harmless since the server still
        // rejects a join here, but worth being accurate).
        SyncSessionToClients(stationIndex, session);
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

        // ResetSession replaces the session object — this was previously
        // syncing the stale, pre-reset "session" reference (still showing the
        // old countdown/players/game), not the fresh empty one that's now
        // actually authoritative. Same fix as HandlePlayerDisconnected below.
        ResetSession(stationIndex);
        SyncSessionToClients(stationIndex, _sessions[stationIndex]);
        _stations[stationIndex].OnSessionUpdated(_sessions[stationIndex]);
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

        Debug.Log($"[GameRoomManager] GAME STARTING — station {stationIndex}, game: {session.SelectedGame?.SceneName}, players: {session.Players.Count}, time: {Time.realtimeSinceStartup:F1}s");

        session.State = GameRoomState.Loading;
        // Clients were otherwise never told the room left Countdown until
        // InProgress synced after scene load — same staleness pattern as
        // RequestStartCountdown above, closed here too. UpdateSessionState
        // already closes any open panel on Loading, so this also makes that
        // trigger correctly for a client whose panel is somehow still open.
        SyncSessionToClients(stationIndex, session);

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

        void loadListener(ClientPresenceChangeEventArgs args) => OnClientPresenceChangeEnd(args, stationIndex);
        _loadListeners[stationIndex] = loadListener;
        InstanceFinder.NetworkManager.SceneManager.OnClientPresenceChangeEnd += loadListener;

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

        Debug.Log($"[GameRoomManager] GAME COMPLETE — station {stationIndex}, results count: {results?.Count ?? 0}, time: {Time.realtimeSinceStartup:F1}s");

        session.State = GameRoomState.Results;
        // No console/kiosk UI is visible during a live round anyway, but keep
        // _syncedState accurate for consistency with every other transition.
        SyncSessionToClients(stationIndex, session);

        string sessionId = GetSessionId(stationIndex);

        ScoreManager.Instance.SubmitResults(sessionId, results);
        ResultsData data = BuildResultsData(sessionId, results);
        session.ActiveController?.ShowResults(data);

        //Debug.Log($"[GameRoomManager] Game complete for station {stationIndex} — showing results");
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

        Debug.Log($"[GameRoomManager] RETURNING TO LOBBY — station {stationIndex}, unloading scene: {session.SelectedGame?.SceneName}, time: {Time.realtimeSinceStartup:F1}s");

        _sessionToken++;
        RpcSyncSessionToken(_sessionToken);

        session.State = GameRoomState.Returning;
        SyncSessionToClients(stationIndex, session);

        string sessionId = GetSessionId(stationIndex);
        session.ActiveController?.CleanUp();

        // This scene instance is about to unload for good — forget its
        // handle so _offsetScenesByHandle doesn't grow forever across a
        // long server session. Read before unloading; GetStationScene
        // can't resolve it anymore once the connections have left it.
        UnityEngine.SceneManagement.Scene endingScene = GetStationScene(stationIndex);
        if (endingScene.IsValid()) _offsetScenesByHandle.Remove(endingScene.handle);

        // Same idea for this station's Directional Light — if it was the
        // one currently lighting the world for this process, hand that job
        // to another still-loaded station before this one's light goes
        // away with its scene.
        RemoveStationSun(stationIndex);

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

        //Debug.Log($"[GameRoomManager] Scene unload progress — {_unloadedClientCounts[stationIndex]}/{players.Count}");

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

        Debug.Log($"[GameRoomManager] BACK IN LOBBY — station {stationIndex}, players: {players.Count}, time: {Time.realtimeSinceStartup:F1}s");

        foreach (PlayerObject player in players)
            ReturnPlayerToLobby(player);

        GameRoomManager.Instance?.SyncLeaderboardToClients();
        // Deliberately NOT calling ScoreManager.UnregisterSession here anymore.
        // This path runs after every normal game end, not just when the group
        // disbands — unregistering here wiped this station's whole score
        // dictionary the instant players returned to the lobby, so "session
        // score" never actually accumulated across more than one game; the
        // leaderboard shown right after each game only ever reflected that
        // one game's payout. RegisterSession() (called from BeginTransition
        // when the next game starts) already no-ops if the session still
        // exists, so leaving it registered here is what lets scores keep
        // accumulating game after game. The session still gets torn down
        // correctly elsewhere — see LeaveMinigameSession's UnregisterSession
        // call, which fires once this station's player count actually reaches
        // 0 (the group truly disbanding).
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

                if (session.Players.Count == 0)
                {
                    // ResetSession replaces the session object entirely (fresh
                    // Private/unlocked room) — sync and refresh using the new
                    // one, not the abandoned "session" reference, same as
                    // RequestLeave/RequestKickPlayer already do.
                    ResetSession(stationIndex);
                    SyncSessionToClients(stationIndex, _sessions[stationIndex]);
                    _stations[stationIndex].OnSessionUpdated(_sessions[stationIndex]);
                }
                else
                {
                    SyncSessionToClients(stationIndex, session);
                    _stations[stationIndex].OnSessionUpdated(session);
                }
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
                    SyncSessionToClients(stationIndex, _sessions[stationIndex]);
                    _stations[stationIndex].OnSessionUpdated(_sessions[stationIndex]);
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

    // ─── Voluntary Leave (pause menu "Quit to Lobby") ────────────────────────
    // Mirrors the InProgress/Results branch of HandlePlayerDisconnected above,
    // but the player stays connected — so unlike a disconnect, we also have to
    // unload the minigame scene for just this one connection (FishNet supports
    // unloading a scene per-connection; everyone else keeps their scene loaded
    // and keeps playing) and then run them through the normal lobby-return path.

    [ServerRpc(RequireOwnership = false)]
    public void RequestLeaveMinigame(PlayerObject requestingPlayer)
    {
        if (requestingPlayer == null) return;

        foreach (var kvp in _sessions)
        {
            GameRoomSession session = kvp.Value;
            if (!session.Players.Contains(requestingPlayer)) continue;

            if (session.State != GameRoomState.InProgress && session.State != GameRoomState.Results)
                return; // not in a state where leaving early makes sense (e.g. still Loading)

            LeaveMinigameSession(session, requestingPlayer, kvp.Key);
            return;
        }

        Debug.LogWarning("[GameRoomManager] RequestLeaveMinigame — requesting player not found in any session.");
    }

    [Server]
    private void LeaveMinigameSession(GameRoomSession session, PlayerObject player, int stationIndex)
    {
        string sceneName = session.SelectedGame.SceneName;
        NetworkConnection conn = player.Owner;

        Debug.Log($"[GameRoomManager] LEAVE MINIGAME — station {stationIndex}, player: {player.name}, scene: {sceneName}");

        session.ActiveController?.RemovePlayer(player);
        RemovePlayerFromSession(session, player, stationIndex); // also migrates host if needed

        void listener(ClientPresenceChangeEventArgs args)
        {
            if (args.Scene.name != sceneName || args.Added || args.Connection != conn) return;
            InstanceFinder.NetworkManager.SceneManager.OnClientPresenceChangeEnd -= listener;
            ReturnPlayerToLobby(player);
        }
        InstanceFinder.NetworkManager.SceneManager.OnClientPresenceChangeEnd += listener;

        SceneUnloadData sud = new SceneUnloadData(sceneName);
        InstanceFinder.NetworkManager.SceneManager.UnloadConnectionScenes(new NetworkConnection[] { conn }, sud);

        if (session.Players.Count == 0)
        {
            session.ActiveController?.CleanUp();
            ScoreManager.Instance.UnregisterSession(GetSessionId(stationIndex));
            ResetSession(stationIndex);
        }

        if (_stations.TryGetValue(stationIndex, out MinigameStation station))
            SyncSessionToClients(stationIndex, _sessions[stationIndex]);
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
        if (nt != null) nt.Teleport();
        player.transform.position = position;
        player.transform.rotation = rotation;
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
    private void RpcReinitializeCamera(NetworkConnection conn, NetworkObject playerNetObj, int stationIndex)
    {
        if (playerNetObj == null) return;
        PlayerObject player = playerNetObj.GetComponent<PlayerObject>();
        if (player == null) return;
        player.ReinitializeCamera();

        // This RPC only ever fires from the per-player loop in
        // StartGameAfterLoad (minigame entry) — the lobby-return path
        // (RpcTeleportAndUnlockPlayer) calls player.ReinitializeCamera()
        // directly without this extra step, so it naturally stays on
        // PlayerCamera's default starting mode (FirstPerson).
        //
        // Minigames use a fixed, scene-placed top-down camera rather than
        // any player-controlled one — every client independently finds
        // their own loaded copy of that scene object (same pattern as
        // FindActiveMinigameController/FindActiveIntroScreen) rather than
        // the server trying to network a reference to it. We also need the
        // scene's own physical render Camera (a separate object from the
        // CinemachineCamera vcam) — see FindActiveMinigameRenderCamera and
        // PlayerCamera.SwitchTo. An earlier version of this fix called
        // SetOwnHeadPiecesVisible(true) here directly, which edits
        // Camera.main — but Camera.main is the player's persistent
        // first/third-person camera, and minigames are actually rendered
        // through this scene's own top-down camera instead, so that edit
        // was silently landing on a camera that wasn't even on screen.
        //
        // stationIndex: this used to call the unscoped FindActiveMinigameCamera()
        // ("first CinemachineCamera in any loaded scene"), which on a host is
        // deterministically wrong the moment two stations are running at once —
        // the host's process is also the server, so it has every active
        // station's scene loaded, and this search always returned whichever
        // station's scene loaded earliest, not this player's own station. That
        // was the actual cause of "host's camera gets pulled into the other
        // player's minigame" even though the host's own teleport/spawn above
        // already used the correctly station-scoped controller. Now scoped the
        // same way FindActiveIntroScreen is.
        CinemachineCamera minigameCam = FindActiveMinigameCamera(stationIndex);
        if (minigameCam != null)
        {
            Camera renderCam = FindActiveMinigameRenderCamera(minigameCam);
            player.Camera.SetMiniGameCamera(minigameCam, renderCam);
            player.Camera.SwitchTo(PlayerCamera.CameraMode.MiniGame);
        }
        else
        {
            Debug.LogWarning("[GameRoomManager] RpcReinitializeCamera — no minigame camera found in loaded scenes.");
        }
    }

    // Finds the minigame scene's own physical rendering Camera (the one its
    // CinemachineBrain actually drives) — a separate scene object from the
    // CinemachineCamera vcam FindActiveMinigameCamera returns below.
    //
    // This used to search every loaded scene the same "skip player rigs"
    // way FindActiveMinigameCamera does — which was itself the bug: the
    // Lobby scene ALSO has a plain Camera (the persistent Camera.main this
    // whole fix exists to get away from), and scenes are searched in load
    // order, so this was finding Lobby's camera first every single time,
    // before ever reaching the minigame scene. A CinemachineCamera vcam is
    // specific enough to only ever match inside a minigame scene, but a
    // bare Camera component isn't — Lobby has one too. Taking the already-
    // found vcam's own Scene and searching only inside it removes the
    // ambiguity entirely, regardless of scene load order.
    private Camera FindActiveMinigameRenderCamera(CinemachineCamera minigameCam)
    {
        if (minigameCam == null) return null;

        UnityEngine.SceneManagement.Scene scene = minigameCam.gameObject.scene;
        foreach (GameObject obj in scene.GetRootGameObjects())
        {
            if (obj.GetComponentInChildren<PlayerObject>() != null) continue;

            Camera cam = obj.GetComponentInChildren<Camera>();
            if (cam != null) return cam;
        }
        return null;
    }

    // Station-scoped — tries GetStationScene(stationIndex) first (accurate on
    // a host, since a host's own client code and the server it also runs
    // share the exact same SceneConnections bookkeeping), falling back to the
    // old unscoped search only if that lookup can't resolve a scene — the
    // situation a genuine dedicated remote client is normally in, where only
    // its own station's scene is loaded anyway so the unscoped search was
    // already correct there. Same scoped-first/unscoped-fallback shape as
    // FindActiveIntroScreen.
    private CinemachineCamera FindActiveMinigameCamera(int stationIndex)
    {
        UnityEngine.SceneManagement.Scene scene = GetStationScene(stationIndex);
        if (scene.IsValid())
        {
            foreach (GameObject obj in scene.GetRootGameObjects())
            {
                if (obj.GetComponentInChildren<PlayerObject>() != null) continue;

                CinemachineCamera cam = obj.GetComponentInChildren<CinemachineCamera>();
                if (cam != null) return cam;
            }
            return null;
        }

        return FindActiveMinigameCameraUnscoped();
    }

    // Finds the current minigame scene's fixed top-down CinemachineCamera.
    // Skips any root object that's a player rig, since PlayerObjectBoZo/
    // PlayerObjectMixamo each carry their own first/third-person
    // CinemachineCameras — a plain type search would risk matching one of
    // those instead of the scene's dedicated camera.
    //
    // Unscoped — kept only as the remote-client fallback above. On a host
    // this returned whichever station's scene loaded earliest, regardless of
    // which station the RPC recipient actually belongs to, which was the
    // confirmed cause of the host's camera getting pulled into a different
    // player's minigame.
    private CinemachineCamera FindActiveMinigameCameraUnscoped()
    {
        for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
            foreach (GameObject obj in scene.GetRootGameObjects())
            {
                if (obj.GetComponentInChildren<PlayerObject>() != null) continue;

                CinemachineCamera cam = obj.GetComponentInChildren<CinemachineCamera>();
                if (cam != null) return cam;
            }
        }
        return null;
    }

    [TargetRpc]
    private void RpcInitMinigame(NetworkConnection conn, NetworkObject playerNetObj, int stationIndex)
    {
        // Same host-cross-talk fix as RpcReinitializeCamera/FindActiveMinigameCamera —
        // scoped to this player's actual station first, unscoped fallback for a
        // genuine remote client (which only ever has one station's scene loaded
        // anyway). Previously always grabbed whichever station's scene loaded
        // earliest on a host, handing ClientInit() to the wrong station's controller.
        UnityEngine.SceneManagement.Scene scene = GetStationScene(stationIndex);
        MiniGameController controller;

        if (scene.IsValid())
        {
            // On a host this is the SAME Scene instance StartGameAfterLoad
            // already offset server-side (host = server + client in one
            // process) — ApplyStationSceneOffset's own handle guard makes
            // this second call a no-op there, not a double shift.
            ApplyStationSceneOffset(scene, stationIndex);
            controller = FindActiveMinigameController(scene);
        }
        else
        {
            // Genuine remote client — GetStationScene can never resolve
            // here (SceneConnections is server-only bookkeeping), but a
            // remote client only ever has its own one minigame scene
            // loaded, so find that scene directly and offset it: it's a
            // separate physical copy on that machine, never synced from
            // the server's copy, so it has to compute the same shift on
            // its own.
            scene = GetActiveMinigameSceneUnscoped();
            ApplyStationSceneOffset(scene, stationIndex);
            controller = FindActiveMinigameController(scene);
        }

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
        List<string> playerNames, List<int> clientIds, GameRoomState state,
        bool gameSelected, int minPlayers, string selectedGameId, bool isPrivateMode, bool isLocked)
    {
        if (!_stations.TryGetValue(stationIndex, out MinigameStation station)) return;
        station.UpdateSessionState(hostClientId, playerNames, clientIds, state, gameSelected, minPlayers, selectedGameId, isPrivateMode, isLocked);
    }

    private void SyncSessionToClients(int stationIndex, GameRoomSession session)
    {
        int hostId = session.HostPlayer?.Owner?.ClientId ?? -1;

        List<string> names = session.Players.Select(p => {
            PlayerProfile profile = PlayerProfileManager.Instance.GetProfile(p.Owner);
            return profile?.DisplayName ?? p.name;
        }).ToList();

        List<int> clientIds = session.Players.Select(p => p.Owner?.ClientId ?? -1).ToList();
        bool gameSelected = session.SelectedGame != null;
        int minPlayers = session.SelectedGame?.MinPlayers ?? 0;
        string selectedGameId = session.SelectedGame?.MiniGameId ?? string.Empty;
        RpcSyncSessionState(stationIndex, hostId, names, clientIds, session.State, gameSelected, minPlayers, selectedGameId, session.IsPrivateMode, session.IsLocked);
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

    // Called directly (not as an RPC itself) by every minigame controller —
    // round starts, timer syncs, holder/turn changes, eliminations, tile
    // updates, game-over payloads, ~25 call sites across BombToss/
    // PaintTheTown/ThiefsMarket, always from server-only code inside that
    // controller instance. This used to BE the [ObserversRpc], broadcasting
    // straight to every connected player. Two separate problems came from
    // that, both from the same cause: ObserversRpc doesn't know or care
    // which station a message is about, and on a host specifically, the
    // server half of that same process is an observer of every active
    // station's game, not just the ones the host is actually playing.
    //
    // 1. The receiving end used to look up "the first MiniGameController in
    //    any loaded scene" (the unscoped FindActiveMinigameController()
    //    overload below) — with two same-or-different-type games running
    //    at once, a host's
    //    process would resolve BOTH stations' broadcasts to whichever
    //    controller instance happened to load first, visibly merging the two
    //    games' player lists/state together.
    // 2. Even once routed to the CORRECT controller (via stationIndex,
    //    below), an uninvolved host would still receive and display that
    //    station's in-round HUD/messages, because ObserversRpc reaches every
    //    connected player regardless of participation — same class of bug
    //    already fixed for the intro screen, camera, and results screen.
    //
    // Fixed the same way as those: this is now a plain method that resolves
    // the calling controller's own station's session and fans out to a
    // per-player TargetRpc, so only the players actually in that specific
    // game ever receive it, on their own process, and only ever see the
    // controller instance for their own station.
    public void RpcMinigameMessage(string messageType, string payload, int stationIndex)
    {
        if (!_sessions.TryGetValue(stationIndex, out GameRoomSession session)) return;

        foreach (PlayerObject player in session.Players)
            TargetMinigameMessage(player.Owner, stationIndex, messageType, payload);
    }

    [TargetRpc]
    private void TargetMinigameMessage(NetworkConnection conn, int stationIndex, string messageType, string payload)
    {
        UnityEngine.SceneManagement.Scene scene = GetStationScene(stationIndex);
        MiniGameController controller = scene.IsValid()
            ? FindActiveMinigameController(scene)
            : FindActiveMinigameController();
        controller?.OnNetworkMessage(messageType, payload);
    }

    // Unscoped search — kept as the remote-client fallback above (and for
    // RpcInitMinigame). A genuine remote client only ever has its own
    // station's minigame scene loaded at a time, so "first match across all
    // loaded scenes" is already correct there. It only goes wrong for
    // server-side code (or, as above, a host reached with the wrong
    // stationIndex before GetStationScene can resolve a scene yet), which
    // can have several stations' scenes loaded at once — see the scoped
    // overload below and GetStationScene's comment.
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

    // Station-scoped version of the search above, for server-only call
    // sites that know which station they're acting on.
    private MiniGameController FindActiveMinigameController(UnityEngine.SceneManagement.Scene scene)
    {
        if (!scene.IsValid()) return null;
        foreach (GameObject obj in scene.GetRootGameObjects())
        {
            MiniGameController ctrl = obj.GetComponentInChildren<MiniGameController>();
            if (ctrl != null) return ctrl;
        }
        return null;
    }

    // Same idea as FindActiveMinigameController()'s unscoped fallback just
    // above, but returns the Scene itself rather than the controller inside
    // it. Needed by RpcInitMinigame so a genuine remote client — whose
    // GetStationScene can never resolve, since SceneConnections is
    // server-only bookkeeping — can still find and offset its own one
    // loaded minigame scene in ApplyStationSceneOffset.
    private UnityEngine.SceneManagement.Scene GetActiveMinigameSceneUnscoped()
    {
        for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
            foreach (GameObject obj in scene.GetRootGameObjects())
            {
                if (obj.GetComponentInChildren<MiniGameController>() != null)
                    return scene;
            }
        }
        return default;
    }

    // Shifts every root GameObject in a station's freshly-loaded scene
    // instance _stationSceneSpacing units apart on Y (straight down), so
    // concurrent stations never occupy the same world-space footprint —
    // arena geometry, spawn points, kill planes, and the scene's own
    // top-down camera all move together as one rigid block, since they're
    // all children of these root objects. Station 0 gets zero offset
    // (stays at the scene's authored coordinates); every station after it
    // drops further down. See the "Station Scene Positioning" comment
    // above for why Y was chosen over a horizontal axis, and for the
    // shared-light shadow tradeoff that comes with it.
    //
    // Each physical process that loads a copy of this scene must call this
    // itself — plain scene geometry isn't a NetworkObject, so it's never
    // synced between the server and a client, or between two different
    // clients' machines. Call sites: StartGameAfterLoad (the server's own
    // authoritative copy, which spawn-point/teleport math reads from) and
    // RpcInitMinigame (each participant's own separately-loaded copy). On a
    // host those two calls land on the exact same Scene instance (host =
    // server + client in one process) — the handle guard below makes the
    // second call a no-op instead of shifting it twice.
    private void ApplyStationSceneOffset(UnityEngine.SceneManagement.Scene scene, int stationIndex)
    {
        if (!scene.IsValid()) return;
        if (!_offsetScenesByHandle.Add(scene.handle)) return; // already shifted on this process

        Vector3 offset = new Vector3(0f, -_stationSceneSpacing * stationIndex, 0f);
        foreach (GameObject root in scene.GetRootGameObjects())
            root.transform.position += offset;

        DisableDuplicateStationSun(scene, stationIndex);
    }

    // Every minigame scene carries its own Directional Light (cloned in
    // from Template, same as every other per-scene script/prefab this
    // session has found duplicated that way). Finds this station's own
    // copy within its scene instance.
    private Light FindStationDirectionalLight(UnityEngine.SceneManagement.Scene scene)
    {
        if (!scene.IsValid()) return null;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (Light light in root.GetComponentsInChildren<Light>(true))
            {
                if (light.type == LightType.Directional) return light;
            }
        }
        return null;
    }

    // Keeps exactly one station's Directional Light enabled per process at
    // a time — see the "Station Scene Positioning" comment above for why.
    // The first station's light to load on this process stays on; every
    // later one found here starts disabled. RemoveStationSun (called from
    // ReturnToLobby) promotes a replacement if the currently-enabled one's
    // station finishes before the others.
    private void DisableDuplicateStationSun(UnityEngine.SceneManagement.Scene scene, int stationIndex)
    {
        Light sun = FindStationDirectionalLight(scene);
        if (sun == null) return;

        _stationSuns[stationIndex] = sun;

        bool anotherAlreadyLit = _stationSuns.Any(kvp => kvp.Key != stationIndex && kvp.Value != null && kvp.Value.enabled);
        sun.enabled = !anotherAlreadyLit;
    }

    // Called from ReturnToLobby right before a station's scene unloads.
    // Drops its Directional Light from the tracked set, and if that light
    // was the one currently lighting the whole world for this process,
    // promotes another still-loaded station's light so the world doesn't
    // go dark once this one is gone.
    private void RemoveStationSun(int stationIndex)
    {
        if (!_stationSuns.TryGetValue(stationIndex, out Light sun)) return;
        _stationSuns.Remove(stationIndex);

        if (sun == null || !sun.enabled) return; // wasn't the active one — nothing to promote

        foreach (var kvp in _stationSuns)
        {
            if (kvp.Value == null) continue;
            kvp.Value.enabled = true;
            break;
        }
    }

    // Finds which loaded Scene instance actually belongs to a given
    // station. Every station's minigame scene is loaded via
    // LoadConnectionScenes with AllowStacking = true, so two stations
    // running the same minigame (or even two different ones) each get their
    // own separate Scene instance, never a shared one — but the server
    // keeps every station's instance loaded at once. FishNet's
    // SceneManager.SceneConnections tracks exactly which connections sit
    // inside each loaded scene, so this matches a station's own player
    // connections against that instead of guessing by scene load order.
    private UnityEngine.SceneManagement.Scene GetStationScene(int stationIndex)
    {
        if (!_sessions.TryGetValue(stationIndex, out GameRoomSession session)) return default;

        string sceneName = session.SelectedGame?.SceneName;
        if (string.IsNullOrEmpty(sceneName)) return default;

        foreach (var kvp in InstanceFinder.NetworkManager.SceneManager.SceneConnections)
        {
            if (kvp.Key.name != sceneName) continue;

            foreach (PlayerObject player in session.Players)
            {
                if (player.Owner != null && kvp.Value.Contains(player.Owner))
                    return kvp.Key;
            }
        }

        return default;
    }

    // Same idea as GetStationScene, but starting from a connection instead
    // of a station index — for server call sites (like RequestMinigameAction
    // below) that only have the sending connection, not the station it
    // belongs to. Same Players-list-membership check RequestSkipIntro above
    // already uses to find "which station is this player in."
    private int GetStationIndexForConnection(NetworkConnection conn)
    {
        if (conn == null) return -1;

        foreach (var kvp in _sessions)
        {
            if (kvp.Value.Players.Any(p => p.Owner == conn))
                return kvp.Key;
        }

        return -1;
    }

    [ServerRpc(RequireOwnership = false)]
    public void RequestMinigameAction(string messageType, string payload, NetworkConnection sender = null)
    {
        // Server-side — see GetStationScene's comment for why an unscoped
        // search here could route this action into the wrong station's
        // game whenever two stations are running minigames at once.
        int stationIndex = GetStationIndexForConnection(sender);
        MiniGameController controller = FindActiveMinigameController(GetStationScene(stationIndex));
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