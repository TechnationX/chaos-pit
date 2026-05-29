// RoundResult.cs

public class RoundResult
{
    public PlayerObject Player;
    public int Standing;        // 1 = first, 2 = second, etc.
    public int ScoreAwarded;    // points awarded this round
    public string ResultLabel;  // "Winner", "2nd Place", etc.

    public RoundResult(PlayerObject player, int standing, int scoreAwarded, string resultLabel)
    {
        Player = player;
        Standing = standing;
        ScoreAwarded = scoreAwarded;
        ResultLabel = resultLabel;
    }
}