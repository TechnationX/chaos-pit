// RelayManager.cs
// Place in: Assets/_Project/Scripts/Session/
// Owns: Unity Relay allocation, join code generation, client join via join code.
// Attach to: RelayManager GameObject in Bootstrap scene under _Managers.
// SessionManager calls into this — nothing else should call Relay directly.

using UnityEngine;
using System.Threading.Tasks;
using Unity.Services.Core;
using Unity.Services.Authentication;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using Unity.Networking.Transport.Relay;
using FishNet.Transporting.UTP;

public class RelayManager : SingletonBehaviour<RelayManager>
{
    // ─── State ────────────────────────────────────────────────────────────────

    public bool IsInitialized { get; private set; } = false;

    // ─── Lifecycle ────────────────────────────────────────────────────────────

    protected override void Awake()
    {
        base.Awake();
    }

    // ─── Initialization ───────────────────────────────────────────────────────

    // Called once at startup by SessionManager before any Relay calls
    public async Task InitializeAsync()
    {
        if (IsInitialized) return;

        try
        {
            await UnityServices.InitializeAsync();

            if (!AuthenticationService.Instance.IsSignedIn)
            {
                await AuthenticationService.Instance.SignInAnonymouslyAsync();
                Debug.Log($"[RelayManager] Signed in. Player ID: {AuthenticationService.Instance.PlayerId}");
            }

            IsInitialized = true;
            Debug.Log("[RelayManager] Initialized.");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[RelayManager] Initialization failed: {e.Message}");
            throw;
        }
    }

    // ─── Host ─────────────────────────────────────────────────────────────────

    // Allocates a Relay server and returns the join code.
    // maxPlayers does not include the host — pass total players minus 1.
    public async Task<string> CreateRelaySessionAsync(int maxPlayers)
    {
        await EnsureInitialized();

        try
        {
            // Relay maxConnections = max clients (excludes host)
            Allocation allocation = await RelayService.Instance.CreateAllocationAsync(maxPlayers - 1);

            string joinCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);
            Debug.Log($"[RelayManager] Relay allocated. Join code: {joinCode}");

            // Configure FishNet transport to use Relay
            SetRelayHostData(allocation);

            return joinCode;
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[RelayManager] CreateRelaySession failed: {e.Message}");
            throw;
        }
    }

    // ─── Client ───────────────────────────────────────────────────────────────

    // Resolves a join code and configures FishNet transport for client connection.
    public async Task JoinRelaySessionAsync(string joinCode)
    {
        await EnsureInitialized();

        try
        {
            JoinAllocation joinAllocation = await RelayService.Instance.JoinAllocationAsync(joinCode);
            Debug.Log($"[RelayManager] Joined Relay allocation with code: {joinCode}");

            // Configure FishNet transport to use Relay
            SetRelayClientData(joinAllocation);
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[RelayManager] JoinRelaySession failed: {e.Message}");
            throw;
        }
    }

    // ─── FishNet Transport Configuration ─────────────────────────────────────

    private void SetRelayHostData(Allocation allocation)
    {
        var utpTransport = FindFirstObjectByType<FishNet.Transporting.UTP.UnityTransport>();

        if (utpTransport == null)
        {
            Debug.LogError("[RelayManager] FishyUnityTransport not found on NetworkManager.");
            return;
        }

        utpTransport.SetRelayServerData(
            allocation.RelayServer.IpV4,
            (ushort)allocation.RelayServer.Port,
            allocation.AllocationIdBytes,
            allocation.Key,
            allocation.ConnectionData,
            allocation.ConnectionData,
            true
        );

        Debug.Log("[RelayManager] FishNet transport configured for host.");
    }

    private void SetRelayClientData(JoinAllocation joinAllocation)
    {
        var utpTransport = FindFirstObjectByType<FishNet.Transporting.UTP.UnityTransport>();

        if (utpTransport == null)
        {
            Debug.LogError("[RelayManager] FishyUnityTransport not found on NetworkManager.");
            return;
        }

        utpTransport.SetRelayServerData(
            joinAllocation.RelayServer.IpV4,
            (ushort)joinAllocation.RelayServer.Port,
            joinAllocation.AllocationIdBytes,
            joinAllocation.Key,
            joinAllocation.ConnectionData,
            joinAllocation.HostConnectionData,
            true
        );

        Debug.Log("[RelayManager] FishNet transport configured for client.");
    }

    // ─── Utilities ────────────────────────────────────────────────────────────

    private async Task EnsureInitialized()
    {
        if (!IsInitialized)
        {
            await InitializeAsync();
        }
    }
}