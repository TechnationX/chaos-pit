// StageActSelector.cs
using System.Collections.Generic;
using FishNet.Object;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Physical in-world prop that opens a stage's act-selection menu — same
/// IInteractable pattern as Grabbable/PoolResetButton, not a UI element on
/// the physical-prop side. The menu itself (_menuCanvas below) IS real UI
/// though, same proven Canvas + EventSystem pattern SettingsPanelController
/// already uses safely — it's just opened by a physical trigger instead of a
/// pause-menu button. Real UI Buttons/Dropdowns don't work through the
/// physical raycast IInteractable system (documented bowling-panel lesson),
/// so this deliberately keeps the two worlds separate: click the prop to
/// open a genuine Canvas menu, then interact with genuine UI inside it.
///
/// Selecting an act in the dropdown does NOT fire anything over the network
/// by itself — it only updates _pendingActIndex locally. Only pressing the
/// Switch button actually commits the change (ServerSwitchAct below). This
/// was an explicit design correction: the first draft fired the switch RPC
/// straight from the dropdown's onValueChanged, which was rejected in favor
/// of requiring a deliberate confirm press.
///
/// A second, optional dropdown handles instrument variant sub-selection —
/// e.g. Guitar + Mic having 5 purely-visual guitar skins, all with identical
/// function. It only shows/populates when the currently pending act actually
/// has more than one InstrumentVariant; acts with zero or one variant (Talk
/// Mic, Singing Mic, TBD, or an instrument act nobody's bothered to reskin
/// yet) just hide it and implicitly switch to variant 0. Same local-only-
/// until-Switch rule applies to it as the act dropdown.
/// </summary>
public class StageActSelector : NetworkBehaviour, IInteractable
{
    [Header("Stage Link")]
    [Tooltip("Index into LobbySpawner's Stage Setups list — must match the stage this selector controls.")]
    [SerializeField] private int _stageSetupIndex = 0;
    [Tooltip("Same StageSetupConfig assigned to that stage's entry in LobbySpawner. Read locally only, to populate the dropdowns' labels — the server is the one that actually resolves setupIndex/actIndex/variantIndex against LobbySpawner's real data, so a mismatch here only makes the dropdown labels wrong, not the switch itself.")]
    [SerializeField] private StageSetupConfig _config;
    [SerializeField] private string _promptLabel = "Change Act";

    [Header("Menu UI (world-space Canvas)")]
    [SerializeField] private GameObject _menuCanvas;
    [SerializeField] private TMP_Dropdown _actDropdown;
    [Tooltip("Sub-selection for an act's instrument variant (e.g. which guitar skin). Its GameObject is shown/hidden automatically based on whether the currently pending act has more than one InstrumentVariant — assign the dropdown's own GameObject here, not just the TMP_Dropdown component's parent, so SetActive works.")]
    [SerializeField] private GameObject _variantDropdownRoot;
    [SerializeField] private TMP_Dropdown _variantDropdown;
    [SerializeField] private Button _switchButton;
    [SerializeField] private Button _closeButton;

    private int _pendingActIndex = 0;
    private int _pendingVariantIndex = 0;
    private PlayerObject _openedBy;
    private bool _isOpen = false;

    public string PromptLabel => _promptLabel;

    private void Awake()
    {
        if (_menuCanvas != null) _menuCanvas.SetActive(false);
        if (_switchButton != null) _switchButton.onClick.AddListener(OnSwitchPressed);
        if (_closeButton != null) _closeButton.onClick.AddListener(CloseMenu);
        if (_actDropdown != null) _actDropdown.onValueChanged.AddListener(OnActDropdownChanged);
        if (_variantDropdown != null) _variantDropdown.onValueChanged.AddListener(OnVariantDropdownChanged);
    }

    private void Start()
    {
        BuildActDropdownOptions();
    }

    private void BuildActDropdownOptions()
    {
        if (_actDropdown == null || _config == null || _config.Acts == null) return;

        List<string> names = new List<string>();
        foreach (StageAct act in _config.Acts)
            names.Add(string.IsNullOrEmpty(act.ActName) ? "(unnamed act)" : act.ActName);

        _actDropdown.ClearOptions();
        _actDropdown.AddOptions(names);

        _pendingActIndex = Mathf.Clamp(_config.DefaultActIndex, 0, Mathf.Max(0, names.Count - 1));
        _actDropdown.SetValueWithoutNotify(_pendingActIndex);

        BuildVariantDropdownOptions(_pendingActIndex);
    }

    // Populates the variant dropdown for whichever act is currently pending
    // and shows/hides it — an act with 0 or 1 variants has nothing worth
    // choosing between, so the dropdown stays hidden and _pendingVariantIndex
    // just defaults to 0 (SpawnStageActRoutine treats that as "no variant" on
    // an empty list regardless).
    private void BuildVariantDropdownOptions(int actIndex)
    {
        List<InstrumentVariant> variants = (_config != null && _config.Acts != null && actIndex >= 0 && actIndex < _config.Acts.Count)
            ? _config.Acts[actIndex].InstrumentVariants
            : null;

        bool hasChoice = variants != null && variants.Count > 1;

        if (_variantDropdownRoot != null) _variantDropdownRoot.SetActive(hasChoice);
        if (!hasChoice)
        {
            _pendingVariantIndex = 0;
            return;
        }

        List<string> names = new List<string>();
        foreach (InstrumentVariant variant in variants)
            names.Add(string.IsNullOrEmpty(variant.VariantName) ? "(unnamed variant)" : variant.VariantName);

        _variantDropdown.ClearOptions();
        _variantDropdown.AddOptions(names);

        int defaultVariant = Mathf.Clamp(_config.Acts[actIndex].DefaultVariantIndex, 0, names.Count - 1);
        _pendingVariantIndex = defaultVariant;
        _variantDropdown.SetValueWithoutNotify(defaultVariant);
    }

    // No IsServerInitialized gate — same reasoning as PoolResetButton/
    // InstrumentInteractable: must be callable by any client's local
    // interaction, not just whichever client happens to also be host.
    // While the menu is open, player.Interaction is disabled (see
    // OpenMenu below), so this can't fire again until it's closed —
    // closing is handled entirely by the Canvas's own Close/Switch buttons,
    // not by re-clicking the physical prop.
    public void OnInteract(PlayerObject player)
    {
        if (!_isOpen) OpenMenu(player);
    }

    private void OpenMenu(PlayerObject player)
    {
        if (_menuCanvas == null) return;

        _openedBy = player;
        _isOpen = true;
        _menuCanvas.SetActive(true);

        // Always land back on the stage's current act (and that act's
        // current variant) each time the menu opens, rather than whatever
        // was left pending from a previous visit that never got confirmed.
        BuildActDropdownOptions();

        player.Camera?.ReleaseCursor();
        player.Interaction?.SetInteractionEnabled(false);
        player.Movement?.SetMovementLocked(true, "stage_act_selector");
    }

    private void CloseMenu()
    {
        if (!_isOpen) return;

        if (_menuCanvas != null) _menuCanvas.SetActive(false);
        _isOpen = false;

        if (_openedBy != null)
        {
            _openedBy.Camera?.LockCursor();
            _openedBy.Interaction?.SetInteractionEnabled(true);
            _openedBy.Movement?.SetMovementLocked(false, "stage_act_selector");
        }

        _openedBy = null;
    }

    // Local-only pending selection — see class comment for why this doesn't
    // fire anything over the network. Changing the act also rebuilds the
    // variant dropdown, since variants are per-act.
    private void OnActDropdownChanged(int index)
    {
        _pendingActIndex = index;
        BuildVariantDropdownOptions(_pendingActIndex);
    }

    private void OnVariantDropdownChanged(int index)
    {
        _pendingVariantIndex = index;
    }

    private void OnSwitchPressed()
    {
        ServerSwitchAct(_pendingActIndex, _pendingVariantIndex);
        CloseMenu();
    }

    [ServerRpc(RequireOwnership = false)]
    private void ServerSwitchAct(int actIndex, int variantIndex)
    {
        if (LobbySpawner.Instance == null)
        {
            Debug.LogWarning("[StageActSelector] No LobbySpawner.Instance found.");
            return;
        }

        LobbySpawner.Instance.SwitchStageAct(_stageSetupIndex, actIndex, variantIndex);
    }
}
