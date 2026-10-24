// CharacterCreatorContinueHandler.cs
using Bozo.ModularCharacters;
using UnityEngine;
using UnityEngine.SceneManagement;
using System.Collections;

public class CharacterCreatorContinueHandler : MonoBehaviour
{
    [SerializeField] private OutfitSystem outfitSystem;
    [SerializeField] private OutfitRegistry registry;
    [SerializeField] private string mainMenuSceneName = "MainMenu";
    [SerializeField] private float _loadTimeoutSeconds = 2f;

    private bool _loadSucceeded;
    private bool _fallbackApplied;

    private void OnEnable()
    {
        outfitSystem.OnCharacterLoaded += OnCharacterLoaded;
    }

    private void OnDisable()
    {
        outfitSystem.OnCharacterLoaded -= OnCharacterLoaded;
    }

    private void Start()
    {
        outfitSystem.LoadFromID(LocalTestPlayerId.GetOrClaim());
        StartCoroutine(FallbackIfLoadFails());
    }

    private void OnCharacterLoaded()
    {
        _loadSucceeded = true;
    }

    private IEnumerator FallbackIfLoadFails()
    {
        float elapsed = 0f;
        while (elapsed < _loadTimeoutSeconds)
        {
            if (_loadSucceeded) yield break;
            elapsed += Time.deltaTime;
            yield return null;
        }

        if (_fallbackApplied) yield break;
        _fallbackApplied = true;

        // Prefer whatever's already staged (e.g. the default Main Menu just applied),
        // so appearance stays consistent rather than rolling a second independent random result.
        // Staged bytes may be either format now (see CharacterLoadout.ApplyPacked).
        var pending = LocalPlayerLoadout.Instance?.PendingLoadoutData;
        if (pending != null && pending.Length > 0)
            _ = CharacterLoadout.ApplyPacked(outfitSystem, pending, registry);
        else
            _ = CharacterLoadout.Apply(outfitSystem, CharacterLoadout.BuildDefaultLoadout(registry), registry);

        Debug.Log($"[CharacterCreatorContinueHandler] No saved character found — applied {(pending != null ? "staged" : "random default")} loadout.");
    }

    // Hook this to the Continue button's OnClick
    public void OnContinuePressed()
    {
        SafeSaveById(LocalTestPlayerId.GetOrClaim());

        // Full appearance (sliders, proportions, patterns, decals...), not just the
        // registry outfit pack, so other players see exactly what this screen shows.
        var packed = CharacterLoadout.PackFullAppearance(outfitSystem);

        LocalPlayerLoadout.Instance.SetLoadout(packed);

        // Opened from the lobby's CustomizationStation (loaded on top of the Lobby):
        // hand the result back instead of leaving for the Main Menu.
        if (LobbyCustomizationSession.IsActive)
        {
            LobbyCustomizationSession.Complete(packed);
            return;
        }

        SceneManager.LoadScene(mainMenuSceneName);
    }

    private void SafeSaveById(string id)
    {
#if UNITY_EDITOR
        var settings = CharacterToolSettingsProvider.Get();
        var assetPath = $"{settings.saveDataFolder}/{id}.asset";
        if (UnityEditor.AssetDatabase.LoadAssetAtPath<CharacterObject>(assetPath) != null)
        {
            UnityEditor.AssetDatabase.DeleteAsset(assetPath);
        }
#endif
        outfitSystem.SaveByID(id);
    }
}