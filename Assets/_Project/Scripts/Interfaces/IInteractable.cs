// IInteractable.cs

public interface IInteractable
{
    string PromptLabel { get; }
    void OnInteract(PlayerObject player);
}