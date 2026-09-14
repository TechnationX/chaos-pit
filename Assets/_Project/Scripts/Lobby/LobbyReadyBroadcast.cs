// LobbyReadyBroadcast
using FishNet.Broadcast;

// Sent by a client (host included — the host is its own local client) once
// its own copy of the Lobby scene has actually finished loading. See
// JoinSessionScreen.LoadLobby / CreateSessionScreen.LoadLobby for where this
// gets sent, and LobbySpawner.OnLobbyReadyBroadcast for where it's consumed.
//
// This exists because LobbySpawner used to trigger a joining connection's
// player spawn + manual-reveal burst off FishNet's own
// SceneManager.OnClientLoadedStartScenes — but nothing in this project loads
// the Lobby scene through FishNet's scene system (both CreateSessionScreen
// and JoinSessionScreen use plain UnityEngine.SceneManagement.SceneManager.
// LoadSceneAsync), so that event has no relationship to whether the
// connecting client's own Lobby scene has actually loaded yet. In testing it
// fired about a second before a remote client's Lobby scene finished
// loading, so the server was spawning/revealing objects for a client that
// had nowhere (yet) to put them — which looked like the client silently
// receiving nothing at all after joining. This broadcast lets the client
// itself tell the server when it's actually ready, closing that race.
public struct LobbyReadyBroadcast : IBroadcast
{
}
