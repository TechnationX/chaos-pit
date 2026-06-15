// LobbyUIManager.cs

using UnityEngine;
using TMPro;

public class LobbyUIManager : MonoBehaviour
{
    [SerializeField] private TMP_Text joinCodeText;
    [SerializeField] private TextMeshProUGUI _playerNameText;

    private void Start()
    {
        if (SessionManager.HasInstance && !string.IsNullOrEmpty(SessionManager.Instance.JoinCode))
        {
            joinCodeText.text = $"Code: {SessionManager.Instance.JoinCode}";
        }
        else
        {
            joinCodeText.text = string.Empty;
        }
    }

    public void SetVisible(bool visible)
    {
        gameObject.SetActive(visible);
    }

    public void SetPlayerName(string name)
    {
        if (_playerNameText != null)
            _playerNameText.text = name;
    }
}