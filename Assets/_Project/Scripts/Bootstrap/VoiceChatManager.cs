// VoiceChatManager.cs
// Place in: Assets/_Project/Scripts/Bootstrap/
// Owns: Vivox init/login and the two persistent positional voice channels
// every player stays in for the whole session (lobby-proximity, stage-broadcast).
// Attach to: VoiceChatManager GameObject in Bootstrap scene under _Managers,
// alongside RelayManager/SessionManager/AudioManager/SettingsManager.
//
// Reuses RelayManager.InitializeAsync() for UGS init + anonymous sign-in
// rather than duplicating that call here — RelayManager already guards it
// with an IsInitialized check, so calling it again here is a no-op once
// Relay itself has already logged in (or vice versa, whichever runs first).

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Services.Vivox;

public class VoiceChatManager : SingletonBehaviour<VoiceChatManager>
{
    // Every player joins both of these at spawn and stays in both for the
    // whole session — see StageMic (Phase 3) for how transmission switches
    // between them. Short range for ordinary talking-distance chat; long
    // range + silent-until-someone-transmits for the stage PA effect.
    public const string LobbyProximityChannel = "lobby-proximity";
    public const string StageBroadcastChannel = "stage-broadcast";

    private const float _cloudCallTimeoutSeconds = 5f;

    [Header("Push-to-Talk")]
    // Matches the project's existing input convention (see PlayerMovement's
    // _crouchKey/_shoveKey) — a plain serialized Key polled with
    // Keyboard.current, not an InputAction. Only used when voiceChatMode is
    // PushToTalk; ignored in AlwaysOn mode.
    [SerializeField] private Key _pushToTalkKey = Key.V;

    // Independent from VivoxService.Instance.IsLoggedIn (which the SDK also
    // exposes) — this one only flips true once login AND both persistent
    // channel joins have completed, which is the point at which everything
    // else in this class is safe to use.
    public bool IsLoggedIn { get; private set; }
    private Task _readyTask;
    private readonly HashSet<string> _joinedChannels = new HashSet<string>();

    // Voice settings state, mirrored from SettingsManager and re-evaluated
    // every frame in Update() below to decide whether the mic should
    // actually be transmitting right now.
    private bool _manualMute;
    private VoiceChatMode _mode = VoiceChatMode.PushToTalk;

    // Tracks what we've last told Vivox (muted/unmuted) so Update() only
    // calls MuteInputDevice/UnmuteInputDevice on an actual transition,
    // rather than every frame.
    private bool _isTransmitting;

    protected override void Awake()
    {
        base.Awake();
        SettingsManager.OnVoiceInputDeviceChanged += SetInputDevice;
        SettingsManager.OnVoiceOutputDeviceChanged += SetOutputDevice;
        SettingsManager.OnVoiceChatMutedChanged += SetMuted;
        SettingsManager.OnVoiceChatVolumeChanged += SetVolume;
        SettingsManager.OnVoiceChatModeChanged += SetMode;
    }

    // Push-to-talk / mute enforcement. Only meaningful once logged in —
    // before that, _manualMute/_mode just sit at whatever
    // ApplySavedVoicePreferences (or the settings events) last set them to,
    // and get enforced against Vivox the moment IsLoggedIn flips true and
    // this starts actually running the check below.
    private void Update()
    {
        if (!IsLoggedIn) return;

        bool pushToTalkHeld = Keyboard.current != null && Keyboard.current[_pushToTalkKey].isPressed;
        bool wantsToTransmit = !_manualMute && (_mode == VoiceChatMode.AlwaysOn || pushToTalkHeld);

        if (wantsToTransmit != _isTransmitting)
        {
            _isTransmitting = wantsToTransmit;
            if (_isTransmitting)
                VivoxService.Instance.UnmuteInputDevice();
            else
                VivoxService.Instance.MuteInputDevice();
        }
    }

    // Safe to call repeatedly (e.g. from PlayerObject.Initialize on every
    // local spawn) — the underlying work only actually runs once; later
    // callers just await the same in-flight/completed Task.
    public Task EnsureVoiceReadyAsync()
    {
        if (_readyTask == null)
            _readyTask = LoginAndJoinPersistentChannelsAsync();
        return _readyTask;
    }

    private async Task LoginAndJoinPersistentChannelsAsync()
    {
        if (RelayManager.Instance == null)
        {
            Debug.LogError("[VoiceChatManager] RelayManager not found — cannot initialize UGS for voice.");
            return;
        }

        try
        {
            // UGS init + anonymous sign-in — shared with Relay, see header comment.
            await RelayManager.Instance.InitializeAsync();

            await WithTimeout(VivoxService.Instance.InitializeAsync(), _cloudCallTimeoutSeconds, "VivoxService.InitializeAsync");

            VivoxService.Instance.ChannelJoined += OnChannelJoined;
            VivoxService.Instance.ChannelLeft += OnChannelLeft;

            var loginTcs = new TaskCompletionSource<bool>();
            void HandleLoggedIn() => loginTcs.TrySetResult(true);
            VivoxService.Instance.LoggedIn += HandleLoggedIn;

            await VivoxService.Instance.LoginAsync(new LoginOptions());
            await WithTimeout(loginTcs.Task, _cloudCallTimeoutSeconds, "Vivox login");

            VivoxService.Instance.LoggedIn -= HandleLoggedIn;
            IsLoggedIn = true;

            await JoinChannelAsync(LobbyProximityChannel, audibleDistance: 15, conversationalDistance: 3);
            await JoinChannelAsync(StageBroadcastChannel, audibleDistance: 40, conversationalDistance: 8);

            // Default transmission target is normal talking range. StageMic
            // (Phase 3) switches this to StageBroadcastChannel on pickup and
            // back to LobbyProximityChannel on drop. Login itself defaults
            // transmission to TransmissionMode.All (every joined channel at
            // once), so this narrows it back down to just the lobby channel.
            await VivoxService.Instance.SetChannelTransmissionModeAsync(TransmissionMode.Single, LobbyProximityChannel);

            ApplySavedVoicePreferences();
        }
        catch (Exception e)
        {
            Debug.LogError($"[VoiceChatManager] Voice setup failed: {e.Message}");
        }
    }

    private async Task JoinChannelAsync(string channelName, int audibleDistance, int conversationalDistance)
    {
        // Positional args here, not named — the SDK's own ctor parameter is
        // literally named "audioFadeIntensityByDistanceaudio" (trailing
        // "audio" looks like an SDK typo), so named args would either fail
        // to compile against the documented name or read strangely if we
        // typed the typo out. Order is: audible, conversational, fade
        // intensity, fade model.
        var props = new Channel3DProperties(
            audibleDistance,
            conversationalDistance,
            1.0f,
            AudioFadeModel.InverseByDistance);

        var joinedTcs = new TaskCompletionSource<bool>();
        void HandleJoined(string joined)
        {
            if (joined == channelName)
                joinedTcs.TrySetResult(true);
        }
        VivoxService.Instance.ChannelJoined += HandleJoined;

        await VivoxService.Instance.JoinPositionalChannelAsync(channelName, ChatCapability.AudioOnly, props);
        await WithTimeout(joinedTcs.Task, _cloudCallTimeoutSeconds, $"Join {channelName}");

        VivoxService.Instance.ChannelJoined -= HandleJoined;
    }

    private void OnChannelJoined(string channelName) => _joinedChannels.Add(channelName);
    private void OnChannelLeft(string channelName) => _joinedChannels.Remove(channelName);
    public bool IsInChannel(string channelName) => _joinedChannels.Contains(channelName);

    // ─── Settings wiring ──────────────────────────────────────────────────
    // Subscribed to all five SettingsManager voice events in Awake above —
    // device selection was the original gap that class's old TODO comment
    // flagged; mute/volume/mode are new alongside push-to-talk. No changes
    // to SettingsManager.cs's own logic needed beyond adding the new
    // fields/events; this follows the same "other systems subscribe to
    // these" convention it already used for device selection.

    private void ApplySavedVoicePreferences()
    {
        if (SettingsManager.Instance == null) return;
        string savedInput = SettingsManager.Instance.Current.voiceInputDeviceName;
        if (!string.IsNullOrEmpty(savedInput) && savedInput != "Default")
            SetInputDevice(savedInput);

        string savedOutput = SettingsManager.Instance.Current.voiceOutputDeviceName;
        if (!string.IsNullOrEmpty(savedOutput) && savedOutput != "Default")
            SetOutputDevice(savedOutput);

        SetMuted(SettingsManager.Instance.Current.voiceChatMuted);
        SetVolume(SettingsManager.Instance.Current.voiceChatVolume);
        SetMode(SettingsManager.Instance.Current.voiceChatMode);
    }

    public void SetInputDevice(string deviceName)
    {
        if (!IsLoggedIn) return;
        foreach (var device in VivoxService.Instance.AvailableInputDevices)
        {
            if (device.DeviceName == deviceName)
            {
                _ = VivoxService.Instance.SetActiveInputDeviceAsync(device);
                return;
            }
        }
    }

    public void SetOutputDevice(string deviceName)
    {
        if (!IsLoggedIn) return;
        foreach (var device in VivoxService.Instance.AvailableOutputDevices)
        {
            if (device.DeviceName == deviceName)
            {
                _ = VivoxService.Instance.SetActiveOutputDeviceAsync(device);
                return;
            }
        }
    }

    // Just updates local state — Update() above is what actually calls
    // MuteInputDevice/UnmuteInputDevice on Vivox, since the real
    // transmit/mute decision also depends on push-to-talk key state, which
    // only Update() can observe per-frame.
    private void SetMuted(bool muted) => _manualMute = muted;

    private void SetVolume(float volume)
    {
        if (!IsLoggedIn) return;
        // voiceChatVolume is a 0-1 slider value; Vivox's own output-volume
        // scale is -50..50 where 0 is "unmodified" (its default), so 0.5 on
        // the slider maps to Vivox's neutral point rather than to silence —
        // this is a boost/cut around the natural recorded volume, not a
        // traditional 0-to-max gain control. Worth calling out in any
        // settings UI copy/tooltip.
        int vivoxVolume = Mathf.RoundToInt(Mathf.Lerp(-50, 50, Mathf.Clamp01(volume)));
        VivoxService.Instance.SetOutputDeviceVolume(vivoxVolume);
    }

    // Same reasoning as SetMuted — Update() reads _mode each frame rather
    // than this method driving Vivox directly, so switching modes mid-hold
    // (e.g. flipping to AlwaysOn while the push-to-talk key happens to be
    // up) takes effect on the very next frame with no extra bookkeeping.
    private void SetMode(VoiceChatMode mode) => _mode = mode;

    private void OnDestroy()
    {
        SettingsManager.OnVoiceInputDeviceChanged -= SetInputDevice;
        SettingsManager.OnVoiceOutputDeviceChanged -= SetOutputDevice;
        SettingsManager.OnVoiceChatMutedChanged -= SetMuted;
        SettingsManager.OnVoiceChatVolumeChanged -= SetVolume;
        SettingsManager.OnVoiceChatModeChanged -= SetMode;

        if (VivoxService.Instance == null) return;
        VivoxService.Instance.ChannelJoined -= OnChannelJoined;
        VivoxService.Instance.ChannelLeft -= OnChannelLeft;
    }

    // ─── Timeout helper — identical pattern to RelayManager.WithTimeout ────

    private static async Task<T> WithTimeout<T>(Task<T> task, float timeoutSeconds, string operationLabel)
    {
        Task winner = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));
        if (winner != task)
            throw new TimeoutException($"[VoiceChatManager] {operationLabel} timed out after {timeoutSeconds:0}s.");
        return await task;
    }

    private static async Task WithTimeout(Task task, float timeoutSeconds, string operationLabel)
    {
        Task winner = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));
        if (winner != task)
            throw new TimeoutException($"[VoiceChatManager] {operationLabel} timed out after {timeoutSeconds:0}s.");
        await task;
    }
}
