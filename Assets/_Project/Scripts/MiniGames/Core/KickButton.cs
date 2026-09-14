// KickButton.cs

using UnityEngine;

// Physical, walk-up-and-click interactable — same IInteractable pattern as
// ConsoleButton/MinigameStation. GameRoomConsole spawns one of these next to
// every non-host player's name in the room's player list and wires it via
// Setup() before it's ever interacted with. Plain MonoBehaviour, no
// NetworkBehaviour needed — it only ever calls into GameRoomManager's
// existing [ServerRpc] RequestKickPlayer, which re-validates host authority
// server-side regardless of who can physically reach this button.
public class KickButton : MonoBehaviour, IInteractable
{
    private int _stationIndex;
    private int _targetClientId;

    public string PromptLabel => "Kick Player";

    public void Setup(int stationIndex, int targetClientId)
    {
        _stationIndex = stationIndex;
        _targetClientId = targetClientId;
    }

    public void OnInteract(PlayerObject player)
    {
        GameRoomManager.Instance.RequestKickPlayer(_stationIndex, _targetClientId, player);
    }
}
