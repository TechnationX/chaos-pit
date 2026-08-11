// ConfirmDialog.cs
using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class ConfirmDialog : MonoBehaviour
{
    [SerializeField] private GameObject _panelRoot;
    [SerializeField] private TMP_Text _messageLabel;
    [SerializeField] private Button _confirmButton;
    [SerializeField] private Button _cancelButton;

    private Action _onConfirm;

    private void Awake()
    {
        _panelRoot.SetActive(false);
        _confirmButton.onClick.AddListener(HandleConfirm);
        _cancelButton.onClick.AddListener(HandleCancel);
    }

    public void Show(string message, Action onConfirm)
    {
        _messageLabel.text = message;
        _onConfirm = onConfirm;
        _panelRoot.SetActive(true);
    }

    private void HandleConfirm()
    {
        _panelRoot.SetActive(false);
        _onConfirm?.Invoke();
        _onConfirm = null;
    }

    private void HandleCancel()
    {
        _panelRoot.SetActive(false);
        _onConfirm = null;
    }
}