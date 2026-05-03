// ChaosPitSceneSetup.cs
// Place in: Assets/Editor/
// Run via Unity menu: Tools > Chaos Pit > Setup Bootstrap Scene
// Opens or assumes Bootstrap scene is active. Generates full hierarchy.
// Attach scripts and components manually after running.

using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public class ChaosPitSceneSetup
{
    [MenuItem("Tools/Chaos Pit/Setup Bootstrap Scene")]
    public static void SetupBootstrapScene()
    {
        // Warn if active scene is not Bootstrap
        string activeScene = EditorSceneManager.GetActiveScene().name;
        if (activeScene != "Bootstrap")
        {
            bool proceed = EditorUtility.DisplayDialog(
                "Scene Mismatch",
                $"Active scene is '{activeScene}', not 'Bootstrap'. Generate hierarchy here anyway?",
                "Yes, proceed",
                "Cancel"
            );
            if (!proceed) return;
        }

        // --- Root: _Managers ---
        GameObject managers = CreateEmpty("_Managers", null);

        // Persistent singletons
        CreateEmpty("BootstrapManager", managers.transform);
        CreateEmpty("PlayerDataManager", managers.transform);
        CreateEmpty("AudioManager", managers.transform);
        CreateEmpty("NetworkManager", managers.transform); // FishNet component goes here

        // --- Root: _Systems (placeholder for future Bootstrap-level systems) ---
        GameObject systems = CreateEmpty("_Systems", null);
        CreateEmpty("SettingsManager", systems.transform); // future

        // --- Root: _Audio ---
        GameObject audio = CreateEmpty("_Audio", null);
        CreateEmpty("MusicSource", audio.transform);   // AudioSource component goes here
        CreateEmpty("SFXSource", audio.transform);   // AudioSource component goes here

        // Mark scene dirty so Unity prompts to save
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

        Debug.Log("[ChaosPitSceneSetup] Bootstrap hierarchy generated. Attach scripts and components manually.");
    }

    // Helper: create an empty GameObject, optionally parented
    private static GameObject CreateEmpty(string name, Transform parent)
    {
        GameObject go = new GameObject(name);
        if (parent != null)
        {
            go.transform.SetParent(parent);
        }
        go.transform.localPosition = Vector3.zero;
        return go;
    }
}