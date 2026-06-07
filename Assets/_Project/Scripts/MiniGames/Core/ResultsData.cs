// ResultsData.cs

using System.Collections.Generic;

public class ResultsData
{
    public string SessionId;
    public List<PlayerResultEntry> Entries = new List<PlayerResultEntry>();
}

public class PlayerResultEntry
{
    public string DisplayName;
    public int Standing;
    public string ResultLabel;
    public int PointsEarned;
    public int CareerScore;
    public int CareerLevel;

    public static int CalculateLevel(int careerScore)
    {
        // TODO: BACKEND — replace with full leveling curve when system is built
        return 1 + (careerScore / 100);
    }
}