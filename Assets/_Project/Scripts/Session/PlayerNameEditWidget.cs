// PlayerNameEditWidget.cs
using FishNet;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class PlayerNameEditWidget : MonoBehaviour
{
    [SerializeField] private TMP_Text _nameLabel;
    [SerializeField] private TMP_InputField _nameInputField;
    [SerializeField] private Button _editButton;
    [SerializeField] private Button _confirmButton;

    private PlayerProfileSync _localProfileSync;

    private void Awake()
    {
        _nameInputField.gameObject.SetActive(false);
        _editButton.onClick.AddListener(BeginEdit);
        _confirmButton.onClick.AddListener(ConfirmEdit);
    }

    private void OnEnable()
    {
        TryResolveLocalProfileSync();
        if (_localProfileSync != null)
        {
            _localProfileSync.DisplayName.OnChange += HandleDisplayNameChanged;
            _nameLabel.text = _localProfileSync.DisplayName.Value;
        }
    }

    private void OnDisable()
    {
        if (_localProfileSync != null)
            _localProfileSync.DisplayName.OnChange -= HandleDisplayNameChanged;
    }

    private void TryResolveLocalProfileSync()
    {
        var localConn = InstanceFinder.ClientManager?.Connection;
        if (localConn?.FirstObject == null) return;

        _localProfileSync = localConn.FirstObject.GetComponent<PlayerProfileSync>();
    }

    private void BeginEdit()
    {
        if (_localProfileSync == null) return;

        _nameInputField.text = _localProfileSync.DisplayName.Value;
        _nameInputField.gameObject.SetActive(true);
        _nameLabel.gameObject.SetActive(false);
        _nameInputField.Select();
    }

    private void ConfirmEdit()
    {
        if (_localProfileSync == null) return;

        string newName = _nameInputField.text;
        _localProfileSync.RequestSetDisplayName(newName);

        _nameInputField.gameObject.SetActive(false);
        _nameLabel.gameObject.SetActive(true);
    }

    private void HandleDisplayNameChanged(string prev, string next, bool asServer)
    {
        _nameLabel.text = next;
    }
}