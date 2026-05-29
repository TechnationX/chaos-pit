// LobbyUIManager.cs

using UnityEngine;
using TMPro;

public class LobbyUIManager : MonoBehaviour
{
    [SerializeField] private TMP_Text joinCodeText;

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
}