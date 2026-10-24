// PrivateRoomPanel.cs
using TMPro;
using UnityEngine;

// Physical access panel for a PrivateRoom. Put one outside the sealed door (Enter)
// and one inside (Exit). Not networked itself: it only forwards the press to its
// PrivateRoom, which owns the state and does the server-side checks.
//
// Needs a Collider on the interactable layer, same as every other IInteractable prop.
public class PrivateRoomPanel : MonoBehaviour, IInteractable
{
    public enum PanelRole
    {
        Enter,
        Exit
    }

    [SerializeField] private PrivateRoom _room;
    [SerializeField] private PanelRole _role = PanelRole.Enter;

    [Header("Status Display (optional)")]
    [Tooltip("Text on the console that shows whether the room is free, e.g. the console's GameText.")]
    [SerializeField] private TMP_Text _statusText;
    [SerializeField] private string _openText = "Open";
    [SerializeField] private string _occupiedText = "Occupied";
    [SerializeField] private Color _openColor = Color.white;
    [SerializeField] private Color _occupiedColor = new Color(1f, 0.35f, 0.35f);

    private bool _hasShownState;
    private bool _shownOccupied;

    public string PromptLabel => _room != null ? _room.GetPromptFor(_role) : "";

    // Polls the room's synced occupancy rather than subscribing to it: works the
    // same for late joiners and host/client, and is a single bool compare per frame.
    // Only touches the text when the state actually changes.
    private void Update()
    {
        if (_statusText == null || _room == null) return;

        bool occupied = _room.IsOccupied;
        if (_hasShownState && occupied == _shownOccupied) return;

        _hasShownState = true;
        _shownOccupied = occupied;
        _statusText.text = occupied ? _occupiedText : _openText;
        _statusText.color = occupied ? _occupiedColor : _openColor;
    }

    public void OnInteract(PlayerObject player)
    {
        if (_room == null)
        {
            Debug.LogWarning($"[PrivateRoomPanel] {name} has no PrivateRoom assigned.");
            return;
        }

        if (_role == PanelRole.Enter) _room.RequestEnter(player);
        else _room.RequestExit(player);
    }
}
