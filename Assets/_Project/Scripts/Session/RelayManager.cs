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

    // Every await in this class that crosses out to Unity Cloud Services
    // (sign-in, allocate, join) gets wrapped in WithTimeout below. None of
    // these SDK calls carry their own timeout — on a bad connection (flaky
    // wifi, a firewall that lets the HTTPS request out but never gets a
    // reply, DNS hanging) the awaited Task can simply sit forever with no
    // exception ever thrown. SessionManager's JoinSession/StartHostSession
    // already catch and recover from any exception these throw, so turning
    // "never resolves" into "throws after N seconds" is enough to fix a
    // hang without touching SessionManager at all. This was the gap left
    // after fixing the later FishNet-connection race (see SessionManager)
    // — that fix only guards the connection handshake AFTER these Relay
    // calls already succeeded; a hang here happens earlier and was never
    // covered.
    private const float _cloudCallTimeoutSeconds = 5f;

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
            await WithTimeout(UnityServices.InitializeAsync(), _cloudCallTimeoutSeconds, "UnityServices.InitializeAsync");

            if (!AuthenticationService.Instance.IsSignedIn)
                await WithTimeout(AuthenticationService.Instance.SignInAnonymouslyAsync(), _cloudCallTimeoutSeconds, "SignInAnonymouslyAsync");

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
            Allocation allocation = await WithTimeout(
                RelayService.Instance.CreateAllocationAsync(maxPlayers - 1, "us-west2"), _cloudCallTimeoutSeconds, "RelayService.CreateAllocationAsync");
            _allocationId = allocation.AllocationId;

            //Debug.Log($"[RelayManager] Allocation created at time: {Time.realtimeSinceStartup}, region: {allocation.Region}");

            string joinCode = await WithTimeout(
                RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId), _cloudCallTimeoutSeconds, "RelayService.GetJoinCodeAsync");

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
            JoinAllocation joinAllocation = await WithTimeout(
                RelayService.Instance.JoinAllocationAsync(joinCode), _cloudCallTimeoutSeconds, "RelayService.JoinAllocationAsync");
            SetRelayClientData(joinAllocation);
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[RelayManager] JoinRelaySession failed: {e.Message}");
            throw;
        }
    }

    // ─── Timeout Helper ───────────────────────────────────────────────────────

    // Every Unity Cloud Services call above goes through here. If `task`
    // hasn't completed within `timeoutSeconds`, throws a TimeoutException
    // instead of leaving the caller awaiting forever — the underlying task
    // is left to finish/fail on its own (Task.WhenAny doesn't cancel it),
    // its result is just no longer waited on. SessionManager's
    // JoinSession/StartHostSession both already catch System.Exception and
    // recover (reset to Idle, show an error), so this is the only change
    // needed to turn a silent hang into a visible, recoverable failure.
    private static async Task<T> WithTimeout<T>(Task<T> task, float timeoutSeconds, string operationLabel)
    {
        Task winner = await Task.WhenAny(task, Task.Delay(System.TimeSpan.FromSeconds(timeoutSeconds)));
        if (winner != task)
            throw new System.TimeoutException($"[RelayManager] {operationLabel} timed out after {timeoutSeconds:0}s.");

        // task is already complete here — awaiting it again just returns
        // its result (or rethrows its real exception, instead of masking
        // it as a timeout).
        return await task;
    }

    private static async Task WithTimeout(Task task, float timeoutSeconds, string operationLabel)
    {
        Task winner = await Task.WhenAny(task, Task.Delay(System.TimeSpan.FromSeconds(timeoutSeconds)));
        if (winner != task)
            throw new System.TimeoutException($"[RelayManager] {operationLabel} timed out after {timeoutSeconds:0}s.");

        await task;
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