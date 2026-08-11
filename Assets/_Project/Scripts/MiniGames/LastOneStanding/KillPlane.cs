// KillPlane.cs
using UnityEngine;
namespace ChaosPit.Minigames.LastOneStanding
{
    public class KillPlane : MonoBehaviour
    {
        private void OnTriggerEnter(Collider other)
        {
            // Search up the hierarchy — catches child colliders on the character model
            PlayerObject player = other.GetComponentInParent<PlayerObject>();
            if (player == null) return;

            Debug.Log($"[KillPlane] Found player: {player.name}, ClientId: {player.Owner?.ClientId}, PlayerId: {player.PlayerId}");

            GameRoomManager.Instance.RequestMinigameAction(
                "los_kill_request", player.Owner.ClientId.ToString());
        }
    }
}