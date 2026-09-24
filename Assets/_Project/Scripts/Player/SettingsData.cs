// SettingsData.cs
using UnityEngine;

// How the local player's mic transmits into voice chat. See VoiceChatManager's
// Update() for where this is actually enforced — this enum just carries the
// user's choice through Settings/PlayerPrefs like every other value below.
public enum VoiceChatMode
{
    AlwaysOn,
    PushToTalk
}

[System.Serializable]
public class SettingsData
{
    // Audio
    public float masterVolume = 1f;
    public float musicVolume = 1f;
    public float sfxVolume = 1f;
    public string voiceInputDeviceName = "Disabled";  // "Disabled" or matches an entry in Microphone.devices
    public string voiceOutputDeviceName = "Default";  // TODO: VOICE - output device enumeration needs a native audio plugin; Unity has no built-in API for this
    public bool voiceChatMuted = false;               // Manual override — always silent while true, regardless of voiceChatMode
    public float voiceChatVolume = 0.5f;               // 0-1 slider value. 0.5 = Vivox's own neutral/unmodified level, not silence — see VoiceChatManager.SetVolume
    public VoiceChatMode voiceChatMode = VoiceChatMode.PushToTalk;

    // Video
    public int resolutionIndex = -1; // -1 = not yet set, resolve to native on first load
    public FullScreenMode displayMode = FullScreenMode.FullScreenWindow;
    public int qualityLevel = 2; // index into QualitySettings.names
    public bool vSyncEnabled = true;
    public int frameRateCap = 60; // ignored if vSync enabled
    public float fieldOfView = 60f;

    // Controls
    public float mouseSensitivity = 1f;
    public bool invertYAxis = false;
}