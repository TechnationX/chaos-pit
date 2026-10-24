// SettingsPanelController.cs
using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using Unity.Services.Vivox;
using UnityEngine;
using UnityEngine.UI;

public class SettingsPanelController : MonoBehaviour
{
    [Header("Audio")]
    [SerializeField] private Slider _masterVolumeSlider;
    [SerializeField] private Slider _musicVolumeSlider;
    [SerializeField] private Slider _sfxVolumeSlider;
    [SerializeField] private TMP_Dropdown _voiceInputDeviceDropdown;
    [SerializeField] private TMP_Dropdown _voiceOutputDeviceDropdown;

    [Header("Video")]
    [SerializeField] private TMP_Dropdown _resolutionDropdown;
    [SerializeField] private TMP_Dropdown _displayModeDropdown;
    [SerializeField] private TMP_Dropdown _qualityDropdown;
    [SerializeField] private Toggle _vSyncToggle;
    [SerializeField] private TMP_Dropdown _frameRateCapDropdown;
    [SerializeField] private Slider _fovSlider;
    [SerializeField] private TMP_Text _fovValueLabel; // optional, shows "60" next to slider

    [Header("Controls")]
    [SerializeField] private Slider _sensitivitySlider;
    [SerializeField] private TMP_Text _sensitivityValueLabel; // optional
    [SerializeField] private Toggle _invertYToggle;
    [SerializeField] private TMP_Dropdown _lobbyViewModeDropdown; // optional, null-guarded
    [SerializeField] private Button _keybindMenuButton;
    [SerializeField] private ConfirmDialog _confirmDialog; // reused for "coming soon" message

    private List<Resolution> _availableResolutions;
    private List<string> _inputDeviceOptions;
    private List<string> _outputDeviceOptions = new List<string> { "Default" }; // rebuilt from VivoxService.Instance.AvailableOutputDevices every time the panel opens — see BuildVoiceDeviceOptions
    private readonly int[] _frameRateCapOptions = { 30, 60, 90, 120, 144, 0 }; // 0 = Unlimited

    // Vivox's native output-device enumeration always injects these three
    // synthetic pseudo-devices ahead of real hardware — they're Vivox's own
    // fixed (non-localized) labels, not something this project named, and
    // they're meaningless to a player picking a physical output device:
    // "Default System Device" / "Default Communication Device" mirror
    // Windows' own default-device roles (which our own "Default" entry
    // above already covers conceptually), and "No Device" silences output
    // entirely, which push-to-talk/mute already covers. Filtered out below
    // rather than shown and selectable.
    private static readonly HashSet<string> _excludedOutputDeviceNames = new HashSet<string>
    {
        "Default System Device",
        "Default Communication Device",
        "No Device",
    };
    private bool _isInitializing;

    private void OnEnable()
    {
        // Rebuild the voice device lists (not just once in Awake) because
        // Vivox's own device list (VivoxService.Instance.AvailableOutputDevices)
        // may not be populated yet at scene load. By the time the player
        // actually opens Settings, login has usually had time to finish, so
        // re-querying on every open keeps the list current without needing to
        // hook AvailableOutputDevicesChanged. _isInitializing is set first so
        // the ClearOptions/AddOptions calls inside can't fire a spurious
        // SetVoiceOutputDevice/SetVoiceInputDevice call before the real saved
        // value is restored below.
        _isInitializing = true;
        BuildVoiceDeviceOptions();
        RefreshFromCurrentSettings();
    }

    private void Awake()
    {
        BuildResolutionOptions();
        BuildDisplayModeOptions();
        BuildQualityOptions();
        BuildFrameRateCapOptions();
        BuildVoiceDeviceOptions();
        BuildLobbyViewModeOptions();

        _masterVolumeSlider.onValueChanged.AddListener(HandleMasterVolumeChanged);
        _musicVolumeSlider.onValueChanged.AddListener(HandleMusicVolumeChanged);
        _sfxVolumeSlider.onValueChanged.AddListener(HandleSFXVolumeChanged);
        _voiceInputDeviceDropdown.onValueChanged.AddListener(HandleVoiceInputDeviceChanged);
        _voiceOutputDeviceDropdown.onValueChanged.AddListener(HandleVoiceOutputDeviceChanged);

        _resolutionDropdown.onValueChanged.AddListener(HandleResolutionOrDisplayModeChanged);
        _displayModeDropdown.onValueChanged.AddListener(HandleResolutionOrDisplayModeChanged);
        _qualityDropdown.onValueChanged.AddListener(HandleQualityChanged);
        _vSyncToggle.onValueChanged.AddListener(HandleVSyncChanged);
        _frameRateCapDropdown.onValueChanged.AddListener(HandleFrameRateCapChanged);
        _fovSlider.onValueChanged.AddListener(HandleFOVChanged);

        _sensitivitySlider.onValueChanged.AddListener(HandleSensitivityChanged);
        _invertYToggle.onValueChanged.AddListener(HandleInvertYChanged);
        if (_lobbyViewModeDropdown != null)
            _lobbyViewModeDropdown.onValueChanged.AddListener(HandleLobbyViewModeChanged);
        _keybindMenuButton.onClick.AddListener(HandleKeybindMenuClicked);
    }

    // ---------- Populate dropdown option lists (once) ----------

    private void BuildResolutionOptions()
    {
        _availableResolutions = Screen.resolutions
            .GroupBy(r => new { r.width, r.height })
            .Select(g => g.Last()) // highest refresh rate per unique width/height
            .OrderBy(r => r.width * r.height)
            .ToList();

        _resolutionDropdown.ClearOptions();
        _resolutionDropdown.AddOptions(
            _availableResolutions.Select(r => $"{r.width} x {r.height}").ToList());
    }

    private void BuildDisplayModeOptions()
    {
        _displayModeDropdown.ClearOptions();
        _displayModeDropdown.AddOptions(new List<string> { "Fullscreen", "Windowed", "Borderless" });
    }

    private void BuildQualityOptions()
    {
        _qualityDropdown.ClearOptions();
        _qualityDropdown.AddOptions(QualitySettings.names.ToList());
    }

    private void BuildFrameRateCapOptions()
    {
        _frameRateCapDropdown.ClearOptions();
        _frameRateCapDropdown.AddOptions(
            _frameRateCapOptions.Select(f => f == 0 ? "Unlimited" : f.ToString()).ToList());
    }

    // Option order must match the LobbyViewMode enum, since the dropdown index
    // is cast straight to it in HandleLobbyViewModeChanged.
    private void BuildLobbyViewModeOptions()
    {
        if (_lobbyViewModeDropdown == null) return;
        _lobbyViewModeDropdown.ClearOptions();
        _lobbyViewModeDropdown.AddOptions(new List<string> { "First Person", "Third Person" });
    }

    private void BuildVoiceDeviceOptions()
    {
        // "Default" lets Vivox pick the OS default mic/output — same sentinel
        // for both lists below. Muting/push-to-talk are the actual way to go
        // silent now (see VoiceChatManager), so there's no separate "Disabled"
        // device entry anymore.
        _inputDeviceOptions = new List<string> { "Default" };
        _inputDeviceOptions.AddRange(Microphone.devices);

        _voiceInputDeviceDropdown.ClearOptions();
        _voiceInputDeviceDropdown.AddOptions(_inputDeviceOptions);

        // Mirrors VoiceChatManager.SetOutputDevice's own lookup — pulling from
        // the same VivoxService.Instance.AvailableOutputDevices list it
        // already matches against by DeviceName, instead of the old hardcoded
        // single-"Default"-entry stub that left this dropdown permanently
        // disabled. VoiceChatManager.EnsureVivoxInitializedAsync now kicks
        // off Vivox SDK init from its own Awake() at game boot (not gated on
        // a player ever spawning), specifically so this list is already
        // populated the first time the player opens Settings from the Main
        // Menu. The try/catch below only matters for the brief window right
        // at launch before that init has actually finished — falls back to
        // just "Default" rather than throwing; OnEnable re-runs this every
        // time the panel opens, so reopening Settings a moment later picks
        // up the real list.
        _outputDeviceOptions = new List<string> { "Default" };
        try
        {
            if (VivoxService.Instance != null)
            {
                foreach (var device in VivoxService.Instance.AvailableOutputDevices)
                {
                    if (!_excludedOutputDeviceNames.Contains(device.DeviceName))
                        _outputDeviceOptions.Add(device.DeviceName);
                }
            }
        }
        catch (Exception)
        {
            // Not yet initialized — "Default" only, until the next reopen.
        }

        _voiceOutputDeviceDropdown.ClearOptions();
        _voiceOutputDeviceDropdown.AddOptions(_outputDeviceOptions);
        _voiceOutputDeviceDropdown.interactable = true;
    }

    // ---------- Sync UI to current settings (on open) ----------

    private void RefreshFromCurrentSettings()
    {
        if (SettingsManager.Instance == null) return;

        _isInitializing = true;
        SettingsData s = SettingsManager.Instance.Current;

        _masterVolumeSlider.value = s.masterVolume;
        _musicVolumeSlider.value = s.musicVolume;
        _sfxVolumeSlider.value = s.sfxVolume;

        int inputIndex = _inputDeviceOptions.IndexOf(s.voiceInputDeviceName);
        _voiceInputDeviceDropdown.value = inputIndex >= 0 ? inputIndex : 0;

        int outputIndex = _outputDeviceOptions.IndexOf(s.voiceOutputDeviceName);
        _voiceOutputDeviceDropdown.value = outputIndex >= 0 ? outputIndex : 0;

        int resIndex = s.resolutionIndex >= 0 && s.resolutionIndex < _availableResolutions.Count
            ? MapScreenResolutionIndexToDropdownIndex(s.resolutionIndex)
            : GetCurrentResolutionDropdownIndex();
        _resolutionDropdown.value = resIndex;

        _displayModeDropdown.value = DisplayModeToDropdownIndex(s.displayMode);
        _qualityDropdown.value = Mathf.Clamp(s.qualityLevel, 0, QualitySettings.names.Length - 1);
        _vSyncToggle.isOn = s.vSyncEnabled;

        int frameCapIndex = System.Array.IndexOf(_frameRateCapOptions, s.frameRateCap);
        _frameRateCapDropdown.value = frameCapIndex >= 0 ? frameCapIndex : 1; // default to 60
        _frameRateCapDropdown.interactable = !s.vSyncEnabled;

        _fovSlider.value = s.fieldOfView;
        if (_fovValueLabel != null) _fovValueLabel.text = Mathf.RoundToInt(s.fieldOfView).ToString();

        _sensitivitySlider.value = s.mouseSensitivity;
        if (_sensitivityValueLabel != null) _sensitivityValueLabel.text = s.mouseSensitivity.ToString("F2");
        _invertYToggle.isOn = s.invertYAxis;
        if (_lobbyViewModeDropdown != null)
            _lobbyViewModeDropdown.value = (int)s.lobbyViewMode;

        _isInitializing = false;
    }

    // ---------- Helpers ----------

    private int MapScreenResolutionIndexToDropdownIndex(int screenResolutionIndex)
    {
        // SettingsManager stores the raw Screen.resolutions index; find matching entry in our deduped list
        Resolution target = Screen.resolutions[screenResolutionIndex];
        int idx = _availableResolutions.FindIndex(r => r.width == target.width && r.height == target.height);
        return idx >= 0 ? idx : GetCurrentResolutionDropdownIndex();
    }

    private int GetCurrentResolutionDropdownIndex()
    {
        int idx = _availableResolutions.FindIndex(r =>
            r.width == Screen.currentResolution.width && r.height == Screen.currentResolution.height);
        return idx >= 0 ? idx : _availableResolutions.Count - 1;
    }

    private int DisplayModeToDropdownIndex(FullScreenMode mode) => mode switch
    {
        FullScreenMode.ExclusiveFullScreen => 0,
        FullScreenMode.Windowed => 1,
        FullScreenMode.FullScreenWindow => 2,
        _ => 2
    };

    private FullScreenMode DropdownIndexToDisplayMode(int index) => index switch
    {
        0 => FullScreenMode.ExclusiveFullScreen,
        1 => FullScreenMode.Windowed,
        2 => FullScreenMode.FullScreenWindow,
        _ => FullScreenMode.FullScreenWindow
    };

    // ---------- Event handlers ----------

    private void HandleMasterVolumeChanged(float v) { if (!_isInitializing) SettingsManager.Instance.SetMasterVolume(v); }
    private void HandleMusicVolumeChanged(float v) { if (!_isInitializing) SettingsManager.Instance.SetMusicVolume(v); }
    private void HandleSFXVolumeChanged(float v) { if (!_isInitializing) SettingsManager.Instance.SetSFXVolume(v); }
    private void HandleVoiceInputDeviceChanged(int index)
    {
        if (_isInitializing) return;
        SettingsManager.Instance.SetVoiceInputDevice(_inputDeviceOptions[index]);
    }

    private void HandleVoiceOutputDeviceChanged(int index)
    {
        if (_isInitializing) return;
        SettingsManager.Instance.SetVoiceOutputDevice(_outputDeviceOptions[index]);
    }

    private void HandleResolutionOrDisplayModeChanged(int _)
    {
        if (_isInitializing) return;

        Resolution selected = _availableResolutions[_resolutionDropdown.value];
        int screenResIndex = System.Array.FindIndex(Screen.resolutions,
            r => r.width == selected.width && r.height == selected.height);

        FullScreenMode mode = DropdownIndexToDisplayMode(_displayModeDropdown.value);
        SettingsManager.Instance.SetResolutionAndDisplayMode(screenResIndex, mode);
    }

    private void HandleQualityChanged(int index)
    {
        if (!_isInitializing) SettingsManager.Instance.SetQualityLevel(index);
    }

    private void HandleVSyncChanged(bool enabled)
    {
        _frameRateCapDropdown.interactable = !enabled;
        if (!_isInitializing) SettingsManager.Instance.SetVSync(enabled);
    }

    private void HandleFrameRateCapChanged(int index)
    {
        if (!_isInitializing) SettingsManager.Instance.SetFrameRateCap(_frameRateCapOptions[index]);
    }

    private void HandleFOVChanged(float value)
    {
        if (_fovValueLabel != null) _fovValueLabel.text = Mathf.RoundToInt(value).ToString();
        if (!_isInitializing) SettingsManager.Instance.SetFOV(value);
    }

    private void HandleSensitivityChanged(float value)
    {
        if (_sensitivityValueLabel != null) _sensitivityValueLabel.text = value.ToString("F2");
        if (!_isInitializing) SettingsManager.Instance.SetSensitivity(value);
    }

    private void HandleInvertYChanged(bool value)
    {
        if (!_isInitializing) SettingsManager.Instance.SetInvertY(value);
    }

    private void HandleLobbyViewModeChanged(int index)
    {
        if (!_isInitializing) SettingsManager.Instance.SetLobbyViewMode((LobbyViewMode)index);
    }

    private void HandleKeybindMenuClicked()
    {
        // TODO: KEYBINDS — replace with actual rebind submenu once Input System action map rebinding is built
        _confirmDialog.Show("Keybind customization is coming soon.", () => { });
    }
}