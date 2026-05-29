// PlacementConverter.cs

public static class PlacementConverter
{
    // Points awarded by placement
    // TODO: BACKEND — pull these values from a config/database for live balancing
    private static readonly int[] _pointTable = new int[]
    {
        100,  // 1st
        60,   // 2nd
        30,   // 3rd
        10,   // 4th
        5,    // 5th
        2     // 6th
    };

    /// <summary>
    /// Returns points for a given placement (1-based) in a game with playerCount players.
    /// </summary>
    public static int GetPoints(int placement, int playerCount)
    {
        if (placement < 1 || placement > playerCount)
        {
            UnityEngine.Debug.LogWarning($"[PlacementConverter] Invalid placement {placement} for playerCount {playerCount}");
            return 0;
        }

        int index = placement - 1;

        if (index >= _pointTable.Length)
            return _pointTable[_pointTable.Length - 1]; // floor at lowest defined value

        return _pointTable[index];
    }
}