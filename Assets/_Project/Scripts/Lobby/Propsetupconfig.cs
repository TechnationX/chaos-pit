// PropSetupConfig
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Defines one mini prop setup (e.g. Chess Set, Bowling Lane, Pool Table) as a
/// reusable template: a list of named Patterns, each a different arrangement
/// of props built from the same piece set — swap ActivePatternIndex (or
/// enable RandomizePattern) to change which arrangement spawns, without
/// touching code.
///
/// This config holds no world/anchor position — where a setup is placed in
/// the lobby is a scene concern, wired up on LobbySpawner via a Transform
/// (see PropSetupInstance in LobbySpawner.cs), not baked into this asset.
/// That also means the same config can be reused at more than one anchor if
/// you ever want two pool tables.
///
/// Example: a "Bowling Lane" config could have Patterns "Full Rack" (10 pins
/// + ball) and "7-10 Split" (2 pins + ball) — same prefabs, different entry
/// lists.
///
/// Entries reuse PropSpawnConfig.PropEntry (Label/Prefab/Type/Position/
/// Rotation/Scale) rather than a new entry type, so setup props carry the
/// same Grabbable/Throwable Type your single-item props already use.
/// Position/Rotation on each entry are local to whatever Anchor Transform
/// this config ends up paired with in the scene.
/// </summary>
[CreateAssetMenu(fileName = "New Prop Setup Config", menuName = "ChaosPit/Lobby/Prop Setup Config")]
public class PropSetupConfig : ScriptableObject
{
    [Header("Identity")]
    [Tooltip("Used for naming spawned objects and in editor/debug logs, e.g. \"Chess Set\".")]
    public string SetupLabel;

    [Header("Patterns")]
    [Tooltip("Different prop arrangements built from this template. Only one spawns per anchor.")]
    public List<PropSetupPattern> Patterns = new List<PropSetupPattern>();

    [Tooltip("If true, a random Pattern is chosen at spawn time. If false, ActivePatternIndex is used.")]
    public bool RandomizePattern = false;

    [Tooltip("Index into Patterns used when RandomizePattern is false.")]
    public int ActivePatternIndex = 0;
}

[System.Serializable]
public class PropSetupPattern
{
    [Tooltip("Editor-only name for this pattern, e.g. \"Standard Rack\" or \"7-10 Split\".")]
    public string PatternName;

    [Tooltip("Position/Rotation on each entry are relative to whichever Anchor Transform this config is paired with on LobbySpawner.")]
    public PropSpawnConfig.PropEntry[] Entries;
}