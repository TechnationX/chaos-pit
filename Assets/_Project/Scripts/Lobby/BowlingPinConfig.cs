// BowlingPinConfig
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Defines which of the 10 standard pin-deck slots are active for a given
/// pattern. Unlike PoolSetupConfig, the 10 slot positions themselves are
/// generated procedurally by LobbySpawner from a single head-pin anchor +
/// spacing value — a Pattern here is just which slot indices are occupied,
/// using standard bowling numbering:
///
///   Row 1 (front):  0 = pin 1
///   Row 2:          1 = pin 2,  2 = pin 3
///   Row 3:          3 = pin 4,  4 = pin 5,  5 = pin 6
///   Row 4 (back):   6 = pin 7,  7 = pin 8,  8 = pin 9,  9 = pin 10
///
/// Because every pattern shares the same physical slots, switching patterns
/// never despawns/spawns a single pin — see the class comment in Pin.cs.
/// </summary>
[CreateAssetMenu(fileName = "New Bowling Pin Config", menuName = "ChaosPit/Lobby/Bowling Pin Config")]
public class BowlingPinConfig : ScriptableObject
{
    [Header("Identity")]
    public string SetupLabel;

    [Header("Patterns")]
    [Tooltip("Different pin-slot selections (e.g. \"Standard 10\", \"Big Four Split\", \"6-Pin\").")]
    public List<BowlingPinPattern> Patterns = new List<BowlingPinPattern>();

    [Tooltip("If true, a random Pattern is chosen each time a lane re-racks. If false, ActivePatternIndex is used.")]
    public bool RandomizePattern = false;

    [Tooltip("Index into Patterns used when RandomizePattern is false.")]
    public int ActivePatternIndex = 0;
}

[System.Serializable]
public class BowlingPinPattern
{
    public string PatternName;

    [Tooltip("Which of the 10 standard slots (0-9, see BowlingPinConfig's class comment for the index-to-pin map) are occupied. Leave an index out to leave that slot empty.")]
    public List<int> ActiveSlotIndices = new List<int>();
}
