// KillPlane.cs
using UnityEngine;
namespace ChaosPit.Minigames.LastOneStanding
{
    public class KillPlane : MonoBehaviour
    {
        private void OnTriggerEnter(Collider other)
        {
            Debug.Log($"[KillPlane] Trigger entered by: {other.name}, tag: {other.tag}");

            PlayerObject player = other.GetComponent<PlayerObject>();
            Debug.Log($"[KillPlane] PlayerObject found: {player != null}");

            if (player == null) return;

            Debug.Log($"[KillPlane] IsOwner: {player.IsOwner}, PlayerId: {player.PlayerId}");

            Debug.Log($"[KillPlane] Sending kill request for: {player.PlayerId}");
            GameRoomManager.Instance.RequestMinigameAction(
                "los_kill_request", player.PlayerId.ToString());
        }
    }
}