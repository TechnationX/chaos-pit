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

        // TODO: BACKEND — push updated score to database here
        // Example: await PlayFabManager.SavePlayerScore(profile.SteamId, profile.CareerScore)
    }

    // TODO: BACKEND — add local save/load methods here when persistence between sessions is needed
    // For now scores only persist for the duration of the server session
}