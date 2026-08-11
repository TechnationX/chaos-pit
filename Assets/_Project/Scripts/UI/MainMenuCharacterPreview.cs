// MainMenuCharacterPreview.cs
using Bozo.ModularCharacters;
using System.Collections;
using UnityEngine;

public class MainMenuCharacterPreview : MonoBehaviour
{
    [SerializeField] private OutfitSystem outfitSystem;
    [SerializeField] private OutfitRegistry registry;
    [SerializeField] private float _loadTimeoutSeconds = 2f;

    private bool _loadSucceeded;
    private bool _defaultAlreadyApplied;

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
        StartCoroutine(FallbackToDefaultIfLoadFails());
    }

    private IEnumerator FallbackToDefaultIfLoadFails()
    {
        float elapsed = 0f;
        while (elapsed < _loadTimeoutSeconds)
        {
            if (_loadSucceeded) yield break; // real save loaded successfully — nothing to do
            elapsed += Time.deltaTime;
            yield return null;
        }

        // Timed out with no successful load — save file missing/invalid, apply default
        ApplyDefaultIfNeeded();
    }

    private void OnCharacterLoaded()
    {
        _loadSucceeded = true;

        Debug.Log($"[MainMenuCharacterPreview] OnCharacterLoaded fired at {Time.realtimeSinceStartup:F1}s");

        // Only backfill if nothing has already been staged this session
        // (e.g. player already visited the Character Creator and hit Continue)
        if (LocalPlayerLoadout.Instance.PendingLoadoutData != null) return;

        var slots = CharacterLoadout.BuildFromOutfitSystem(outfitSystem, registry);
        var packed = CharacterLoadout.Pack(slots);
        LocalPlayerLoadout.Instance.SetLoadout(packed);

        Debug.Log($"[MainMenuCharacterPreview] Backfilled LocalPlayerLoadout from saved character — {slots.Count} slots.");
    }

    private void ApplyDefaultIfNeeded()
    {
        if (_defaultAlreadyApplied) return;
        _defaultAlreadyApplied = true;

        var defaultSlots = CharacterLoadout.BuildDefaultLoadout(registry);
        CharacterLoadout.Apply(outfitSystem, defaultSlots, registry);

        if (LocalPlayerLoadout.Instance.PendingLoadoutData == null)
            LocalPlayerLoadout.Instance.SetLoadout(CharacterLoadout.Pack(defaultSlots));

        Debug.Log($"[MainMenuCharacterPreview] No saved character found — applied random default loadout ({defaultSlots.Count} slots).");
    }
}