// Sittable.cs 

using UnityEngine;
using FishNet.Object;

public class Sittable : NetworkBehaviour, IInteractable
{
    [Header("Sittable Settings")]
    [SerializeField] private Transform[] _sitPoints;
    [SerializeField] private string _promptLabel = "Sit";

    private PlayerObject[] _occupyingPlayers;
    private bool[] _occupiedSlots;

    private void Awake()
    {
        _occupyingPlayers = new PlayerObject[_sitPoints.Length];
        _occupiedSlots = new bool[_sitPoints.Length];
    }

    public string PromptLabel => AllSlotsFull() ? "" : _promptLabel;

    private bool AllSlotsFull()
    {
        foreach (var slot in _occupiedSlots)
            if (!slot) return false;
        return true;
    }

    private int GetAvailableSlot()
    {
        for (int i = 0; i < _occupiedSlots.Length; i++)
            if (!_occupiedSlots[i]) return i;
        return -1;
    }

    private int GetPlayerSlot(PlayerObject player)
    {
        for (int i = 0; i < _occupyingPlayers.Length; i++)
            if (_occupyingPlayers[i] == player) return i;
        return -1;
    }

    public void OnInteract(PlayerObject player)
    {
        int playerSlot = GetPlayerSlot(player);
        if (playerSlot >= 0)
        {
            ServerStand(player.NetworkObject, playerSlot);
            return;
        }

        int availableSlot = GetAvailableSlot();
        if (availableSlot < 0) return;

        ServerSit(player.NetworkObject, availableSlot);
    }

    [ServerRpc(RequireOwnership = false)]
    private void ServerSit(NetworkObject playerNetObj, int slotIndex)
    {
        PlayerObject player = playerNetObj?.GetComponent<PlayerObject>();
        if (player == null || _occupiedSlots[slotIndex]) return;

        _occupiedSlots[slotIndex] = true;
        _occupyingPlayers[slotIndex] = player;

        ObserversSit(playerNetObj, _sitPoints[slotIndex].position, _sitPoints[slotIndex].rotation, slotIndex);
    }

    [ServerRpc(RequireOwnership = false)]
    private void ServerStand(NetworkObject playerNetObj, int slotIndex)
    {
        PlayerObject player = playerNetObj?.GetComponent<PlayerObject>();
        if (player == null || _occupyingPlayers[slotIndex] != player) return;

        _occupiedSlots[slotIndex] = false;
        _occupyingPlayers[slotIndex] = null;

        ObserversStand(playerNetObj, slotIndex);
    }

    [ObserversRpc]
    private void ObserversSit(NetworkObject playerNetObj, Vector3 position, Quaternion rotation, int slotIndex)
    {
        PlayerObject player = playerNetObj?.GetComponent<PlayerObject>();
        if (player == null) return;

        // Sync local state so GetPlayerSlot works on all clients
        _occupiedSlots[slotIndex] = true;
        _occupyingPlayers[slotIndex] = player;

        if (player.CharacterAnimator != null)
            player.CharacterAnimator.SetBool("IsSitting", true);

        if (player.Movement != null)
        {
            player.Movement.SetMovementLocked(true, "sitting");
            player.Movement.SetCurrentSeat(this);
        }

        player.transform.position = position;
        player.transform.rotation = rotation;

        if (player.IsOwner && player.Camera != null)
            player.Camera.SwitchTo(PlayerCamera.CameraMode.ThirdPerson);
    }

    [ObserversRpc]
    private void ObserversStand(NetworkObject playerNetObj, int slotIndex)
    {
        PlayerObject player = playerNetObj?.GetComponent<PlayerObject>();
        if (player == null) return;

        // Sync local state
        _occupiedSlots[slotIndex] = false;
        _occupyingPlayers[slotIndex] = null;

        if (player.CharacterAnimator != null)
            player.CharacterAnimator.SetBool("IsSitting", false);

        if (player.Movement != null)
        {
            player.Movement.SetMovementLocked(false, "sitting");
            player.Movement.SetCurrentSeat(null);
        }

        if (player.IsOwner && player.Camera != null)
            player.Camera.SwitchTo(PlayerCamera.CameraMode.FirstPerson);
    }

    public void ForceStand(PlayerObject player)
    {
        int slot = GetPlayerSlot(player);
        if (slot < 0) return;
        ServerStand(player.NetworkObject, slot);
    }
}