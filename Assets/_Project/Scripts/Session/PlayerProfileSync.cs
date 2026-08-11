// PlayerProfileSync.cs
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

public class PlayerProfileSync : NetworkBehaviour
{
    public readonly SyncVar<string> DisplayName = new SyncVar<string>();
    public readonly SyncVar<int> CareerScore = new SyncVar<int>();

    public override void OnStartServer()
    {
        base.OnStartServer();

        PlayerProfile profile = PlayerProfileManager.Instance.GetProfile(Owner);
        if (profile == null) return;

        DisplayName.Value = profile.DisplayName;
        CareerScore.Value = profile.CareerScore;
    }

    public override void OnStartClient()
    {
        base.OnStartClient();

        DisplayName.OnChange += HandleDisplayNameChanged;
        CareerScore.OnChange += HandleCareerScoreChanged;

        if (!IsOwner) return;
        if (LocalPlayerProfile.Instance == null) return;

        string savedName = LocalPlayerProfile.Instance.DisplayName;
        if (!string.IsNullOrEmpty(savedName))
            RequestSetDisplayName(savedName);

        RequestInitCareerScore(LocalPlayerProfile.Instance.CareerScore);
    }

    public override void OnStopClient()
    {
        base.OnStopClient();
        DisplayName.OnChange -= HandleDisplayNameChanged;
        CareerScore.OnChange -= HandleCareerScoreChanged;
    }

    // --- Scoring ---

    private void HandleCareerScoreChanged(int prev, int next, bool asServer)
    {
        if (!IsOwner) return;
        LocalPlayerProfile.Instance?.SetCareerScore(next);
    }

    [ServerRpc]
    public void RequestInitCareerScore(int localScore)
    {
        PlayerProfileManager.Instance.SetInitialCareerScore(Owner, localScore);
    }

    // --- Display Name ---

    [ServerRpc]
    public void RequestSetDisplayName(string newName)
    {
        newName = SanitizeName(newName);
        if (string.IsNullOrEmpty(newName)) return;

        PlayerProfileManager.Instance.SetDisplayName(Owner, newName);
        DisplayName.Value = newName; // SyncVar push — updates all clients automatically

        GameRoomManager.Instance?.SyncLeaderboardToClients(); // keep leaderboard in sync with the live name
    }

    private string SanitizeName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim();
        if (raw.Length > 20) raw = raw.Substring(0, 20);
        return raw;
    }

    // --- Career Score Reset (testing only) ---

    [ServerRpc]
    public void RequestResetCareerScore()
    {
        PlayerProfileManager.Instance.ResetCareerScore(Owner);
        CareerScore.Value = 0;
    }

    private void HandleDisplayNameChanged(string prev, string next, bool asServer)
    {
        if (!IsOwner) return;

        LobbyUIManager ui = FindFirstObjectByType<LobbyUIManager>(FindObjectsInactive.Include);
        ui?.SetPlayerName(next);
    }
}