// PlayerProfileManager.cs

using System.Collections.Generic;
using UnityEngine;
using FishNet;
using FishNet.Connection;

public class PlayerProfileManager : MonoBehaviour
{
    public static PlayerProfileManager Instance { get; private set; }

    // Server-side registry — keyed by clientId
    private Dictionary<int, PlayerProfile> _profiles = new Dictionary<int, PlayerProfile>();

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    // --- Registration ---

    public void RegisterPlayer(NetworkConnection conn)
    {
        if (_profiles.ContainsKey(conn.ClientId)) return;

        PlayerProfile profile = new PlayerProfile(conn.ClientId);
        _profiles[conn.ClientId] = profile;

        // TODO: BACKEND — load career score from database here instead of defaulting to 0
        // Example: await PlayFabManager.GetPlayerScore(profile.SteamId)

        Debug.Log($"[PlayerProfileManager] Registered profile for ClientId: {conn.ClientId}, Name: {profile.DisplayName}");
    }

    public void UnregisterPlayer(NetworkConnection conn)
    {
        if (!_profiles.ContainsKey(conn.ClientId)) return;

        // TODO: BACKEND — save career score to database before removing
        // Example: await PlayFabManager.SavePlayerScore(profile.SteamId, profile.CareerScore)

        _profiles.Remove(conn.ClientId);
        Debug.Log($"[PlayerProfileManager] Unregistered profile for ClientId: {conn.ClientId}");
    }

    // --- Accessors ---

    public PlayerProfile GetProfile(NetworkConnection conn)
    {
        _profiles.TryGetValue(conn.ClientId, out PlayerProfile profile);
        return profile;
    }

    public PlayerProfile GetProfile(int clientId)
    {
        _profiles.TryGetValue(clientId, out PlayerProfile profile);
        return profile;
    }

    public List<PlayerProfile> GetAllProfiles()
    {
        return new List<PlayerProfile>(_profiles.Values);
    }

    // --- Scoring ---

    public void AddCareerScore(NetworkConnection conn, int points)
    {
        if (!_profiles.TryGetValue(conn.ClientId, out PlayerProfile profile)) return;

        profile.CareerScore += points;
        Debug.Log($"[PlayerProfileManager] +{points} pts to {profile.DisplayName} — Total: {profile.CareerScore}");

        var sync = conn.FirstObject?.GetComponent<PlayerProfileSync>();
        if (sync != null) sync.CareerScore.Value = profile.CareerScore;

        // TODO: BACKEND — push updated score to database here
        // Example: await PlayFabManager.SavePlayerScore(profile.SteamId, profile.CareerScore)
    }

    public void SetLoadout(NetworkConnection conn, byte[] packedData)
    {
        if (!_profiles.TryGetValue(conn.ClientId, out PlayerProfile profile)) return;
        profile.CurrentLoadout = packedData;

        // TODO: BACKEND — persist loadout to database here
        // Example: await PlayFabManager.SaveLoadout(profile.SteamId, profile.CurrentLoadout)
    }

    // --- Display Name ---

    public void SetDisplayName(NetworkConnection conn, string newName)
    {
        if (!_profiles.TryGetValue(conn.ClientId, out PlayerProfile profile)) return;

        profile.DisplayName = newName;
        Debug.Log($"[PlayerProfileManager] ClientId {conn.ClientId} renamed to: {newName}");

        // Keep PlayerObject.PlayerName in sync — it's a separate SyncVar
        // (used as a fallback name source by some minigames, e.g. Jinxed)
        // that LobbySpawner only ever sets ONCE, at spawn, using whatever
        // this profile's DisplayName happened to be at that exact instant.
        // RegisterPlayer() (called immediately before that spawn-time read,
        // in the same method) always creates a brand-new profile whose
        // DisplayName is still the "Player_<id>" placeholder — the real
        // name can't possibly have arrived yet, since it requires the
        // player's NetworkObject to finish spawning AND a round-trip
        // ServerRpc from their client. So without this, PlayerObject.PlayerName
        // stays wrong for the rest of the session even after the real name
        // syncs in everywhere else. This call is the only place DisplayName
        // ever actually changes post-registration, so it's the right choke
        // point to also correct it.
        var playerObj = conn.FirstObject?.GetComponent<PlayerObject>();
        playerObj?.SetPlayerName(newName);

        // TODO: BACKEND — persist display name once Steam/account integration exists
    }

    // --- Career Reset (testing only) ---

    public void ResetCareerScore(NetworkConnection conn)
    {
        if (!_profiles.TryGetValue(conn.ClientId, out PlayerProfile profile)) return;

        profile.CareerScore = 0;
        Debug.LogWarning($"[PlayerProfileManager] Career score reset for ClientId {conn.ClientId} (testing only)");

        var sync = conn.FirstObject?.GetComponent<PlayerProfileSync>();
        if (sync != null) sync.CareerScore.Value = 0;

        // TODO: BACKEND — clear persisted score in database once backend exists
    }

    public void SetInitialCareerScore(NetworkConnection conn, int score)
    {
        if (!_profiles.TryGetValue(conn.ClientId, out PlayerProfile profile)) return;

        profile.CareerScore = score;

        var sync = conn.FirstObject?.GetComponent<PlayerProfileSync>();
        if (sync != null) sync.CareerScore.Value = score;

        GameRoomManager.Instance?.SyncLeaderboardToClients();

        Debug.Log($"[PlayerProfileManager] Initialized CareerScore for ClientId {conn.ClientId} from local save: {score}");
    }

    // TODO: BACKEND — add local save/load methods here when persistence between sessions is needed
    // For now scores only persist for the duration of the server session
}