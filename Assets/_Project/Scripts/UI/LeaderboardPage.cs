// LeaderboardPage.cs

using UnityEngine;

public abstract class LeaderboardPage : MonoBehaviour
{
    public abstract void Populate(int maxEntries);
}