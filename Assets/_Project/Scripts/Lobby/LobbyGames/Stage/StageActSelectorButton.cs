// StageActSelectorButton.cs
using TMPro;
using UnityEngine;

/// <summary>
/// One physical, always-live button on a stage's act-selection panel — same
/// IInteractable pattern as every other physical prop in the game
/// (Grabbable, PoolResetButton, InstrumentInteractable), not a UI element.
/// There's no menu to open: this button just sits there and reacts to the
/// normal camera-forward-raycast + left-click flow every other interactable
/// already uses, exactly like PoolResetButton's lever/button props.
///
/// One script, a role flag per instance — same convention as
/// PauseMenuUI/BowlingPanelButton's _context flag — rather than three
/// near-identical scripts for Act/Variant/Switch buttons.
///
/// Normally scripted-spawned now: StageActSelector instantiates these from a
/// prefab and calls Initialize() right after, rather than every instance
/// being hand-placed in the scene with its fields set in the Inspector (that
/// got unwieldy once an act could have 5+ instrument variants). The
/// Inspector fields are left serialized rather than made purely runtime-only
/// so a button CAN still be hand-placed/hand-wired the old way if that ever
/// suits a particular stage better (e.g. hand-placing the one Switch button
/// while scripting the Variant buttons) — Initialize() just overwrites
/// whatever was set in the Inspector.
/// </summary>
public enum StageActSelectorButtonRole
{
    Act,
    Variant,
    Switch
}

public class StageActSelectorButton : MonoBehaviour, IInteractable
{
    [SerializeField] private StageActSelector _selector;
    [SerializeField] private StageActSelectorButtonRole _role = StageActSelectorButtonRole.Act;

    [Tooltip("Act role: which act this button selects (index into the StageSetupConfig's Acts list). Variant role: which variant this button selects within its owner act. Ignored for Switch. Set automatically by StageActSelector.Initialize() for scripted-spawned buttons.")]
    [SerializeField] private int _index;

    [Tooltip("Variant role ONLY: which act this variant button belongs to. Ignored for Act/Switch roles. Set automatically by StageActSelector.Initialize() for scripted-spawned buttons.")]
    [SerializeField] private int _ownerActIndex;

    [SerializeField] private string _promptLabel = "Select";

    [Tooltip("Optional on-prop text (e.g. a sign reading \"Red Guitar\" or \"Talk Mic\"). Leave null if this prefab has no label mesh — the interaction prompt UI still shows PromptLabel either way; this is purely a visual label on the physical button itself.")]
    [SerializeField] private TMP_Text _labelText;

    public string PromptLabel => _promptLabel;
    public StageActSelectorButtonRole Role => _role;
    public int OwnerActIndex => _ownerActIndex;

    // Called by StageActSelector right after Instantiate for a
    // scripted-spawned button. Overwrites whatever these fields held in the
    // prefab/Inspector — a hand-placed button that's never passed through
    // this method keeps its own Inspector-authored values untouched.
    public void Initialize(StageActSelector selector, StageActSelectorButtonRole role, int index, int ownerActIndex, string promptLabel)
    {
        _selector = selector;
        _role = role;
        _index = index;
        _ownerActIndex = ownerActIndex;
        _promptLabel = promptLabel;

        if (_labelText != null)
        {
            _labelText.text = promptLabel;
        }
    }

    // No IsServerInitialized gate — same reasoning as every other physical
    // interactable in this game: must be callable by any client's local
    // interaction, not just whichever client happens to also be host.
    // Everything here is a LOCAL call into the owning StageActSelector
    // (not an RPC) — it only updates a pending selection. Only Switch
    // actually reaches the network, via StageActSelector.Switch().
    public void OnInteract(PlayerObject player)
    {
        if (_selector == null)
        {
            Debug.LogWarning($"[StageActSelectorButton] {name} has no StageActSelector assigned.");
            return;
        }

        switch (_role)
        {
            case StageActSelectorButtonRole.Act:
                _selector.SelectAct(_index);
                break;
            case StageActSelectorButtonRole.Variant:
                _selector.SelectVariant(_index);
                break;
            case StageActSelectorButtonRole.Switch:
                _selector.Switch();
                break;
        }
    }
}
