// PlayerAppearance.cs
using System.Collections.Generic;
using Bozo.ModularCharacters;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

public class PlayerAppearance : NetworkBehaviour
{
    [SerializeField] private OutfitRegistry registry;
    [SerializeField] private OutfitSystem outfitSystem;

    private readonly SyncVar<byte[]> _loadoutData = new SyncVar<byte[]>();

    private void Awake()
    {
        _loadoutData.OnChange += OnLoadoutChanged;
    }

    public override void OnStartClient()
    {
        base.OnStartClient();

        if (IsOwner)
        {
            var localSlots = LoadLocalLoadout();
            if (localSlots == null) return;

            CharacterLoadout.Apply(outfitSystem, localSlots, registry);
            SubmitLoadout(CharacterLoadout.Pack(localSlots));
        }
        else if (_loadoutData.Value is { Length: > 0 })
        {
            // Late join: remote player's loadout already replicated on spawn
            ApplyReceivedLoadout(_loadoutData.Value);
        }
    }

    [ServerRpc(RequireOwnership = true)]
    private void SubmitLoadout(byte[] packedData)
    {
        _loadoutData.Value = packedData;
        PlayerProfileManager.Instance.SetLoadout(Owner, packedData);
    }

    private void OnLoadoutChanged(byte[] prev, byte[] next, bool asServer)
    {
        if (asServer) return;
        if (IsOwner) return;
        if (next is not { Length: > 0 }) return;

        ApplyReceivedLoadout(next);
    }

    private void ApplyReceivedLoadout(byte[] packed)
    {
        var slots = CharacterLoadout.Unpack(packed);
        CharacterLoadout.Apply(outfitSystem, slots, registry);
    }

    private List<SlotLoadout> LoadLocalLoadout()
    {
        var packed = LocalPlayerLoadout.Instance?.PendingLoadoutData;
        if (packed == null || packed.Length == 0)
            return CharacterLoadout.BuildDefaultLoadout(registry);

        return CharacterLoadout.Unpack(packed);
    }
}