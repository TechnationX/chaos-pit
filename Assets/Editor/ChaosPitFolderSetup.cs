// ChaosPitFolderSetup.cs
// Place this file in any folder named Editor/ inside your Assets directory.
// Run via Unity menu: Tools > Chaos Pit > Generate Folder Structure

using UnityEditor;
using UnityEngine;
using System.IO;

public class ChaosPitFolderSetup
{
    [MenuItem("Tools/Chaos Pit/Generate Folder Structure")]
    public static void GenerateFolders()
    {
        string[] folders = new string[]
        {
            // Scenes
            "Assets/_Project/Scenes",
            "Assets/_Project/Scenes/GameRooms",

            // Scripts
            "Assets/_Project/Scripts/Bootstrap",
            "Assets/_Project/Scripts/Session",
            "Assets/_Project/Scripts/Player",
            "Assets/_Project/Scripts/Lobby",
            "Assets/_Project/Scripts/GameRoom",
            "Assets/_Project/Scripts/MiniGames",
            "Assets/_Project/Scripts/Cosmetics",
            "Assets/_Project/Scripts/Scoring",
            "Assets/_Project/Scripts/SceneManagement",
            "Assets/_Project/Scripts/UI",
            "Assets/_Project/Scripts/UI/Screens",
            "Assets/_Project/Scripts/Audio",

            // Prefabs
            "Assets/_Project/Prefabs/Player",
            "Assets/_Project/Prefabs/UI",
            "Assets/_Project/Prefabs/GameRoom",
            "Assets/_Project/Prefabs/MiniGames",

            // Art
            "Assets/_Project/Art/Characters/Meshes",
            "Assets/_Project/Art/Characters/Materials",
            "Assets/_Project/Art/Characters/Textures",
            "Assets/_Project/Art/Environment",
            "Assets/_Project/Art/UI/Icons",
            "Assets/_Project/Art/UI/Fonts",
            "Assets/_Project/Art/UI/Sprites",
            "Assets/_Project/Art/VFX",

            // Audio
            "Assets/_Project/Audio/Music",
            "Assets/_Project/Audio/SFX",

            // Data
            "Assets/_Project/Data/ScriptableObjects",
            "Assets/_Project/Data/SaveData",

            // Animations
            "Assets/_Project/Animations/Player",
            "Assets/_Project/Animations/UI",

            // Third Party
            "Assets/ThirdParty/FishNet",
            "Assets/ThirdParty/UnityRelay",
        };

        int created = 0;
        int skipped = 0;

        foreach (string folder in folders)
        {
            if (!AssetDatabase.IsValidFolder(folder))
            {
                CreateFolderRecursive(folder);
                created++;
            }
            else
            {
                skipped++;
            }
        }

        AssetDatabase.Refresh();

        Debug.Log($"[ChaosPitFolderSetup] Done. Created: {created} folders. Skipped (already exist): {skipped} folders.");
    }

    private static void CreateFolderRecursive(string path)
    {
        // Split path into parts and create each level if missing
        string[] parts = path.Split('/');
        string current = parts[0];

        for (int i = 1; i < parts.Length; i++)
        {
            string next = current + "/" + parts[i];

            if (!AssetDatabase.IsValidFolder(next))
            {
                AssetDatabase.CreateFolder(current, parts[i]);
            }

            current = next;
        }
    }
}