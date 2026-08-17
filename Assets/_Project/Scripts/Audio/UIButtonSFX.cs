// UIButtonSFX.cs
using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(Button))]
public class UIButtonSFX : MonoBehaviour
{
    [Tooltip("Leave empty to use AudioManager's default click sound.")]
    [SerializeField] private AudioClip _overrideClip;

    private Button _button;

    private void Awake()
    {
        _button = GetComponent<Button>();
        _button.onClick.AddListener(PlayClick);
    }

    private void OnDestroy()
    {
        if (_button != null)
            _button.onClick.RemoveListener(PlayClick);
    }

    private void PlayClick()
    {
        AudioClip clip = _overrideClip != null ? _overrideClip : AudioManager.Instance?.DefaultClickClip;
        AudioManager.Instance?.PlaySFX(clip);
    }
}