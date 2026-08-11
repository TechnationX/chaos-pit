// ResetCareerStatsButton.cs
using FishNet;
using UnityEngine;
using UnityEngine.UI;

public class ResetCareerStatsButton : MonoBehaviour
{
    [SerializeField] private Button _resetButton;
    [SerializeField] private ConfirmDialog _confirmDialog;

    private void Awake()
    {
        _resetButton.onClick.AddListener(PromptReset);
    }

    private void PromptReset()
    {
        _confirmDialog.Show(
            message: "Reset career stats? This cannot be undone.",
            onConfirm: DoReset
        );
    }

    private void DoReset()
    {
        var localConn = InstanceFinder.ClientManager?.Connection;
        var profileSync = localConn?.FirstObject?.GetComponent<PlayerProfileSync>();
        profileSync?.RequestResetCareerScore();
    }
}