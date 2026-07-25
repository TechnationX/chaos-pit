// CharacterCreatorContinueHandler.cs
using Bozo.ModularCharacters;
using UnityEngine;
using UnityEngine.SceneManagement;

public class CharacterCreatorContinueHandler : MonoBehaviour
{
    [SerializeField] private OutfitSystem outfitSystem;
    [SerializeField] private OutfitRegistry registry;
    [SerializeField] private string mainMenuSceneName = "MainMenu";

    // Hook this to the Continue button's OnClick
    public void OnContinuePressed()
    {
        var slots = CharacterLoadout.BuildFromOutfitSystem(outfitSystem, registry);
        var packed = CharacterLoadout.Pack(slots);

        LocalPlayerLoadout.Instance.SetLoadout(packed);

        SceneManager.LoadScene(mainMenuSceneName);
    }
}