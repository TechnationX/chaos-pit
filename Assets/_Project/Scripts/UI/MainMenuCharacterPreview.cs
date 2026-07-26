// MainMenuCharacterPreview.cs
using Bozo.ModularCharacters;
using UnityEngine;

public class MainMenuCharacterPreview : MonoBehaviour
{
    [SerializeField] private OutfitSystem outfitSystem;
    [SerializeField] private OutfitRegistry registry;

    private void Awake()
    {
        outfitSystem.OnCharacterLoaded += OnCharacterLoaded;
    }

    private void OnDestroy()
    {
        outfitSystem.OnCharacterLoaded -= OnCharacterLoaded;
    }

    private void Start()
    {
        outfitSystem.LoadFromID(LocalTestPlayerId.GetOrClaim());
    }

    private void OnCharacterLoaded()
    {
        // Only backfill if nothing has already been staged this session
        // (e.g. player already visited the Character Creator and hit Continue)
        if (LocalPlayerLoadout.Instance.PendingLoadoutData != null) return;

        var slots = CharacterLoadout.BuildFromOutfitSystem(outfitSystem, registry);
        var packed = CharacterLoadout.Pack(slots);
        LocalPlayerLoadout.Instance.SetLoadout(packed);

        Debug.Log($"[MainMenuCharacterPreview] Backfilled LocalPlayerLoadout from saved character — {slots.Count} slots.");
    }
}