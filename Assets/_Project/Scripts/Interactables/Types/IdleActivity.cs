// IdleActivity.cs

using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

public class IdleActivity : NetworkBehaviour, IInteractable
{
    public enum ActivityType
    {
        Generic,        // Placeholder for unimplemented activities
        Jukebox,        // Music player — stub for now
        PhotoBooth,     // Camera UI — stub for now
        VendingMachine  // Cosmetic randomizer — stub for now
    }

    [Header("Activity Settings")]
    [SerializeField] private ActivityType _activityType = ActivityType.Generic;
    [SerializeField] private string _promptLabel = "Interact";

    [Header("Networked State")]
    private readonly SyncVar<bool> _inUse = new SyncVar<bool>();
    private readonly SyncVar<string> _currentUserName = new SyncVar<string>();

    public string PromptLabel => _inUse.Value ? $"In use by {_currentUserName.Value}" : _promptLabel;
    public bool InUse => _inUse.Value;

    public void OnInteract(PlayerObject player)
    {
        if (!IsServerInitialized) return;
        if (_inUse.Value) return;

        ServerActivate(player);
    }

    [ServerRpc(RequireOwnership = false)]
    private void ServerActivate(PlayerObject player)
    {
        if (_inUse.Value) return;

        _inUse.Value = true;
        _currentUserName.Value = player.PlayerName;

        ObserversActivate(player, _activityType);
    }

    [Server]
    public void ServerDeactivate()
    {
        _inUse.Value = false;
        _currentUserName.Value = string.Empty;

        ObserversDeactivate();
    }

    [ObserversRpc]
    private void ObserversActivate(PlayerObject player, ActivityType type)
    {
        switch (type)
        {
            case ActivityType.Jukebox:
                HandleJukebox(player);
                break;

            case ActivityType.PhotoBooth:
                HandlePhotoBooth(player);
                break;

            case ActivityType.VendingMachine:
                HandleVendingMachine(player);
                break;

            case ActivityType.Generic:
            default:
                HandleGeneric(player);
                break;
        }
    }

    [ObserversRpc]
    private void ObserversDeactivate()
    {
        // Each type can clean up here when activity ends
    }

    // --- Activity Handlers ---
    // Each is stubbed — fill in when that feature is built

    private void HandleGeneric(PlayerObject player)
    {
        // Generic interact — plays animation, no special logic
        // player.Animator.SetTrigger("Interact"); — hook when Animator is ready
        Debug.Log($"{player.PlayerName} used a generic idle activity");
    }

    private void HandleJukebox(PlayerObject player)
    {
        // TODO: Open jukebox UI, queue song
        // Music system TBD — local preload vs streaming
        Debug.Log($"{player.PlayerName} interacted with jukebox");
        ServerDeactivate(); // Immediately release — UI handles the rest
    }

    private void HandlePhotoBooth(PlayerObject player)
    {
        // TODO: Open photo booth UI, trigger camera effect
        Debug.Log($"{player.PlayerName} entered photo booth");
    }

    private void HandleVendingMachine(PlayerObject player)
    {
        // TODO: Open vending machine UI, randomize cosmetic
        Debug.Log($"{player.PlayerName} used vending machine");
        ServerDeactivate(); // Immediately release — UI handles the rest
    }

    // Called externally when player walks away or activity ends
    public void ReleaseActivity()
    {
        if (IsServerInitialized && _inUse.Value)
            ServerDeactivate();
    }
}