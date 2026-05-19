// ChaosPitUISetup.cs
// Run via Unity menu:
//   Tools > Chaos Pit > Setup Splash Scene
//   Tools > Chaos Pit > Setup Main Menu Scene
// Each menu item generates the scene hierarchy AND creates empty script files.
// Attach scripts and components manually after running.

using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using System.IO;

public class ChaosPitUISetup
{
    // ─── Script Paths ─────────────────────────────────────────────────────────

    private const string UIScriptsPath = "Assets/_Project/Scripts/UI";
    private const string ScreenScriptsPath = "Assets/_Project/Scripts/UI/Screens";

    // ─── Splash Scene ─────────────────────────────────────────────────────────

    [MenuItem("Tools/Chaos Pit/Setup Splash Scene")]
    public static void SetupSplashScene()
    {
        if (!ConfirmScene("Splash")) return;

        // --- Create Scripts ---
        CreateScriptIfMissing(ScreenScriptsPath, "SplashScreen");

        // --- Hierarchy ---

        // Root: Canvas
        // Add Component: Canvas, CanvasScaler, GraphicRaycaster
        // Canvas Settings: Render Mode = Screen Space Overlay
        GameObject canvas = CreateEmpty("Canvas", null);

        // Attach: SplashScreen.cs
        // Role: Controls logo fade in/out, triggers transition to Main Menu
        GameObject splashScreen = CreateEmpty("SplashScreen", canvas.transform);

        // Child: Studio logo image
        // Add Component: Image
        // Assign: Studio logo sprite in Inspector
        CreateEmpty("StudioLogo", splashScreen.transform);

        // Child: Game logo image
        // Add Component: Image
        // Assign: Game logo sprite in Inspector
        CreateEmpty("GameLogo", splashScreen.transform);

        // Root: EventSystem
        // Add Component: EventSystem, StandaloneInputModule
        CreateEmpty("EventSystem", null);

        FinishScene("Splash scene hierarchy generated.");
    }

    // ─── Main Menu Scene ──────────────────────────────────────────────────────

    [MenuItem("Tools/Chaos Pit/Setup Main Menu Scene")]
    public static void SetupMainMenuScene()
    {
        if (!ConfirmScene("MainMenu")) return;

        // --- Create Scripts ---
        CreateScriptIfMissing(UIScriptsPath, "UIScreenBase");
        CreateScriptIfMissing(UIScriptsPath, "MainMenuManager");
        CreateScriptIfMissing(ScreenScriptsPath, "CreateSessionScreen");
        CreateScriptIfMissing(ScreenScriptsPath, "JoinSessionScreen");

        // --- Hierarchy ---

        // Root: MainMenuManager
        // Attach: MainMenuManager.cs
        // Role: Button wiring, screen transitions, loads player name/level
        CreateEmpty("MainMenuManager", null);

        // Root: Canvas
        // Add Component: Canvas, CanvasScaler, GraphicRaycaster
        // Canvas Settings: Render Mode = Screen Space Overlay
        GameObject canvas = CreateEmpty("Canvas", null);

        // --- Screen: MainMenuScreen ---
        // Attach: nothing — controlled by MainMenuManager
        // Add Component: CanvasGroup (for fade transitions)
        GameObject mainMenuScreen = CreateEmpty("MainMenuScreen", canvas.transform);

        // Player info display
        // Add Component: TextMeshProUGUI to each
        GameObject playerInfo = CreateEmpty("PlayerInfo", mainMenuScreen.transform);
        CreateEmpty("PlayerNameText", playerInfo.transform);
        CreateEmpty("PlayerLevelText", playerInfo.transform);

        // Navigation buttons
        // Add Component: Button + TextMeshProUGUI to each
        GameObject buttons = CreateEmpty("Buttons", mainMenuScreen.transform);
        CreateEmpty("CreateSessionButton", buttons.transform);
        CreateEmpty("JoinSessionButton", buttons.transform);
        CreateEmpty("CustomizationButton", buttons.transform);
        CreateEmpty("SettingsButton", buttons.transform);
        CreateEmpty("QuitButton", buttons.transform);

        // --- Screen: CreateSessionScreen ---
        // Attach: CreateSessionScreen.cs
        // Add Component: CanvasGroup
        GameObject createScreen = CreateEmpty("CreateSessionScreen", canvas.transform);

        // Loading state shown while Relay allocates
        GameObject loadingState = CreateEmpty("LoadingState", createScreen.transform);
        CreateEmpty("LoadingText", loadingState.transform);  // TextMeshProUGUI

        // Code state shown once join code is ready
        GameObject codeState = CreateEmpty("CodeState", createScreen.transform);
        CreateEmpty("JoinCodeText", codeState.transform);     // TextMeshProUGUI
        CreateEmpty("CopyCodeButton", codeState.transform);     // Button

        CreateEmpty("BackButton", createScreen.transform);  // Button

        // --- Screen: JoinSessionScreen ---
        // Attach: JoinSessionScreen.cs
        // Add Component: CanvasGroup
        GameObject joinScreen = CreateEmpty("JoinSessionScreen", canvas.transform);

        CreateEmpty("JoinCodeInput", joinScreen.transform);    // TMP_InputField
        CreateEmpty("ConnectButton", joinScreen.transform);    // Button
        CreateEmpty("ErrorText", joinScreen.transform);    // TextMeshProUGUI — hidden by default
        CreateEmpty("BackButton", joinScreen.transform);    // Button

        // --- Screen: CustomizationScreen (stub) ---
        // Attach: nothing yet — CosmeticSystem not built
        // Add Component: CanvasGroup
        CreateEmpty("CustomizationScreen", canvas.transform);

        // --- Screen: SettingsScreen (stub) ---
        // Attach: nothing yet — SettingsManager not built
        // Add Component: CanvasGroup
        CreateEmpty("SettingsScreen", canvas.transform);

        // Root: EventSystem
        // Add Component: EventSystem, StandaloneInputModule
        CreateEmpty("EventSystem", null);

        FinishScene("Main Menu scene hierarchy generated.");
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static bool ConfirmScene(string expectedName)
    {
        string activeScene = EditorSceneManager.GetActiveScene().name;
        if (activeScene == expectedName) return true;

        return EditorUtility.DisplayDialog(
            "Scene Mismatch",
            $"Active scene is '{activeScene}', not '{expectedName}'. Generate hierarchy here anyway?",
            "Yes, proceed",
            "Cancel"
        );
    }

    private static void FinishScene(string message)
    {
        AssetDatabase.Refresh();
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        Debug.Log($"[ChaosPitUISetup] {message} Attach scripts and components manually.");
    }

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

    private static void CreateScriptIfMissing(string folderPath, string scriptName)
    {
        string fullPath = $"{folderPath}/{scriptName}.cs";

        if (File.Exists(fullPath))
        {
            Debug.Log($"[ChaosPitUISetup] Script already exists, skipping: {fullPath}");
            return;
        }

        // Ensure folder exists
        Directory.CreateDirectory(folderPath);

        string content =
$@"// {scriptName}.cs
// Place in: {folderPath}/
// TODO: Implement {scriptName}

using UnityEngine;

public class {scriptName} : MonoBehaviour
{{
    // TODO
}}
";
        File.WriteAllText(fullPath, content);
        Debug.Log($"[ChaosPitUISetup] Created script: {fullPath}");
    }
}