// ThiefsMarketItemVisual.cs

using UnityEngine;

/// Non-networked visual/trigger for a single Thief's Market item.
/// Server owns all state (Available/Held/Dropped) via ThiefsMarketController.
/// This component only handles local trigger detection and visual placement —
/// it never decides pickup on its own, it just asks the server.
[RequireComponent(typeof(Collider))]
public class ThiefsMarketItemVisual : MonoBehaviour
{
    [SerializeField] private int _pointValue = 1;
    public int PointValue => _pointValue;
    private int _itemId = -1;
    private Vector3 _originalPosition;

    public int ItemId => _itemId;

    private void Awake()
    {
        _originalPosition = transform.position;
    }

    public void SetId(int id)
    {
        _itemId = id;
    }

    public void SetVisible(bool visible)
    {
        gameObject.SetActive(visible);
    }

    public void SetPosition(Vector3 position)
    {
        transform.position = position;
    }

    public void ResetToOriginal()
    {
        transform.position = _originalPosition;
    }

    private void OnTriggerEnter(Collider other)
    {
        PlayerObject player = other.GetComponentInParent<PlayerObject>();
        if (player == null || !player.IsOwner) return;

        GameRoomManager.Instance.RequestMinigameAction("tm_pickup_request", _itemId.ToString());
    }
}