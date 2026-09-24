// StageSetupConfig
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Defines the set of performance "acts" a stage can be switched between —
/// Mic Only, Piano + Mic, Guitar + Mic, Flute + Mic and a 5th placeholder
/// slot reserved for a future act. Each Act is a self-contained prefab pair
/// (mic stand + optional instrument) with its own offset from the stage's
/// Anchor — same authored-offset convention as PropSetupConfig's Entries,
/// just renamed (Acts instead of Patterns/Entries) since "pattern" reads
/// oddly for "which performance is currently set up."
///
/// Only ONE act is ever spawned at a time per stage (unlike PropSetupConfig,
/// where every entry in the active Pattern spawns together) — switching acts
/// despawns the previous act's prefabs and spawns the new one's. See
/// LobbySpawner.SwitchStageAct/SwitchStageActRoutine.
///
/// Prefab references live directly on this ScriptableObject (MicStandPrefab/
/// each InstrumentVariant's Prefab below), same as PropSetupConfig's
/// PropEntry.Prefab — NOT moved onto the scene-side StageSetupInstance the
/// way PoolSetupConfig's RackPrefab was. That move was only needed there
/// because BillardBall_Triangle is a Prefab Variant rooted in an FBX Model,
/// which hit a Unity bug ("Type mismatch," no console error) when assigned
/// into a ScriptableObject field specifically. If a mic stand/instrument
/// prefab silently fails to assign here with the same symptom, move that one
/// field onto StageSetupInstance in LobbySpawner.cs instead (mirroring
/// PoolSetupInstance.RackPrefab) rather than moving all of them.
/// </summary>
[CreateAssetMenu(fileName = "New Stage Setup Config", menuName = "ChaosPit/Lobby/Stage Setup Config")]
public class StageSetupConfig : ScriptableObject
{
    [Header("Identity")]
    public string SetupLabel;

    [Header("Acts")]
    [Tooltip("One entry per performance act (Mic Only, Piano + Mic, Guitar + Mic, Guitar + Mic, TBD #5). Order here is what fills the StageActSelector dropdown.")]
    public List<StageAct> Acts = new List<StageAct>();

    [Tooltip("Index into Acts that this stage spawns with at Lobby load. There's no RandomizePattern equivalent — a stage always starts on a specific, chosen act rather than a random performance.")]
    public int DefaultActIndex = 0;
}

[System.Serializable]
public class StageAct
{
    [Tooltip("Shown in the StageActSelector dropdown (e.g. \"Mic Only\", \"Piano + Mic\"). Leave a slot's whole entry mostly empty with just a name set (e.g. \"TBD\") to reserve the 5th act slot without it doing anything yet.")]
    public string ActName;

    [Header("Mic Stand")]
    [Tooltip("Every act needs a mic stand — even the placeholder slot should have one so the stage never sits empty when it's the active act. Leave null only for a true no-op placeholder.")]
    public GameObject MicStandPrefab;
    [Tooltip("Offset from the stage's Anchor, in the Anchor's local space (position rotated by Anchor's rotation, then added — same convention as PropSetupConfig.PropEntry).")]
    public Vector3 MicStandPosition;
    public Vector3 MicStandRotation;
    public Vector3 MicStandScale = Vector3.one;

    [Header("Mic")]
    [Tooltip("The actual mic head prop, spawned separately from the stand and attached at MicAnchorName. Leave null for an act with no mic of its own (unusual, but supported the same way an empty MicStandPrefab is).")]
    public GameObject MicPrefab;
    [Tooltip("Type the exact child name from MicStandPrefab's Hierarchy where the mic attaches (e.g. \"MicAnchor\") — plain text matching a slot name, same convention as PoolSetupConfig.PoolSlotAssignment.SlotName, not a dragged Transform reference. A design-time reference can't point at a child on a prefab instance that only exists once this act's stand is actually spawned, and different mic stand styles can name their anchor differently, so this is resolved via Transform.Find on the spawned stand at spawn time (see LobbySpawner.SpawnStageActRoutine). Must match exactly (case-sensitive) or the mic won't appear — check the Console for a warning if it doesn't.")]
    public string MicAnchorName;
    public Vector3 MicScale = Vector3.one;

    [Header("Instrument (optional)")]
    [Tooltip("Leave empty for acts with no instrument (Talk Mic, Singing Mic). One entry per purely-visual variant (e.g. 5 guitar skins) — all variants share this act's InstrumentPosition/Rotation/Scale below, since they're just different-looking prefabs occupying the same socket, not different instruments. Each variant prefab is expected to carry its own InstrumentInteractable component (with its own trigger clips assigned) — the function is the same across variants, only the visual differs, so nothing about behavior branches on which variant is picked.")]
    public List<InstrumentVariant> InstrumentVariants = new List<InstrumentVariant>();
    [Tooltip("Index into InstrumentVariants this act spawns with at Lobby load — StageActSelector lets it be switched afterward. Ignored if InstrumentVariants is empty.")]
    public int DefaultVariantIndex = 0;
    public Vector3 InstrumentPosition;
    public Vector3 InstrumentRotation;
    public Vector3 InstrumentScale = Vector3.one;
}

[System.Serializable]
public class InstrumentVariant
{
    [Tooltip("Shown in StageActSelector's variant dropdown when an act has more than one (e.g. \"Red Guitar\", \"Black Guitar\"). Purely a label — has no effect on behavior.")]
    public string VariantName;
    public GameObject Prefab;
}
