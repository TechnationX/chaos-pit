// JinxedKillPlane.cs
// Detects players falling off the arena and sends a kill request to the server.
// Place a trigger collider below the arena in the Jinxed scene.

using UnityEngine;

namespace ChaosPit.Minigames.Jinxed
{
    public class JinxedKillPlane : MonoBehaviour
    {
        private void OnTriggerEnter(Collider other)
        {
            PlayerObject player = other.GetComponent<PlayerObject>();
            if (player == null) return;

            //Debug.Log($"[JinxedKillPlane] Triggered by: {player.PlayerId}");
            GameRoomManager.Instance.RequestMinigameAction(
                "jinxed_kill_request", player.PlayerId.ToString());
        }
    }
}