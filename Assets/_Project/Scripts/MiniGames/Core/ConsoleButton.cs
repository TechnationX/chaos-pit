// ConsoleButton.cs

using UnityEngine;

// One script, an enum picks the behavior per instance — same convention as
// BowlingPanelButton. Place one instance per physical button in the waiting
// room (Select Game / Start / Leave), each wired to the room's GameRoomConsole.
// Plain MonoBehaviour + IInteractable, matching MinigameStation's own
// precedent — no NetworkBehaviour needed, since every action here just calls
// straight into GameRoomManager's existing [ServerRpc] methods the same way
// MinigameStation's panel buttons already do.
public class ConsoleButton : MonoBehaviour, IInteractable
{
    public enum ConsoleAction { SelectGame, Start, Leave }

    [SerializeField] private ConsoleAction _action;
    [SerializeField] private GameRoomConsole _console;

    public string PromptLabel => _action switch
    {
        ConsoleAction.SelectGame => "Select Game",
        ConsoleAction.Start => "Start Game",
        ConsoleAction.Leave => "Leave Room",
        _ => "Interact"
    };

    public void OnInteract(PlayerObject player)
    {
        if (_console == null)
        {
            Debug.LogWarning("[ConsoleButton] No GameRoomConsole assigned.");
            return;
        }

        int stationIndex = _console.StationIndex;

        switch (_action)
        {
            case ConsoleAction.SelectGame:
                string nextGameId = _console.GetNextGameId();
                if (!string.IsNullOrEmpty(nextGameId))
                    GameRoomManager.Instance.SelectGame(stationIndex, nextGameId, player);
                break;

            case ConsoleAction.Start:
                GameRoomManager.Instance.RequestStartCountdown(stationIndex, player);
                break;

            case ConsoleAction.Leave:
                GameRoomManager.Instance.RequestLeave(stationIndex, player);
                break;
        }
    }
}
