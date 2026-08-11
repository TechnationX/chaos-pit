// MainMenuNameEditWidget.cs
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class MainMenuNameEditWidget : MonoBehaviour
{
    [SerializeField] private TMP_Text _nameLabel;
    [SerializeField] private TMP_InputField _nameInputField;
    [SerializeField] private Button _editButton;
    [SerializeField] private Button _confirmButton;

    private const int MaxNameLength = 20;

    private void Awake()
    {
        _nameInputField.gameObject.SetActive(false);
        _confirmButton.gameObject.SetActive(false);
        _editButton.onClick.AddListener(BeginEdit);
        _confirmButton.onClick.AddListener(ConfirmEdit);
    }

    private void OnEnable()
    {
        RefreshLabel();
    }

    private void RefreshLabel()
    {
        string current = LocalPlayerProfile.Instance != null ? LocalPlayerProfile.Instance.DisplayName : "";
        _nameLabel.text = string.IsNullOrEmpty(current) ? "Player Name" : current;
    }

    private void BeginEdit()
    {
        _nameInputField.text = LocalPlayerProfile.Instance.DisplayName;
        _nameInputField.gameObject.SetActive(true);
        _nameLabel.gameObject.SetActive(false);

        _editButton.gameObject.SetActive(false);
        _confirmButton.gameObject.SetActive(true);

        _nameInputField.Select();
    }

    private void ConfirmEdit()
    {
        string newName = SanitizeName(_nameInputField.text);
        if (!string.IsNullOrEmpty(newName))
            LocalPlayerProfile.Instance.SetDisplayName(newName);

        _nameInputField.gameObject.SetActive(false);
        _nameLabel.gameObject.SetActive(true);

        _confirmButton.gameObject.SetActive(false);
        _editButton.gameObject.SetActive(true);

        RefreshLabel();
    }

    private string SanitizeName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim();
        if (raw.Length > MaxNameLength) raw = raw.Substring(0, MaxNameLength);
        return raw;
    }
}
