// CareerLevelSystem.cs
using System.Collections.Generic;

public static class CareerLevelSystem
{
    private const float BasePointsPerLevel = 100f;
    private const float ScalingExponent = 1.3f;
    private const int MaxCachedLevel = 100; // thresholds beyond this compute on demand

    private static readonly List<int> _cumulativeThresholds = BuildThresholds();

    private static List<int> BuildThresholds()
    {
        var thresholds = new List<int> { 0 }; // index 0 = score needed to reach level 1 (always 0)
        int cumulative = 0;

        for (int level = 1; level <= MaxCachedLevel; level++)
        {
            int requirement = UnityEngine.Mathf.RoundToInt(BasePointsPerLevel * UnityEngine.Mathf.Pow(level, ScalingExponent));
            cumulative += requirement;
            thresholds.Add(cumulative);
        }

        return thresholds;
    }

    /// Returns the player's current level for a given total career score.
    public static int CalculateLevel(int careerScore)
    {
        int level = 1;
        for (int i = 1; i < _cumulativeThresholds.Count; i++)
        {
            if (careerScore < _cumulativeThresholds[i]) break;
            level = i + 1;
        }
        return level;
    }

    /// Returns (currentLevelFloor, nextLevelThreshold) for progress-bar UI.
    public static (int currentFloor, int nextThreshold) GetLevelProgress(int careerScore)
    {
        int level = CalculateLevel(careerScore);
        int currentFloor = level - 1 < _cumulativeThresholds.Count ? _cumulativeThresholds[level - 1] : _cumulativeThresholds[^1];
        int nextThreshold = level < _cumulativeThresholds.Count ? _cumulativeThresholds[level] : currentFloor + 999999;
        return (currentFloor, nextThreshold);
    }
}