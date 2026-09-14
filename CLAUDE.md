# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

Chaos Pit — a Unity multiplayer party game (collaborative build). Players roam a shared lobby, walk up to game-room "stations," queue for a minigame, get teleported into it, and return to the lobby afterward with points recorded on a leaderboard.

- Unity 6000.3.8f1, Universal Render Pipeline.
- Networking: FishNet (`Assets/FishNet`) over Unity Relay/Unity Services Multiplayer (`Assets/_Project/Scripts/Session/RelayManager.cs`), not Netcode for GameObjects or Mirror.
- All first-party code lives under `Assets/_Project/`; it compiles into the default `Assembly-CSharp` assembly (no custom `.asmdef` for project scripts — the `.asmdef` files present belong to FishNet/vendor packages).

## Working with this repo

There is no CLI build/lint/test pipeline — this is a Unity Editor project, driven either through the Unity Editor UI or through the `unity-editor-mcp` MCP tools available in this environment (`recompile`, `run_tests`, `build`, `editor_play`, `console`, etc.). There are no test assemblies under `Assets/_Project` yet despite `com.unity.test-framework` being installed, so `run_tests` currently has nothing to run.

- Compile/check for errors: `recompile` (or open the Editor and watch the Console) — there is no `dotnet build`/`msbuild` workflow used day-to-day.
- Read console errors: `console` / `get_console_logs`.
- Play-test a change: `editor_play`, then interact via the MCP tools or take a `screenshot`/`capture_game_view`.
- Builds are produced via the `build` MCP tool or Unity's own Build Settings; past builds are dropped in `Builds/Alpha V0.x.x/` and zipped there too — that folder is output, not source.

## Architecture

### Bootstrap → Lobby → Minigame → Lobby loop

1. **Bootstrap** (`Scenes/Bootstrap.unity`, `Scripts/Bootstrap/BootstrapManager.cs`): verifies persistent singletons (`PlayerDataManager`, `AudioManager`, FishNet `NetworkManager`) exist, then loads `Splash` → eventually `MainMenu`/`Lobby`. Persistent managers use `SingletonBehaviour<T>` (`Scripts/Bootstrap/SingletonBehaviour.cs`), which `DontDestroyOnLoad`s the manager's root and self-destructs duplicates.
2. **Session/connection**: `SessionManager` (singleton) owns host/join flow — it drives `RelayManager` to allocate/join a Unity Relay allocation, then starts FishNet's `ServerManager`/`ClientManager` and *awaits* the real `Started` connection-state event (not a fixed delay) before flipping `SessionState` to `Active`. `GameRoomManager.HandlePlayerDisconnected` is invoked from `SessionManager`'s `OnRemoteConnectionState` callback when a remote connection stops.
3. **Lobby**: players free-roam; `LobbySpawner` places/returns them. Each **game room station** in the scene is a `MinigameStation` (see `Assets/_Project/Prefabs/GameRoom/GameStation.prefab`) that registers itself with the server-authoritative `GameRoomManager` singleton (`GameRoomManager.RequestRegistration`, buffered via a static pending list if the manager isn't ready yet). Each station gets its own `GameRoomSession` keyed by `StationIndex`.
4. **GameRoomManager** (`Scripts/MiniGames/Core/GameRoomManager.cs`) is the central server-authoritative state machine per station. `GameRoomSession.State` moves through `GameRoomState`: `Idle → Waiting → Countdown → Loading → InProgress → Results → Returning`. Key flows, all server RPCs guarded by host/ownership checks:
   - `RequestJoin`/`RequestLeave`/`RequestKickPlayer` (kick is host-only, via the room's physical `KickButton`) manage `GameRoomSession.Players` and `HostPlayer` (with host migration on host departure).
   - `SelectGame`/`RequestStartCountdown` (host-only) pick a `MiniGameRegistryEntry` or the special `Private` mode (`PrivateModeId`), which just locks the room instead of starting a game.
   - `BeginTransition` additively loads the minigame's scene **only for the connections in that session** (`SceneLoadData` with `AllowStacking = true`), waits for all clients to report loaded via `OnClientPresenceChangeEnd`, shows an intro/rules screen (`IntroScreenUI`, skippable), then calls `MiniGameController.StartGame`.
   - `OnGameComplete` → `ScoreManager.SubmitResults` → `MiniGameController.ShowResults` → (after the results screen times out) `OnResultsDismissed` → `ReturnToLobby`, which unloads the minigame scene per-connection and teleports players back via `LobbySpawner`.
   - Session state is pushed to clients via `RpcSyncSessionState`/`SyncSessionToClients`; `MinigameStation`/`GameRoomConsole` render it locally rather than owning state themselves.
   - Player↔minigame communication in-flight uses generic string-typed messages: `GameRoomManager.RpcMinigameMessage(type, payload)` (server→all clients, routed to `MiniGameController.OnNetworkMessage`) and `RequestMinigameAction(type, payload)` (client→server, routed to `MiniGameController.OnClientAction`). Payloads are hand-rolled pipe/comma-delimited strings, not JSON — see any existing controller's `Build*Payload`/`Apply*Payload` methods for the convention.

### Minigame plugin pattern

Each minigame lives in `Scripts/MiniGames/<GameName>/` with its own scene under `Scenes/MiniGames/<GameName>Scene.unity`, and is described by a `MiniGameRegistryEntry` ScriptableObject (`Data/ScriptableObjects/MiniGames/*.asset`, referenced by the single `MiniGameRegistry` asset the sessions load from `Resources/MiniGameRegistry`). Existing games: `BombToss`, `Jinxed`, `LastOneStanding`, `PaintTheTown`, `ThiefsMarket`.

**`Scripts/MiniGames/Template/`** is a working, copy-and-rename starting point for new minigames (`TemplateController.cs`/`TemplateHUD.cs`/`TemplateScoreRow.cs`), with its own scene (`TemplateMGScene.unity`). It documents the required steps in its header comment:
1. Copy the template controller/HUD/score-row files, rename namespace + class + message-type strings.
2. Implement the `MiniGameController` abstract members: `StartGame`, `StartRound`, `EndRound`, `GetResults`, `CleanUp` (server-authoritative game loop lives here — guard server-only logic with `FishNet.InstanceFinder.IsServerStarted`), plus optional `ClientInit`/`RemovePlayer`/`OnNetworkMessage`/`OnClientAction` overrides.
3. Build a new scene, wire the controller's Inspector references (spawn points, results panel, HUD), add a `MiniGameRegistryEntry` asset for it, add the scene to Build Settings.

`MiniGameController` base class (`Scripts/MiniGames/Core/MiniGameController.cs`) supplies shared plumbing every game reuses: spawn-point teleporting, the shared `ResultsScreenUI`/`ResultsCanvas` results-panel flow (`ShowResults` for the server-authoritative controller instance vs. `ShowResultsClientOnly` for pure clients receiving a results broadcast — don't double-trigger `GameRoomManager.OnResultsDismissed`), and `GetResultLabel`/point-standing helpers.

Namespacing is inconsistent across existing games — some (`Jinxed`, `Template`) use a `ChaosPit.Minigames.<Name>` namespace, most others (`BombToss`, `PaintTheTown`, `LastOneStanding`, `ThiefsMarket`) don't use a namespace at all. Follow whichever convention the game you're touching already uses.

### Player

`PlayerObject` (`Scripts/Player/PlayerObject.cs`) is a `NetworkBehaviour` that composes sub-systems by Inspector reference rather than inheritance: `Movement` (`PlayerMovement`), `Camera` (`PlayerCamera`), `Interaction` (`InteractionManager`), `Appearance` (`PlayerAppearance`). Minigames and `GameRoomManager` reach these via `player.Movement`/`player.Interaction`/etc. rather than `GetComponent`. Per-minigame player toggles (e.g. `SetJinxedTagActive`, `SetThiefsMarketPunchActive`, `SetBombPassActive`) live on `InteractionManager` and are flipped by `GameRoomManager` via `TargetRpc`s as players enter/leave the relevant game.

`IInteractable` (`Scripts/Interfaces/IInteractable.cs`) is the contract for anything a player can interact with (`PromptLabel` + `OnInteract(PlayerObject)`); `InteractionManager` drives prompts/interaction off it. Physical console buttons (`ConsoleButton`, `KickButton`) and other interactables implement it.

### Other systems

- **Scoring**: `ScoreManager` (singleton) tracks per-session results (`RegisterSession`/`SubmitResults`/`UnregisterSession`, keyed by `"station_<index>"`) and feeds both a per-session and a persistent career leaderboard (`CareerLevelSystem`, `PlayerResultEntry.CalculateLevel`); `LeaderboardManager` on clients caches and renders synced leaderboard data (`GameRoomManager.SyncLeaderboardToClients`).
- **Profiles**: `PlayerProfileManager` (server-side singleton) maps `NetworkConnection` → `PlayerProfile` (display name, career score); `PlayerProfileSync` handles client-side sync of the local player's own profile edits.
- **Cosmetics**: `CharacterLoadout`/`PlayerAppearance`/`OutfitRegistry` drive the character customization shown in `CharacterCreator.unity` and applied to spawned `PlayerObject`s.
