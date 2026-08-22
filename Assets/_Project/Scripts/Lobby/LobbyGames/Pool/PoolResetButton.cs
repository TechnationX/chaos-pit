// PoolResetButton
using FishNet.Object;
using UnityEngine;

/// <summary>
/// Physical in-world button/lever prop for resetting the pool table — same
/// IInteractable pattern as Pushable/Grabbable/IdleActivity, not a UI
/// element, since nothing else in the lobby uses on-screen buttons for
/// physical interactions.
///
/// Three independent uses, picked per-instance via _mode. Place one button
/// per mode you want available — each one's _poolSetupIndex must match the
/// same pool table:
///   FullReset     — switches the table to THIS button's own
///                   _targetPatternIndex (default 0 — the 8-ball pattern)
///                   and does a full re-rack under it: rack, every numbered
///                   ball, and the cue ball all go back to spawn, balls
///                   re-locked. Always resets to 8-ball specifically,
///                   regardless of whatever mode the table is currently in
///                   — same mechanism as NineBallReset below, just pointed
///                   at a different pattern. See LobbySpawner.SwitchPoolPattern().
///   CueBallOnly   — just the cue ball. For when it flies off the table
///                   entirely — there's no pocket collider out there to
///                   catch it and trigger PoolPocket's normal reset. Not
///                   tied to a game mode, so this one doesn't use
///                   _targetPatternIndex.
///   NineBallReset — same as FullReset, but for this button's own
///                   _targetPatternIndex (typically 1 — wherever the 9-ball
///                   Pattern lands in the config). Each reset button is
///                   fully self-contained: pressing either one always lands
///                   on ITS OWN game mode, never "whatever was racked
///                   before" — that's why both cases below call the exact
///                   same method with just a different index.
/// The table always spawns on the config's default Pattern
/// (PoolSetupConfig.ActivePatternIndex, normally 0/8-ball) — these buttons
/// are the only way to switch to a different one at runtime.
/// </summary>
public class PoolResetButton : NetworkBehaviour, IInteractable
{
    public enum ResetMode
    {
        FullReset,
        CueBallOnly,
        NineBallReset
    }

    [Header("Reset Button Settings")]
    [SerializeField] private ResetMode _mode = ResetMode.FullReset;
    [Tooltip("Index into LobbySpawner's Pool Setups list — must match the pool table this button controls.")]
    [SerializeField] private int _poolSetupIndex = 0;
    [Tooltip("Used when Mode is FullReset or NineBallReset — index into this pool setup's PoolSetupConfig.Patterns list that THIS button always resets to. Leave at 0 for the default 8-ball button; set to wherever the 9-ball Pattern lands in the config (e.g. 1) on the 9-ball button. Not used when Mode is CueBallOnly.")]
    [SerializeField] private int _targetPatternIndex = 0;
    [SerializeField] private string _promptLabel = "Reset Table";

    public string PromptLabel => _promptLabel;

    public void OnInteract(PlayerObject player)
    {
        // No IsServerInitialized gate here — this must be callable by ANY
        // client's local interaction, same as Pushable/Grabbable. The old
        // gate checked whether the PRESSING player's own machine was the
        // server, which is only ever true for host — so a client's press
        // was silently dropped before the ServerRpc below even fired.
        ServerActivate();
    }

    [ServerRpc(RequireOwnership = false)]
    private void ServerActivate()
    {
        if (LobbySpawner.Instance == null)
        {
            Debug.LogWarning("[PoolResetButton] No LobbySpawner.Instance found.");
            return;
        }

        switch (_mode)
        {
            case ResetMode.FullReset:
                LobbySpawner.Instance.SwitchPoolPattern(_poolSetupIndex, _targetPatternIndex);
                break;
            case ResetMode.CueBallOnly:
                LobbySpawner.Instance.ResetCueBall(_poolSetupIndex);
                break;
            case ResetMode.NineBallReset:
                LobbySpawner.Instance.SwitchPoolPattern(_poolSetupIndex, _targetPatternIndex);
                break;
        }
    }
}
