// BowlingPanelButton.cs
using FishNet.Object;
using UnityEngine;

/// <summary>
/// One physical button on a bowling lane's control panel — same
/// IInteractable pattern as PoolResetButton/BowlingJoinStation (not a UI
/// element), and the same "one script, _action enum picks the behavior per
/// instance" pattern PoolResetButton uses for its three modes. Place five
/// of these on a panel, one per Action, to cover Join/Leave/Start/frame
/// count/pin layout — replaces the single-purpose BowlingJoinStation.
///
/// CycleFrameCount/CyclePinLayout step through a fixed option list one
/// press at a time rather than offering a dropdown — there's no in-world
/// UI canvas in this project (see the project's lobby-prop-setups notes),
/// so a real "pick from a list" widget isn't available yet. Confirmation is
/// a console log only until Phase 4's scoreboard UI exists to show it
/// on-screen.
/// </summary>
public class BowlingPanelButton : NetworkBehaviour, IInteractable
{
    public enum PanelAction
    {
        Join,
        Leave,
        Start,
        CycleFrameCount,
        CyclePinLayout
    }

    [Tooltip("The BowlingGameController for the lane this button controls. Both are scene objects, so a direct reference works fine here — no prefab/scene-reference restriction to work around.")]
    [SerializeField] private BowlingGameController _gameController;
    [SerializeField] private PanelAction _action = PanelAction.Join;
    [SerializeField] private string _promptLabel = "Join";

    public string PromptLabel => _promptLabel;

    public void OnInteract(PlayerObject player)
    {
        // No IsServerInitialized gate — must be callable from any client's
        // own local interaction; the server-only decision happens inside
        // the ServerRpc below. A gate here would silently drop every
        // non-host player's press (see PoolResetButton's comment on the
        // same bug).
        ServerActivate(player);
    }

    [ServerRpc(RequireOwnership = false)]
    private void ServerActivate(PlayerObject player)
    {
        if (_gameController == null)
        {
            Debug.LogWarning("[BowlingPanelButton] No GameController assigned.");
            return;
        }

        switch (_action)
        {
            case PanelAction.Join:
                _gameController.ServerJoin(player);
                break;
            case PanelAction.Leave:
                _gameController.ServerLeave(player);
                break;
            case PanelAction.Start:
                _gameController.ServerStart(player);
                break;
            case PanelAction.CycleFrameCount:
                _gameController.ServerCycleFrameCount();
                break;
            case PanelAction.CyclePinLayout:
                _gameController.ServerCyclePinLayout();
                break;
        }
    }
}
