// MiniGameRegistryEntry.cs

using UnityEngine;

[CreateAssetMenu(fileName = "MiniGameRegistryEntry", menuName = "MiniGames/Mini Game Registry Entry")]
public class MiniGameRegistryEntry : ScriptableObject
{
    [Header("Identity")]
    public string MiniGameId;
    public string MiniGameName;
    public string SceneName;

    [Header("Player Count")]
    public int MinPlayers = 2;
    public int MaxPlayers = 6;

    [Header("Display")]
    [TextArea] public string Description;
    public Sprite Thumbnail;

    [Header("Availability")]
    public bool IsActive = true;
}