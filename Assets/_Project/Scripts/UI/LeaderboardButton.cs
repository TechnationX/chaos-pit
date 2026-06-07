// LeaderboardButton.cs

using UnityEngine;

public class LeaderboardButton : MonoBehaviour, IInteractable
{
    public enum ButtonAction { Next, Prev }

    [SerializeField] private ButtonAction _action;

    public string PromptLabel => _action == ButtonAction.Next ? "Next Page" : "Prev Page";

    public void OnInteract(PlayerObject player)
    {
        if (_action == ButtonAction.Next)
            LeaderboardManager.Instance.NextPage();
        else
            LeaderboardManager.Instance.PrevPage();
    }
}