// LocalPlayerProfile.cs
using System;
using System.IO;
using UnityEngine;

public class LocalPlayerProfile : MonoBehaviour
{
    public static LocalPlayerProfile Instance { get; private set; }

    public string DisplayName { get; private set; } = "";
    public int CareerScore { get; private set; } = 0;

    private const string FileName = "LocalPlayerProfile.json";
    private string FilePath => Path.Combine(Application.persistentDataPath, FileName);

    [Serializable]
    private class ProfileData
    {
        public string displayName;
        public int careerScore;
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        Load();
    }

    public void SetDisplayName(string newName)
    {
        DisplayName = newName;
        Save();
    }

    public void SetCareerScore(int newScore)
    {
        CareerScore = newScore;
        Save();
    }

    private void Load()
    {
        if (!File.Exists(FilePath)) return;

        try
        {
            string json = File.ReadAllText(FilePath);
            ProfileData data = JsonUtility.FromJson<ProfileData>(json);
            DisplayName = data?.displayName ?? "";
            CareerScore = data?.careerScore ?? 0;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[LocalPlayerProfile] Failed to load profile: {e.Message}");
        }
    }

    private void Save()
    {
        try
        {
            ProfileData data = new ProfileData { displayName = DisplayName, careerScore = CareerScore };
            File.WriteAllText(FilePath, JsonUtility.ToJson(data));
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[LocalPlayerProfile] Failed to save profile: {e.Message}");
        }
    }
}