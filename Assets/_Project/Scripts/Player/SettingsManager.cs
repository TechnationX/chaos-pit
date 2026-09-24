// SettingsManager.cs
using System;
using UnityEngine;
using UnityEngine.Audio;

public class SettingsManager : MonoBehaviour
{
    public static SettingsManager Instance { get; private set; }

    [Header("Audio Mixer")]
    [SerializeField] private AudioMixer _masterMixer;
    private const string MixerMasterParam = "MasterVolume";
    private const string MixerMusicParam = "MusicVolume";
    private const string MixerSFXParam = "SFXVolume";

    public SettingsData Current { get; private set; } = new SettingsData();

    // Other systems subscribe to these instead of SettingsManager reaching into them directly.
    public static event Action<float> OnFOVChanged;
    public static event Action<float> OnSensitivityChanged;
    public static event Action<bool> OnInvertYChanged;
    public static event Action<string> OnVoiceInputDeviceChanged;
    public static event Action<string> OnVoiceOutputDeviceChanged;
    public static event Action<bool> OnVoiceChatMutedChanged;
    public static event Action<float> OnVoiceChatVolumeChanged;
    public static event Action<VoiceChatMode> OnVoiceChatModeChanged;

    private const string KeyMaster = "Settings_MasterVolume";
    private const string KeyMusic = "Settings_MusicVolume";
    private const string KeySFX = "Settings_SFXVolume";
    private const string KeyVoiceInputDevice = "Settings_VoiceInputDevice";
    private const string KeyVoiceOutputDevice = "Settings_VoiceOutputDevice";
    private const string KeyVoiceChatMuted = "Settings_VoiceChatMuted";
    private const string KeyVoiceChatVolume = "Settings_VoiceChatVolume";
    private const string KeyVoiceChatMode = "Settings_VoiceChatMode";
    private const string KeyResolutionIndex = "Settings_ResolutionIndex";
    private const string KeyDisplayMode = "Settings_DisplayMode";
    private const string KeyQuality = "Settings_QualityLevel";
    private const string KeyVSync = "Settings_VSync";
    private const string KeyFrameCap = "Settings_FrameRateCap";
    private const string KeyFOV = "Settings_FOV";
    private const string KeySensitivity = "Settings_MouseSensitivity";
    private const string KeyInvertY = "Settings_InvertY";

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        Load();
        ApplyAll();
    }

    private void Load()
    {
        Current.masterVolume = PlayerPrefs.GetFloat(KeyMaster, 1f);
        Current.musicVolume = PlayerPrefs.GetFloat(KeyMusic, 1f);
        Current.sfxVolume = PlayerPrefs.GetFloat(KeySFX, 1f);
        Current.voiceInputDeviceName = PlayerPrefs.GetString(KeyVoiceInputDevice, "Disabled");
        Current.voiceOutputDeviceName = PlayerPrefs.GetString(KeyVoiceOutputDevice, "Default");
        Current.voiceChatMuted = PlayerPrefs.GetInt(KeyVoiceChatMuted, 0) == 1;
        Current.voiceChatVolume = PlayerPrefs.GetFloat(KeyVoiceChatVolume, 0.5f);
        Current.voiceChatMode = (VoiceChatMode)PlayerPrefs.GetInt(KeyVoiceChatMode, (int)VoiceChatMode.PushToTalk);

        Current.resolutionIndex = PlayerPrefs.GetInt(KeyResolutionIndex, -1);
        Current.displayMode = (FullScreenMode)PlayerPrefs.GetInt(KeyDisplayMode, (int)FullScreenMode.FullScreenWindow);
        Current.qualityLevel = PlayerPrefs.GetInt(KeyQuality, QualitySettings.GetQualityLevel());
        Current.vSyncEnabled = PlayerPrefs.GetInt(KeyVSync, 1) == 1;
        Current.frameRateCap = PlayerPrefs.GetInt(KeyFrameCap, 60);
        Current.fieldOfView = PlayerPrefs.GetFloat(KeyFOV, 60f);

        Current.mouseSensitivity = PlayerPrefs.GetFloat(KeySensitivity, 1f);
        Current.invertYAxis = PlayerPrefs.GetInt(KeyInvertY, 0) == 1;
    }

    private void Save()
    {
        PlayerPrefs.SetFloat(KeyMaster, Current.masterVolume);
        PlayerPrefs.SetFloat(KeyMusic, Current.musicVolume);
        PlayerPrefs.SetFloat(KeySFX, Current.sfxVolume);
        PlayerPrefs.SetString(KeyVoiceInputDevice, Current.voiceInputDeviceName);
        PlayerPrefs.SetString(KeyVoiceOutputDevice, Current.voiceOutputDeviceName);
        PlayerPrefs.SetInt(KeyVoiceChatMuted, Current.voiceChatMuted ? 1 : 0);
        PlayerPrefs.SetFloat(KeyVoiceChatVolume, Current.voiceChatVolume);
        PlayerPrefs.SetInt(KeyVoiceChatMode, (int)Current.voiceChatMode);

        PlayerPrefs.SetInt(KeyResolutionIndex, Current.resolutionIndex);
        PlayerPrefs.SetInt(KeyDisplayMode, (int)Current.displayMode);
        PlayerPrefs.SetInt(KeyQuality, Current.qualityLevel);
        PlayerPrefs.SetInt(KeyVSync, Current.vSyncEnabled ? 1 : 0);
        PlayerPrefs.SetInt(KeyFrameCap, Current.frameRateCap);
        PlayerPrefs.SetFloat(KeyFOV, Current.fieldOfView);

        PlayerPrefs.SetFloat(KeySensitivity, Current.mouseSensitivity);
        PlayerPrefs.SetInt(KeyInvertY, Current.invertYAxis ? 1 : 0);

        PlayerPrefs.Save();
    }

    private void ApplyAll()
    {
        ApplyMasterVolume(Current.masterVolume);
        ApplyMusicVolume(Current.musicVolume);
        ApplySFXVolume(Current.sfxVolume);

        ApplyResolutionAndDisplayMode(Current.resolutionIndex, Current.displayMode);
        ApplyQualityLevel(Current.qualityLevel);
        ApplyVSync(Current.vSyncEnabled);
        ApplyFrameRateCap(Current.frameRateCap);
        ApplyFOV(Current.fieldOfView);

        ApplySensitivity(Current.mouseSensitivity);
        ApplyInvertY(Current.invertYAxis);
    }

    // ---------- Audio ----------

    public void SetMasterVolume(float value)
    {
        Current.masterVolume = value;
        ApplyMasterVolume(value);
        Save();
    }

    public void SetMusicVolume(float value)
    {
        Current.musicVolume = value;
        ApplyMusicVolume(value);
        Save();
    }

    public void SetSFXVolume(float value)
    {
        Current.sfxVolume = value;
        ApplySFXVolume(value);
        Save();
    }

    // VoiceChatManager subscribes to all five voice-related events below
    // instead of this class reaching into it directly — same convention as
    // OnFOVChanged/OnSensitivityChanged/OnInvertYChanged above.
    public void SetVoiceInputDevice(string deviceName)
    {
        Current.voiceInputDeviceName = deviceName;
        OnVoiceInputDeviceChanged?.Invoke(deviceName);
        Save();
    }

    public void SetVoiceOutputDevice(string deviceName)
    {
        Current.voiceOutputDeviceName = deviceName;
        OnVoiceOutputDeviceChanged?.Invoke(deviceName);
        Save();
    }

    public void SetVoiceChatMuted(bool muted)
    {
        Current.voiceChatMuted = muted;
        OnVoiceChatMutedChanged?.Invoke(muted);
        Save();
    }

    public void SetVoiceChatVolume(float value)
    {
        Current.voiceChatVolume = value;
        OnVoiceChatVolumeChanged?.Invoke(value);
        Save();
    }

    public void SetVoiceChatMode(VoiceChatMode mode)
    {
        Current.voiceChatMode = mode;
        OnVoiceChatModeChanged?.Invoke(mode);
        Save();
    }

    private void ApplyMasterVolume(float value) => SetMixerVolume(MixerMasterParam, value);
    private void ApplyMusicVolume(float value) => SetMixerVolume(MixerMusicParam, value);
    private void ApplySFXVolume(float value) => SetMixerVolume(MixerSFXParam, value);

    private void SetMixerVolume(string param, float linearValue)
    {
        if (_masterMixer == null) return;
        float dB = Mathf.Log10(Mathf.Max(linearValue, 0.0001f)) * 20f;
        _masterMixer.SetFloat(param, dB);
    }

    // ---------- Video ----------

    public void SetResolutionAndDisplayMode(int resolutionIndex, FullScreenMode mode)
    {
        Current.resolutionIndex = resolutionIndex;
        Current.displayMode = mode;
        ApplyResolutionAndDisplayMode(resolutionIndex, mode);
        Save();
    }

    private void ApplyResolutionAndDisplayMode(int resolutionIndex, FullScreenMode mode)
    {
        Resolution[] resolutions = Screen.resolutions;
        if (resolutionIndex >= 0 && resolutionIndex < resolutions.Length)
        {
            Resolution res = resolutions[resolutionIndex];
            Screen.SetResolution(res.width, res.height, mode, res.refreshRateRatio);
        }
        else
        {
            Screen.fullScreenMode = mode;
        }
    }

    public void SetQualityLevel(int level)
    {
        Current.qualityLevel = level;
        ApplyQualityLevel(level);
        Save();
    }

    private void ApplyQualityLevel(int level) => QualitySettings.SetQualityLevel(level, true);

    public void SetVSync(bool enabled)
    {
        Current.vSyncEnabled = enabled;
        ApplyVSync(enabled);
        // Re-apply frame cap since vSync overrides it
        ApplyFrameRateCap(Current.frameRateCap);
        Save();
    }

    private void ApplyVSync(bool enabled) => QualitySettings.vSyncCount = enabled ? 1 : 0;

    public void SetFrameRateCap(int fps)
    {
        Current.frameRateCap = fps;
        ApplyFrameRateCap(fps);
        Save();
    }

    private void ApplyFrameRateCap(int fps)
    {
        // Only takes effect when vSync is off; vSync governs frame pacing otherwise.
        if (!Current.vSyncEnabled)
            Application.targetFrameRate = fps;
    }

    public void SetFOV(float fov)
    {
        Current.fieldOfView = fov;
        ApplyFOV(fov);
        Save();
    }

    private void ApplyFOV(float fov) => OnFOVChanged?.Invoke(fov);

    // ---------- Controls ----------

    public void SetSensitivity(float value)
    {
        Current.mouseSensitivity = value;
        ApplySensitivity(value);
        Save();
    }

    private void ApplySensitivity(float value) => OnSensitivityChanged?.Invoke(value);

    public void SetInvertY(bool inverted)
    {
        Current.invertYAxis = inverted;
        ApplyInvertY(inverted);
        Save();
    }

    private void ApplyInvertY(bool inverted) => OnInvertYChanged?.Invoke(inverted);
}