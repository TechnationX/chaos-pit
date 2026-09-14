// KioskJoinButton.cs

using UnityEngine;
using UnityEngine.UI;

// Physical, walk-up-and-click interactable — same IInteractable pattern as
// ConsoleButton/KickButton — wrapping a real Unity UI Button that lives on
// the station's exterior kiosk display Canvas. The Button's own onClick
// event is never used: InteractionManager's raycast finds this component
// directly (via a Collider on this GameObject, sized to the button) and
// calls OnInteract(). The Button is only here so its `.interactable` flag
// drives Unity's built-in disabled-color tint — that's the "greys out when
// locked" visual, with no extra art or material work needed.
//
// Requires, in the Editor: a Collider on this GameObject on the same layer
// as _interactableLayer (InteractionManager.cs) — UI RectTransforms have no
// collider by default, so the physics raycast can't find this button
// without one added manually, sized/positioned to match the Button's
// RectTransform.
public class KioskJoinButton : MonoBehaviour, IInteractable
{
    [SerializeField] private Button _button;
    [SerializeField] private MinigameStation _station;

    public string PromptLabel => _button != null && _button.interactable ? "Join" : "Room Locked";

    private void Awake()
    {
        Refresh();
    }

    // Called by MinigameStation (Awake + every UpdateSessionState) whenever
    // the room's joinable/locked state might have changed.
    public void Refresh()
    {
        if (_button == null || _station == null) return;
        _button.interactable = _station.CanJoin;
    }

    public void OnInteract(PlayerObject player)
    {
        if (_station == null || _button == null || !_button.interactable) return;
        _station.RequestJoin(player);
    }
}
