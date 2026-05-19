// Sittable.cs

using UnityEngine;
using FishNet.Object;

public class Sittable : NetworkBehaviour, IInteractable
{
    [Header("Sittable Settings")]
    [SerializeField] private Transform _sitPoint;
    [SerializeField] private string _promptLabel = "Sit";

    private PlayerObject _occupyingPlayer;
    private bool _isOccupied;

    public string PromptLabel => _isOccupied ? "" : _promptLabel;

    public void OnInteract(PlayerObject player)
    {
        Debug.Log($"Sittable OnInteract called. IsServerInitialized: {IsServerInitialized}, IsOccupied: {_isOccupied}");
        if (!IsServerInitialized) return;

        if (_isOccupied)
        {
            if (_occupyingPlayer == player)
                ServerStand(player.NetworkObject);
            return;
        }

        ServerSit(player.NetworkObject);
    }

    [ServerRpc(RequireOwnership = false)]
    private void ServerSit(NetworkObject playerNetObj)
    {
        PlayerObject player = playerNetObj.GetComponent<PlayerObject>();
        if (player == null || _isOccupied) return;

        _isOccupied = true;
        _occupyingPlayer = player;

        ObserversSit(playerNetObj, _sitPoint.position, _sitPoint.rotation);
    }

    [ServerRpc(RequireOwnership = false)]
    private void ServerStand(NetworkObject playerNetObj)
    {
        PlayerObject player = playerNetObj.GetComponent<PlayerObject>();
        if (player == null || _occupyingPlayer != player) return;

        _isOccupied = false;
        _occupyingPlayer = null;

        ObserversStand(playerNetObj);
    }

    [ObserversRpc]
    private void ObserversSit(NetworkObject playerNetObj, Vector3 position, Quaternion rotation)
    {
        PlayerObject player = playerNetObj.GetComponent<PlayerObject>();
        Debug.Log($"ObserversSit called. Player null: {player == null}");
        if (player == null) return;

        player.Movement.SetMovementLocked(true);
        player.Movement.SetCurrentSeat(this);
        Debug.Log($"SetCurrentSeat called on {player.name}");

        player.transform.position = position;
        player.transform.rotation = rotation;

        if (player.IsOwner)
            player.Camera.SwitchTo(PlayerCamera.CameraMode.ThirdPerson);

        // player.Animator.SetTrigger("Sit"); — hook when Animator ready
    }

    [ObserversRpc]
    private void ObserversStand(NetworkObject playerNetObj)
    {
        PlayerObject player = playerNetObj.GetComponent<PlayerObject>();
        if (player == null) return;

        player.Movement.SetMovementLocked(false);
        player.Movement.SetCurrentSeat(null);

        if (player.IsOwner)
            player.Camera.SwitchTo(PlayerCamera.CameraMode.FirstPerson);

        // player.Animator.SetTrigger("Stand"); — hook when Animator ready
    }

    public void ForceStand(PlayerObject player)
    {
        if (_occupyingPlayer != player) return;
        ServerStand(player.NetworkObject);
    }
}