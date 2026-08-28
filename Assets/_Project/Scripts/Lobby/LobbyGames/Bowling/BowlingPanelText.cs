// BowlingPanelText.cs
using TMPro;
using UnityEngine;

/// <summary>
/// Purely visual — keeps a world-space TextMeshPro label on the bowling
/// panel showing "Frames: N" or "Pins: N", live-updated as
/// BowlingGameController's Cycle Frame Count / Cycle Pin Layout buttons are
/// pressed. Polled every frame rather than reacted to via SyncVar OnChange
/// — same convention as Pin.cs's _isStandingSync (OnChange wasn't found
/// reliable on host in this project).
///
/// Not a NetworkBehaviour — it doesn't need to be. It just reads
/// BowlingGameController's public FrameCount/PinCount accessors, which are
/// already backed by SyncVars and correctly replicated to every client on
/// their own; this script only needs to exist locally on whichever client
/// is looking at the panel.
///
/// Add directly to a button's "Text (TMP)" object (self-references its own
/// TextMeshProUGUI) — one instance on FrameCountButton's text with Stat set
/// to FrameCount and Label Prefix "Frames", another on PinLayoutButton's
/// text with Stat set to PatternName and Label Prefix "Layout" (or PinCount
/// with Label Prefix "Pins", if a raw count is preferred over the name).
/// </summary>
public class BowlingPanelText : MonoBehaviour
{
    public enum DisplayStat
    {
        FrameCount,
        PinCount,
        PatternName
    }

    [Tooltip("The lane's BowlingGameController to read the live value from.")]
    [SerializeField] private BowlingGameController _gameController;
    [SerializeField] private DisplayStat _stat = DisplayStat.FrameCount;
    [Tooltip("Text shown before the value, e.g. \"Frames\" -> \"Frames: 10\".")]
    [SerializeField] private string _labelPrefix = "Frames";
    [Tooltip("Leave unassigned to use a TextMeshProUGUI on this same GameObject.")]
    [SerializeField] private TextMeshProUGUI _text;

    // Sentinel so the very first Update() always applies, even if the real
    // value happens to be "0"/empty. Compared as text now (not just int) so
    // the same caching works for PatternName's string value too.
    private string _lastAppliedValue = null;

    private void Awake()
    {
        if (_text == null)
            _text = GetComponent<TextMeshProUGUI>();
    }

    private void Update()
    {
        if (_gameController == null || _text == null) return;

        string value = _stat switch
        {
            DisplayStat.FrameCount => _gameController.FrameCount.ToString(),
            DisplayStat.PinCount => _gameController.PinCount.ToString(),
            DisplayStat.PatternName => _gameController.PatternName,
            _ => ""
        };
        if (value == _lastAppliedValue) return;

        _lastAppliedValue = value;
        _text.text = $"{_labelPrefix}: {value}";
    }
}
