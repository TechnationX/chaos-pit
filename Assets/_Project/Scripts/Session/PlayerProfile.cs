// PlayerProfile.cs

using System;

[Serializable]
public class PlayerProfile
{
    public int ClientId;
    public string DisplayName;
    public int CareerScore;

    // TODO: BACKEND — add SteamId field here when Steam integration is added
    // public ulong SteamId;

    public PlayerProfile(int clientId)
    {
        ClientId = clientId;
        DisplayName = $"Player_{clientId}";
        // TODO: BACKEND — replace DisplayName with SteamFriends.GetPersonaName()
        CareerScore = 0;
    }
}