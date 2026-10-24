// StageActSelector.cs
using System.Collections.Generic;
using FishNet.Object;
using UnityEngine;

/// <summary>
/// Coordinates one stage's act-selection button bank. NOT an IInteractable
/// itself and NOT a UI/Canvas component — there is no menu to open or close.
/// Each physical button on the panel (StageActSelectorButton) is its own
/// always-live IInteractable prop, using the same camera-raycast interaction
/// every other prop in the game uses; this component just holds the pending
/// selection state those buttons write into and fires the actual network
/// switch when the Switch button is pressed.
///
/// Act/Variant/Switch buttons are now SCRIPTED SPAWNS rather than
/// hand-placed scene objects — once an act could have 5+ instrument
/// variants, hand-placing and individually wiring every button got
/// unwieldy. Act and Switch buttons (a fixed, known count) spawn once at
/// Awake and are never destroyed. Variant buttons are destroyed and
/// re-spawned every time the pending act changes, since different acts can
/// have very different variant counts (5 guitar skins vs. 2 piano skins) —
/// simpler than keeping a pool of hidden buttons sized to the largest act.
///
/// Deliberately spawned with plain Instantiate(), NOT through LobbySpawner /
/// FishNet's NetworkObject+ServerManager.Spawn path every other runtime prop
/// in this project uses. That path exists to keep gameplay-relevant state
/// (position, held/grabbed state, etc.) synced across peers. These buttons
/// carry no state that needs syncing at all — every client builds its own
/// local copies from the same deterministic StageSetupConfig asset, and the
/// only action that actually needs to reach other peers (committing a
/// Switch) already goes through this class's own ServerRpc regardless of
/// how the button GameObject itself was created. Networking them would add
/// NetworkObject/observer/prefab-registration overhead for zero benefit.
///
/// Still a NetworkBehaviour (unlike the plain-MonoBehaviour
/// StageActSelectorButton) purely to own the ServerRpc — nothing on this
/// component is synced or observed. Must be a scene-placed NetworkObject
/// (FishNet auto-initializes those at scene load) rather than spawned.
/// </summary>
public enum StageButtonLayoutDirection
{
    Right,
    Left,
    Up,
    Down
}

public class StageActSelector : NetworkBehaviour
{
    [Tooltip("Which stage this panel controls — matches the index of this stage's StageSetupInstance in LobbySpawner's list, same convention as every other setupIndex-driven lookup in that file.")]
    [SerializeField] private int _stageSetupIndex;

    [Tooltip("Drives everything below: which Acts exist, their names, and each Act's InstrumentVariants. Also used once, at Awake, to seed the pending act with this stage's configured default.")]
    [SerializeField] private StageSetupConfig _config;

    [Header("Scripted Spawn — Prefabs")]
    [Tooltip("Spawned once per Act in _config.Acts, at Awake, under _actButtonsRoot. Leave null to skip scripted Act-button spawning entirely (e.g. if you'd rather hand-place and hand-wire a small fixed set of Act buttons the old way).")]
    [SerializeField] private StageActSelectorButton _actButtonPrefab;
    [Tooltip("Spawned fresh under _variantButtonsRoot every time the pending act changes — one per entry in that act's InstrumentVariants. Leave null for a stage with no instrument acts (or to hand-place variant buttons instead).")]
    [SerializeField] private StageActSelectorButton _variantButtonPrefab;
    [Tooltip("Spawned once, at Awake, at _switchButtonSpawnPoint. Leave null to hand-place the Switch button instead (there's only ever one, so scripting it saves little).")]
    [SerializeField] private StageActSelectorButton _switchButtonPrefab;

    [Header("Scripted Spawn — Layout")]
    [Tooltip("Parent for spawned Act buttons. Button 0 sits exactly at this Transform's position/rotation; every button after that is laid out in a grid (see _actButtonGridColumns/_actButtonColumnSpacing/_actButtonRowSpacing below), in this root's local space.")]
    [SerializeField] private Transform _actButtonsRoot;
    [Tooltip("How many columns wide the Act-button grid is before wrapping to a new row. 4 acts at 2 columns gives a 2x2 grid; a 5th act (e.g. the TBD placeholder) would start a 3rd row rather than breaking the grid.")]
    [SerializeField] private int _actButtonGridColumns = 2;
    [Tooltip("Local offset applied per column within a row, in _actButtonsRoot's own local units. IMPORTANT: the current button prefabs (StageActButton/StageVariantButton/StageSwitchButton) are uGUI RectTransform elements — Button/Image/CanvasRenderer under a World Space Canvas — sized by their own SizeDelta (currently 320x64), NOT meter-scale 3D props. So this needs to be on that same hundreds-of-units scale (e.g. (340,0,0), a bit more than the button's own width) rather than a small meter-ish offset like (0.5,0,0) — a magnitude of 1 is a sliver of a pixel and leaves every button stacked almost exactly on top of the last. If these are ever swapped for real 3D prop buttons instead, drop this back down to a meter-scale value.")]
    [SerializeField] private Vector3 _actButtonColumnSpacing = new Vector3(340f, 0f, 0f);
    [Tooltip("Local offset applied per row, same units/convention as _actButtonColumnSpacing. Negative Y moves each successive row down the panel/screen (standard UI \"+Y is up\" convention), so row 0 stays on top.")]
    [SerializeField] private Vector3 _actButtonRowSpacing = new Vector3(0f, -80f, 0f);
    [Tooltip("Local rotation (Euler angles, relative to _actButtonsRoot) applied to every spawned Act button. A World Space Canvas's UI elements face \"backward\" by default — (0,180,0) is the usual fix to make them face the viewer. Previously this was hardcoded to Quaternion.identity in code, which silently wiped out any rotation you set directly on the StageActButton prefab every time it re-spawned — this field is the real place to tune it now.")]
    [SerializeField] private Vector3 _actButtonRotation = new Vector3(0f, 180f, 0f);

    [Tooltip("Parent for spawned Variant buttons. Same per-index local-offset convention and UI-pixel-scale caveat as the Act buttons above.")]
    [SerializeField] private Transform _variantButtonsRoot;
    [Tooltip("Which way the Variant line grows as index increases — Right/Left move along local X, Up/Down along local Y (same \"+Y is up\" convention as _actButtonRowSpacing). Replaces hand-typing a signed Vector3 to flip direction; combine with _variantButtonSpacingDistance below for the actual gap.")]
    [SerializeField] private StageButtonLayoutDirection _variantButtonDirection = StageButtonLayoutDirection.Right;
    [Tooltip("Distance between each Variant button, in _variantButtonsRoot's own local units (same UI-pixel-scale caveat as _actButtonColumnSpacing — this is hundreds of units, not meters, for the current uGUI button prefabs).")]
    [SerializeField] private float _variantButtonSpacingDistance = 340f;
    [Tooltip("Local rotation (Euler angles, relative to _variantButtonsRoot) applied to every spawned Variant button. Same facing convention/caveat as _actButtonRotation.")]
    [SerializeField] private Vector3 _variantButtonRotation = new Vector3(0f, 180f, 0f);

    [Tooltip("Where the single Switch button spawns — its local position is zeroed to sit exactly at this Transform, no spacing math needed since there's only one.")]
    [SerializeField] private Transform _switchButtonSpawnPoint;
    [Tooltip("Local rotation (Euler angles, relative to _switchButtonSpawnPoint) applied to the spawned Switch button. Same facing convention/caveat as _actButtonRotation.")]
    [SerializeField] private Vector3 _switchButtonRotation = new Vector3(0f, 180f, 0f);

    // Local-only pending selection — written by Act/Variant buttons calling
    // SelectAct/SelectVariant below, read only when Switch() is pressed.
    // Deliberately not networked/synced: this is "what will I switch to if I
    // press Switch," purely a per-viewer local UI state, not game state.
    private int _pendingActIndex;
    private int _pendingVariantIndex;

    // Tracks only the currently-spawned Variant buttons, since those are the
    // ones that get destroyed/re-spawned on every act change. Act/Switch
    // buttons are spawned once and never need to be found again.
    private readonly List<StageActSelectorButton> _spawnedVariantButtons = new List<StageActSelectorButton>();

    private void Awake()
    {
        if (_config != null)
        {
            _pendingActIndex = _config.DefaultActIndex;
        }

        SpawnActButtons();
        SpawnSwitchButton();
        RefreshVariantButtons();
    }

    private void SpawnActButtons()
    {
        if (_actButtonPrefab == null || _actButtonsRoot == null || _config == null) return;

        // Grid layout: index 0 is top-left, filling across columns before
        // wrapping to the next row — e.g. 4 acts at _actButtonGridColumns=2
        // lays out as a clean 2x2 grid. A 5th act (the TBD placeholder)
        // would just start a 3rd row of 1, not resize the grid.
        int columns = Mathf.Max(1, _actButtonGridColumns);

        for (int i = 0; i < _config.Acts.Count; i++)
        {
            int column = i % columns;
            int row = i / columns;

            StageActSelectorButton button = Instantiate(_actButtonPrefab, _actButtonsRoot);
            button.transform.localPosition = (_actButtonColumnSpacing * column) + (_actButtonRowSpacing * row);
            button.transform.localRotation = Quaternion.Euler(_actButtonRotation);

            string actName = _config.Acts[i].ActName;
            string label = string.IsNullOrEmpty(actName) ? $"Act {i}" : actName;
            button.Initialize(this, StageActSelectorButtonRole.Act, i, 0, label);
        }
    }

    private void SpawnSwitchButton()
    {
        if (_switchButtonPrefab == null) return;

        // Must use the Instantiate(prefab, parentTransform) overload, NOT
        // Instantiate(prefab, position, rotation) — that position/rotation
        // overload leaves the new object with NO parent at all. These button
        // prefabs are uGUI RectTransforms (Button/Image/CanvasRenderer) that
        // only draw while somewhere under a Canvas; spawned with no parent,
        // the Switch button existed (Initialize() still ran, OnInteract()
        // would still have worked if you could find it) but was invisible,
        // since it had no ancestor Canvas to render through. Parenting it
        // under _switchButtonSpawnPoint (itself under the stage's Canvas)
        // fixes that, same as Act/Variant buttons already do.
        Transform parent = _switchButtonSpawnPoint != null ? _switchButtonSpawnPoint : transform;
        StageActSelectorButton button = Instantiate(_switchButtonPrefab, parent);
        button.transform.localPosition = Vector3.zero;
        button.transform.localRotation = Quaternion.Euler(_switchButtonRotation);

        button.Initialize(this, StageActSelectorButtonRole.Switch, 0, 0, "Switch");
    }

    // Destroys whatever Variant buttons exist for the previously-pending act
    // and spawns a fresh set for the newly-pending one. Called once from
    // Awake (to populate the default act's variants) and again every time
    // SelectAct() runs.
    private void RefreshVariantButtons()
    {
        foreach (StageActSelectorButton old in _spawnedVariantButtons)
        {
            if (old != null) Destroy(old.gameObject);
        }
        _spawnedVariantButtons.Clear();

        if (_variantButtonPrefab == null || _variantButtonsRoot == null || _config == null) return;
        if (_pendingActIndex < 0 || _pendingActIndex >= _config.Acts.Count) return;

        List<InstrumentVariant> variants = _config.Acts[_pendingActIndex].InstrumentVariants;
        if (variants == null) return;

        Vector3 variantSpacing = GetVariantButtonSpacing();

        for (int i = 0; i < variants.Count; i++)
        {
            StageActSelectorButton button = Instantiate(_variantButtonPrefab, _variantButtonsRoot);
            button.transform.localPosition = variantSpacing * i;
            button.transform.localRotation = Quaternion.Euler(_variantButtonRotation);

            string variantName = variants[i].VariantName;
            string label = string.IsNullOrEmpty(variantName) ? $"Variant {i}" : variantName;
            button.Initialize(this, StageActSelectorButtonRole.Variant, i, _pendingActIndex, label);

            _spawnedVariantButtons.Add(button);
        }
    }

    // Turns _variantButtonDirection + _variantButtonSpacingDistance into the
    // actual per-index local offset. Right/Left run along local X, Up/Down
    // along local Y — same axis convention _actButtonRowSpacing already uses
    // (+Y is up), just picked from a dropdown instead of a signed Vector3.
    private Vector3 GetVariantButtonSpacing()
    {
        switch (_variantButtonDirection)
        {
            case StageButtonLayoutDirection.Right:
                return new Vector3(_variantButtonSpacingDistance, 0f, 0f);
            case StageButtonLayoutDirection.Left:
                return new Vector3(-_variantButtonSpacingDistance, 0f, 0f);
            case StageButtonLayoutDirection.Up:
                return new Vector3(0f, _variantButtonSpacingDistance, 0f);
            case StageButtonLayoutDirection.Down:
                return new Vector3(0f, -_variantButtonSpacingDistance, 0f);
            default:
                return new Vector3(_variantButtonSpacingDistance, 0f, 0f);
        }
    }

    // Called by a StageActSelectorButton in the Act role.
    public void SelectAct(int actIndex)
    {
        _pendingActIndex = actIndex;
        // Switching the pending act always resets the pending variant back
        // to 0 rather than trying to carry a variant index across to an
        // act that may not have that many variants (or any at all).
        _pendingVariantIndex = 0;
        RefreshVariantButtons();
    }

    // Called by a StageActSelectorButton in the Variant role.
    public void SelectVariant(int variantIndex)
    {
        _pendingVariantIndex = variantIndex;
    }

    // Called by the single StageActSelectorButton in the Switch role. This
    // is the only one of the three that actually reaches the network.
    public void Switch()
    {
        ServerSwitchAct(_pendingActIndex, _pendingVariantIndex);
    }

    [ServerRpc(RequireOwnership = false)]
    private void ServerSwitchAct(int actIndex, int variantIndex)
    {
        if (LobbySpawner.Instance == null)
        {
            Debug.LogWarning("[StageActSelector] LobbySpawner.Instance is null — can't switch act.");
            return;
        }

        LobbySpawner.Instance.SwitchStageAct(_stageSetupIndex, actIndex, variantIndex);
    }
}
