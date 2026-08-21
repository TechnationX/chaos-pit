// PoolSetupConfig
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Defines a pool table's rack setup. Unlike PropSetupConfig (independent
/// pieces at hand-authored offsets), the rack prefab (e.g. BillardBall_Triangle)
/// already has its ball positions built in as child slot GameObjects (e.g.
/// "BallHolder_1".."BallHolder_15") — each Pattern is just a list of
/// (SlotName, BallPrefab) pairs. SlotName is typed as plain text matching the
/// child's exact name on the rack prefab, rather than a dragged Transform
/// reference — the Unity Object Picker was throwing "Type mismatch" on
/// nested prefab children in this project, so this sidesteps it entirely.
///
/// NOTE: the rack prefab reference itself lives on PoolSetupInstance in
/// LobbySpawner.cs, not here. BillardBall_Triangle is a Prefab Variant
/// rooted in an FBX Model, and assigning it into a field on this
/// ScriptableObject asset consistently failed with "Type mismatch" with no
/// error logged anywhere, while the exact same prefab assigned fine into a
/// MonoBehaviour field. Keeping the rack prefab on the scene-side
/// MonoBehaviour instance instead of this asset sidesteps whatever that
/// asset-vs-scene edge case is.
/// </summary>
[CreateAssetMenu(fileName = "New Pool Setup Config", menuName = "ChaosPit/Lobby/Pool Setup Config")]
public class PoolSetupConfig : ScriptableObject
{
    [Header("Identity")]
    public string SetupLabel;

    [Header("Patterns")]
    [Tooltip("Different ball-to-slot assignments (e.g. \"Full Rack\", \"Practice - 3 Ball\").")]
    public List<PoolRackPattern> Patterns = new List<PoolRackPattern>();

    [Tooltip("If true, a random Pattern is chosen at spawn time. If false, ActivePatternIndex is used.")]
    public bool RandomizePattern = false;

    [Tooltip("Index into Patterns used when RandomizePattern is false.")]
    public int ActivePatternIndex = 0;
}

[System.Serializable]
public class PoolRackPattern
{
    public string PatternName;
    [Tooltip("One entry per ball you want placed. Leave a slot out of the list to leave it empty.")]
    public List<PoolSlotAssignment> SlotAssignments = new List<PoolSlotAssignment>();
}

[System.Serializable]
public class PoolSlotAssignment
{
    [Tooltip("Type the exact child name from the rack prefab, e.g. \"BallHolder_8\". Must match exactly (case-sensitive) or it won't resolve at spawn time — check the Console for a warning if a ball doesn't appear.")]
    public string SlotName;
    public GameObject BallPrefab;
    public Vector3 Scale = Vector3.one;
}