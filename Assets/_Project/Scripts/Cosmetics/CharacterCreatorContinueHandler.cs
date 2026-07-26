// CharacterCreatorContinueHandler.cs
using Bozo.ModularCharacters;
using UnityEngine;
using UnityEngine.SceneManagement;

public class CharacterCreatorContinueHandler : MonoBehaviour
{
    [SerializeField] private OutfitSystem outfitSystem;
    [SerializeField] private OutfitRegistry registry;
    [SerializeField] private string mainMenuSceneName = "MainMenu";

    private void Start()
    {
        outfitSystem.LoadFromID(LocalTestPlayerId.GetOrClaim());
    }

    // Hook this to the Continue button's OnClick
    public void OnContinuePressed()
    {
        SafeSaveById(LocalTestPlayerId.GetOrClaim());

        var slots = CharacterLoadout.BuildFromOutfitSystem(outfitSystem, registry);
        //Debug.Log($"[CharacterCreator] Built loadout with {slots.Count} slots.");
        var packed = CharacterLoadout.Pack(slots);
        //Debug.Log($"[CharacterCreator] Packed loadout size: {packed.Length} bytes.");

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