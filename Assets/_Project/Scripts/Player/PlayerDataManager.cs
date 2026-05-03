// PlayerDataManager.cs
// Place in: Assets/_Project/Scripts/Player/
// Owns local save/load of player profile.
// Loaded first in Bootstrap. Not networked.

using UnityEngine;
using System.IO;

[System.Serializable]
public class PlayerData
{
    public string displayName = "Player";
    public int level = 1;
    public int careerScore = 0;
    public int gamesPlayed = 0;
    public int wins = 0;
    public int currentStreak = 0;
    // Cosmetic loadout will expand here when CosmeticSystem is built
}

public class PlayerDataManager : SingletonBehaviour<PlayerDataManager>
{
    // Current loaded profile
    public PlayerData Data { get; private set; }

    private string SavePath => Path.Combine(Application.persistentDataPath, "playerdata.json");

    protected override void Awake()
    {
        base.Awake();
        Load();
    }

    public void Load()
    {
        if (File.Exists(SavePath))
        {
            string json = File.ReadAllText(SavePath);
            Data = JsonUtility.FromJson<PlayerData>(json);
            Debug.Log($"[PlayerDataManager] Profile loaded: {Data.displayName}");
        }
        else
        {
            Data = new PlayerData();
            Save();
            Debug.Log("[PlayerDataManager] No save found. Created new profile.");
        }
    }

    public void Save()
    {
        string json = JsonUtility.ToJson(Data, prettyPrint: true);
        File.WriteAllText(SavePath, json);
        Debug.Log("[PlayerDataManager] Profile saved.");
    }

    // Call this after any career stat change (score, wins, etc.)
    public void UpdateCareerStats(int scoreToAdd, bool won)
    {
        Data.careerScore += scoreToAdd;
        Data.gamesPlayed++;
        if (won)
        {
            Data.wins++;
            Data.currentStreak++;
        }
        else
        {
            Data.currentStreak = 0;
        }

        Save();
    }
}