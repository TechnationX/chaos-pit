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
    private System.Guid _allocationId;

    // ─── Lifecycle ────────────────────────────────────────────────────────────

    protected override void Awake()
    {
        base.Awake();
    }

    // ─── Initialization ───────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        if (IsInitialized) return;

        try
        {
            await UnityServices.InitializeAsync();

            if (!AuthenticationService.Instance.IsSignedIn)
                await AuthenticationService.Instance.SignInAnonymouslyAsync();

            IsInitialized = true;
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[RelayManager] Initialization failed: {e.Message}");
            throw;
        }
    }

    // ─── Host ─────────────────────────────────────────────────────────────────

    public async Task<string> CreateRelaySessionAsync(int maxPlayers)
    {
        await EnsureInitialized();

        try
        {
            Allocation allocation = await RelayService.Instance.CreateAllocationAsync(maxPlayers - 1, "us-west2");
            _allocationId = allocation.AllocationId;

            //Debug.Log($"[RelayManager] Allocation created at time: {Time.realtimeSinceStartup}, region: {allocation.Region}");

            string joinCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);

            SetRelayHostData(allocation);

            //Debug.Log($"[RelayManager.CreateRelaySession] Join code: {joinCode} at time: {Time.realtimeSinceStartup}");
            return joinCode;
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[RelayManager] CreateRelaySession failed: {e.Message}");
            throw;
        }
    }

    // ─── Heartbeat ────────────────────────────────────────────────────────────



    public void ClearAllocation()
    {
        _allocationId = System.Guid.Empty;
    }

    // ─── Client ───────────────────────────────────────────────────────────────

    public async Task JoinRelaySessionAsync(string joinCode)
    {
        await EnsureInitialized();

        try
        {
            //Debug.Log($"[RelayManager.JoinRelaySession] Attempting to join with code: {joinCode} at time: {Time.realtimeSinceStartup}");
            JoinAllocation joinAllocation = await RelayService.Instance.JoinAllocationAsync(joinCode);
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

        utpTransport.SetRelayServerData(AllocationUtils.ToRelayServerData(allocation, "dtls"));
        //Debug.Log($"[RelayManager] Protocol type after set: {utpTransport.Protocol}");
    }

    private void SetRelayClientData(JoinAllocation joinAllocation)
    {
        var utpTransport = FindFirstObjectByType<FishNet.Transporting.UTP.UnityTransport>();

        if (utpTransport == null)
        {
            Debug.LogError("[RelayManager] FishyUnityTransport not found on NetworkManager.");
            return;
        }

        utpTransport.SetRelayServerData(AllocationUtils.ToRelayServerData(joinAllocation, "dtls"));
    }

    // ─── Utilities ────────────────────────────────────────────────────────────

    private async Task EnsureInitialized()
    {
        if (!IsInitialized)
            await InitializeAsync();
    }
}