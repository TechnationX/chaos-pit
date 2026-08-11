// SettingsData.cs
using UnityEngine;

[System.Serializable]
public class SettingsData
{
    // Audio
    public float masterVolume = 1f;
    public float musicVolume = 1f;
    public float sfxVolume = 1f;
    public string voiceInputDeviceName = "Disabled";  // "Disabled" or matches an entry in Microphone.devices
    public string voiceOutputDeviceName = "Default";  // TODO: VOICE - output device enumeration needs a native audio plugin; Unity has no built-in API for this

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