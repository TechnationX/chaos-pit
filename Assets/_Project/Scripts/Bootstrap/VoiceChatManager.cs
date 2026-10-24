// VoiceChatManager.cs
// Place in: Assets/_Project/Scripts/Bootstrap/
// Owns: Vivox init/login and the lobby's voice channels.
// Attach to: VoiceChatManager GameObject in Bootstrap scene under _Managers,
// alongside RelayManager/SessionManager/AudioManager/SettingsManager.
//
// lobby-proximity is a real Vivox positional channel — every player joins
// it at login and stays in it for the whole session, same as before.
// stage-broadcast is NOT positional: Vivox only allows one positional
// ("ether") channel per login session, and lobby-proximity already claims
// that slot (confirmed in practice — a second JoinPositionalChannelAsync
// call throws "Only one ether channel is allowed"). stage-broadcast is
// instead a plain 2-D group channel that each player's local client joins
// or leaves outright based on distance to the nearest StageSpeaker — see
// SetStageBroadcastRange, called every frame by PlayerObject. That gives a
// true hard cutoff (actual silence outside range) rather than the
// quiet-but-not-silent floor a channel-volume approach would give.
//
// There's no mic pickup/hold interaction — per explicit instruction, the
// mic passively picks up any player's voice within its own short
// PickupRange (StageMic.cs), the way a real stage mic picks up nearby
// sound. SetMicPickupRange is the other half of that: a player within mic
// range gets their transmission mode switched to TransmissionMode.All
// (lobby-proximity AND stage-broadcast simultaneously, so being near the
// mic adds reach rather than replacing normal nearby talking) and back to
// lobby-proximity only once they leave.
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
    // lobby-proximity: joined at login, stayed in for the whole session —
    // the real Vivox positional channel, short range for ordinary
    // talking-distance chat.
    // stage-broadcast: NOT joined at login — joined/left per-frame by
    // SetStageBroadcastRange based on distance to the nearest StageSpeaker
    // OR StageMic (see class header comment).
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
    private Task _initTask;
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

    // stage-broadcast hard-cutoff state — see SetStageBroadcastRange.
    // _stageBroadcastDesiredInRange is the latest verdict PlayerObject has
    // reported; _stageBroadcastTransitioning guards against starting a
    // second join/leave call for this channel while one is still in
    // flight (a plain in-flight bool, not a queue, is enough because
    // RunStageBroadcastTransitionsAsync loops until the actual channel
    // state matches whatever the desired state last settled on).
    private bool _stageBroadcastDesiredInRange;
    private bool _stageBroadcastTransitioning;

    // Mic-pickup transmission state — see SetMicPickupRange. Same
    // desired-state + transitioning-guard pattern as the stage-broadcast
    // range fields above, for the same reason.
    private bool _micPickupDesiredActive;
    private bool _micPickupTransitioning;

    protected override void Awake()
    {
        base.Awake();
        SettingsManager.OnVoiceInputDeviceChanged += SetInputDevice;
        SettingsManager.OnVoiceOutputDeviceChanged += SetOutputDevice;
        SettingsManager.OnVoiceChatMutedChanged += SetMuted;
        SettingsManager.OnVoiceChatVolumeChanged += SetVolume;
        SettingsManager.OnVoiceChatModeChanged += SetMode;
    }

    private void Start()
    {
        // Kick off Vivox SDK init (NOT login) here rather than Awake() —
        // Unity guarantees every active object's Awake() has already run
        // by the time any Start() runs, so RelayManager.Instance (set in
        // ITS OWN Awake(), via SingletonBehaviour) is guaranteed to exist
        // by now, regardless of GameObject/script order under _Managers.
        // Doing this from Awake() instead raced RelayManager's own Awake()
        // and intermittently failed with "RelayManager instance is null" /
        // "RelayManager not found — cannot initialize UGS for voice."
        //
        // This is what populates VivoxService.Instance.AvailableOutputDevices/
        // AvailableInputDevices for the Settings menu while the player is
        // still sitting in the Main Menu, before ever joining a lobby —
        // device enumeration is a client/SDK-level Vivox feature, separate
        // from logging in and joining channels (which still only happens
        // once a player actually spawns, via EnsureVoiceReadyAsync). See
        // EnsureVivoxInitializedAsync below.
        _ = EnsureVivoxInitializedAsync();
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

    // Safe to call repeatedly (e.g. once from Awake() on game boot, and
    // again from inside LoginAndJoinPersistentChannelsAsync once a player
    // actually spawns) — the underlying work only actually runs once; later
    // callers just await the same in-flight/completed Task. Split out from
    // login/channel-join specifically so VivoxService.Instance.
    // AvailableOutputDevices/AvailableInputDevices (what the Settings menu
    // reads to populate its device dropdowns) are populated as early as
    // possible — that's an SDK/client-level Vivox feature and doesn't need
    // an actual login or lobby to exist, unlike joining real channels below.
    public Task EnsureVivoxInitializedAsync()
    {
        if (_initTask == null)
            _initTask = InitializeVivoxSdkAsync();
        return _initTask;
    }

    private async Task InitializeVivoxSdkAsync()
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

            // Explicitly enabled here rather than trusting whatever the
            // native Vivox client's undocumented default happens to be.
            // This is the actual fix for "the mic should ignore whatever's
            // coming out of the speakers": Acoustic Echo Cancellation (AEC)
            // tracks Vivox's OWN rendered output on THIS client — which is
            // exactly what a player hears through a StageSpeaker, since the
            // stage-broadcast voice they're listening to IS Vivox audio —
            // and strips any portion of it back out of that same client's
            // own mic input before transmitting. So a player standing in
            // both StageMic pickup range and StageSpeaker listening range
            // at once (very likely near the stage) doesn't create a
            // feedback loop by re-broadcasting whatever their own
            // headphones/speakers are currently playing back. It's
            // per-client and automatic once enabled — no StageMic/
            // StageSpeaker-specific logic needed, since it isn't really a
            // proximity problem, it's an acoustic one. One caveat: this
            // only cancels VIVOX's own playback, not unrelated Unity game
            // audio (e.g. InstrumentInteractable's SFX) — Vivox's AEC has
            // no visibility into that separate, non-Vivox audio path.
            // Confirmed safe to call this early against the SDK source —
            // it queues itself and triggers its own init if called before
            // InitializeAsync resolves — kept right after init anyway so
            // this method's whole Vivox setup sequence stays in one place.
            VivoxService.Instance.EnableAcousticEchoCancellation();
        }
        catch (Exception e)
        {
            Debug.LogError($"[VoiceChatManager] Vivox SDK init failed: {e.Message}");
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
        try
        {
            // Make sure SDK init (kicked off independently from Awake(),
            // see EnsureVivoxInitializedAsync above) has actually finished
            // before logging in — normally already long done by the time a
            // player reaches this point, but awaiting it here keeps this
            // method correct even if it somehow ran first.
            await EnsureVivoxInitializedAsync();

            VivoxService.Instance.ChannelJoined += OnChannelJoined;
            VivoxService.Instance.ChannelLeft += OnChannelLeft;

            var loginTcs = new TaskCompletionSource<bool>();
            void HandleLoggedIn() => loginTcs.TrySetResult(true);
            VivoxService.Instance.LoggedIn += HandleLoggedIn;

            await VivoxService.Instance.LoginAsync(new LoginOptions());
            await WithTimeout(loginTcs.Task, _cloudCallTimeoutSeconds, "Vivox login");

            VivoxService.Instance.LoggedIn -= HandleLoggedIn;

            await JoinPositionalAsync(LobbyProximityChannel, audibleDistance: 15, conversationalDistance: 3);

            // IsLoggedIn flips true here, not right after login above — it's
            // what PlayerObject.Update() gates its per-frame Set3DPosition
            // call on, and Set3DPosition throws ("you are not currently in
            // any channels") if called after login succeeds but before the
            // lobby-proximity channel join above has actually finished.
            // Setting it true too early left a real window (roughly however
            // many frames fit in that one channel-join round trip) where
            // Update() saw IsLoggedIn == true and called Set3DPosition
            // before Vivox had actually joined the channel, throwing on
            // every one of those frames until the join caught up.
            IsLoggedIn = true;

            // stage-broadcast is deliberately NOT joined here — see class
            // header comment. SetStageBroadcastRange joins/leaves it later,
            // on-demand, once PlayerObject reports the local player is
            // within a StageSpeaker's VoiceCutoffDistance.

            // Default transmission target is normal talking range.
            // SetMicPickupRange widens this to TransmissionMode.All while a
            // player is within a StageMic's pickup range. Login itself
            // defaults transmission to TransmissionMode.All (every joined
            // channel at once), so this narrows it back down to just the
            // lobby channel to start.
            await VivoxService.Instance.SetChannelTransmissionModeAsync(TransmissionMode.Single, LobbyProximityChannel);

            ApplySavedVoicePreferences();
        }
        catch (Exception e)
        {
            Debug.LogError($"[VoiceChatManager] Voice setup failed: {e.Message}");
        }
    }

    private async Task JoinPositionalAsync(string channelName, int audibleDistance, int conversationalDistance)
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

    // Plain 2-D group channel join — no Channel3DProperties, since this is
    // used for stage-broadcast, which can't be positional (see class
    // header comment). Same ChannelJoined/TaskCompletionSource wiring as
    // JoinPositionalAsync above, just against JoinGroupChannelAsync instead.
    private async Task JoinGroupAsync(string channelName)
    {
        var joinedTcs = new TaskCompletionSource<bool>();
        void HandleJoined(string joined)
        {
            if (joined == channelName)
                joinedTcs.TrySetResult(true);
        }
        VivoxService.Instance.ChannelJoined += HandleJoined;

        await VivoxService.Instance.JoinGroupChannelAsync(channelName, ChatCapability.AudioOnly);
        await WithTimeout(joinedTcs.Task, _cloudCallTimeoutSeconds, $"Join {channelName}");

        VivoxService.Instance.ChannelJoined -= HandleJoined;
    }

    private void OnChannelJoined(string channelName) => _joinedChannels.Add(channelName);
    private void OnChannelLeft(string channelName) => _joinedChannels.Remove(channelName);
    public bool IsInChannel(string channelName) => _joinedChannels.Contains(channelName);

    // Called every frame by the local player's PlayerObject once its
    // distance to the nearest active StageSpeaker crosses that speaker's
    // VoiceCutoffDistance (PlayerObject applies its own hysteresis buffer
    // before calling this again in the opposite direction, so this isn't
    // firing every single frame in practice). Joins or leaves the
    // stage-broadcast channel outright — a true hard cutoff, not a volume
    // fade — rather than relying on Set3DPosition/Channel3DProperties,
    // which stage-broadcast can't use since it isn't a positional channel.
    //
    // Safe to call repeatedly with the same value (no-ops once already in
    // the requested state) and safe to call again mid-transition — it just
    // records the latest desired state and the in-flight transition loop
    // picks it up once the current join/leave call resolves, rather than
    // the request being silently dropped.
    public void SetStageBroadcastRange(bool inRange)
    {
        if (!IsLoggedIn) return;

        _stageBroadcastDesiredInRange = inRange;
        if (_stageBroadcastTransitioning) return;
        if (inRange == IsInChannel(StageBroadcastChannel)) return;

        _ = RunStageBroadcastTransitionsAsync();
    }

    private async Task RunStageBroadcastTransitionsAsync()
    {
        _stageBroadcastTransitioning = true;
        try
        {
            // A loop, not a single join/leave, in case the desired state
            // changes again (player crosses back the other way) while the
            // previous Vivox call for this channel is still in flight —
            // keeps going until actual channel membership matches the
            // latest verdict from PlayerObject.
            while (_stageBroadcastDesiredInRange != IsInChannel(StageBroadcastChannel))
            {
                try
                {
                    if (_stageBroadcastDesiredInRange)
                        await JoinGroupAsync(StageBroadcastChannel);
                    else
                        await VivoxService.Instance.LeaveChannelAsync(StageBroadcastChannel);
                }
                catch (Exception e)
                {
                    Debug.LogError($"[VoiceChatManager] stage-broadcast range transition failed: {e.Message}");
                    break; // avoid looping forever on a persistent failure
                }
            }
        }
        finally
        {
            _stageBroadcastTransitioning = false;
        }
    }

    // Called every frame by the local player's PlayerObject once its
    // distance to the nearest active StageMic crosses that mic's
    // PickupRange. true switches local transmission to
    // TransmissionMode.All, so the player's voice goes into BOTH
    // lobby-proximity (normal nearby talking still works) AND
    // stage-broadcast (picked up by the mic, carried through the PA) at
    // the same time — per explicit instruction, standing near the mic adds
    // reach rather than replacing normal talking. false switches back to
    // TransmissionMode.Single(LobbyProximityChannel), the default.
    //
    // This only decides WHERE a transmitting player's voice goes — whether
    // they're transmitting at all is still entirely controlled by
    // mute/push-to-talk in Update() above, untouched by this. It also
    // doesn't itself join the stage-broadcast channel — PlayerObject
    // already requests that (via SetStageBroadcastRange) whenever mic
    // range is true, since picking up the mic is its own independent
    // reason to need channel membership alongside listening range.
    public void SetMicPickupRange(bool inRange)
    {
        if (!IsLoggedIn) return;

        _micPickupDesiredActive = inRange;
        if (_micPickupTransitioning) return;
        if (inRange == IsTransmittingToStageBroadcast()) return;

        _ = RunMicPickupTransitionsAsync();
    }

    private bool IsTransmittingToStageBroadcast() => VivoxService.Instance.TransmittingChannels.Contains(StageBroadcastChannel);

    private async Task RunMicPickupTransitionsAsync()
    {
        _micPickupTransitioning = true;
        try
        {
            // Same loop-until-converged shape as RunStageBroadcastTransitionsAsync.
            // Note TransmittingChannels can briefly disagree with a call
            // that already succeeded — e.g. if mic range went true before
            // the separate stage-broadcast channel join (triggered by
            // PlayerObject in the same frame) has actually landed,
            // TransmissionMode.All is already the correct standing policy
            // but won't show stage-broadcast in TransmittingChannels until
            // that join completes. The loop just retries harmlessly until
            // it does, rather than needing the two systems to be
            // explicitly sequenced against each other.
            while (_micPickupDesiredActive != IsTransmittingToStageBroadcast())
            {
                try
                {
                    if (_micPickupDesiredActive)
                        await VivoxService.Instance.SetChannelTransmissionModeAsync(TransmissionMode.All);
                    else
                        await VivoxService.Instance.SetChannelTransmissionModeAsync(TransmissionMode.Single, LobbyProximityChannel);
                }
                catch (Exception e)
                {
                    Debug.LogError($"[VoiceChatManager] Mic pickup transmission change failed: {e.Message}");
                    break; // avoid looping forever on a persistent failure
                }
            }
        }
        finally
        {
            _micPickupTransitioning = false;
        }
    }

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
