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
        var pending = LocalPlayerLoadout.Instance?.PendingLoadoutData;
        var slots = (pending != null && pending.Length > 0)
            ? CharacterLoadout.Unpack(pending)
            : CharacterLoadout.BuildDefaultLoadout(registry);

        CharacterLoadout.Apply(outfitSystem, slots, registry);
        Debug.Log($"[CharacterCreatorContinueHandler] No saved character found — applied {(pending != null ? "staged" : "random default")} loadout ({slots.Count} slots).");
    }

    // Hook this to the Continue button's OnClick
    public void OnContinuePressed()
    {
        SafeSaveById(LocalTestPlayerId.GetOrClaim());

        var slots = CharacterLoadout.BuildFromOutfitSystem(outfitSystem, registry);
        var packed = CharacterLoadout.Pack(slots);

        LocalPlayerLoadout.Instance.SetLoadout(packed);

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