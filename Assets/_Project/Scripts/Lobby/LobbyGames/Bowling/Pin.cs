// Pin.cs
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

/// <summary>
/// One physical pin slot. Spawned once per lane at server start and never
/// despawned — a pattern switch or frame reset just toggles it between
/// standing and hidden via ServerSetStanding()/ServerSetHidden(), driven by
/// LobbySpawner. See BowlingPinConfig's class comment for why this avoids
/// the pool table's despawn/spawn burst issue entirely.
///
/// _isStandingSync is polled from Update() rather than reacted to via
/// OnChange, matching Grabbable's _holdingPlayerNetObjSync — FishNet
/// SyncVar OnChange callbacks were found not to fire reliably on host in
/// this project (see Grabbable.cs), so every peer just checks the current
/// value each frame and self-corrects, same principle applied here.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class Pin : NetworkBehaviour
{
    [Header("Fall Detection")]
    [Tooltip("Degrees off vertical before a pin reported by PinFallTrigger is confirmed fallen, rather than just wobbling near the sensor boundary.")]
    [SerializeField] private float _fallAngleThreshold = 35f;

    // Same pattern as Grabbable's impact SFX (see that class's
    // OnCollisionEnter) — played locally on whatever peer detects the
    // collision, no server gate, since physics is simulated independently
    // on every peer in this project. Not routed through AudioManager's
    // single shared source's cooldown; each pin tracks its own, so a
    // chain-reaction strike layers several pins' clatter together instead
    // of one pin's cooldown silencing its neighbors.
    //
    // Two separate clip pools rather than one generic "impact" sound — a
    // heavy ball strike and a light pin-on-pin knock don't sound the same
    // in real bowling, and the split lets each be tuned/mixed independently.
    // Which pool plays is decided by what's on the OTHER side of the
    // collision (see OnCollisionEnter), resolved via GetComponentInParent
    // the same way CueTip/PoolPocket identify what they've hit elsewhere in
    // this project — works whether the collider sits on the same object as
    // the component or on a child mesh.
    [Header("Impact SFX")]
    [Tooltip("Played when the BALL hits this pin.")]
    [SerializeField] private AudioClip[] _ballImpactClips;
    [Tooltip("Played when another PIN hits this pin.")]
    [SerializeField] private AudioClip[] _pinImpactClips;
    [SerializeField] private float _minImpactVelocity = 1.5f;
    [SerializeField] private float _impactCooldown = 0.15f;

    private float _lastImpactTime = -999f;

    private readonly SyncVar<bool> _isStandingSync = new SyncVar<bool>();

    private Rigidbody _rigidbody;
    private Collider[] _colliders;
    private Renderer[] _renderers;

    private Vector3 _slotPosition;
    private Quaternion _slotRotation;

    private bool _lastAppliedStanding = true;
    private bool _pendingFallCheck;
    private bool _isDown;

    // Reads the IMMEDIATE physical state, not the delayed visual one.
    // _isStandingSync only flips once LobbySpawner's group-hide coordinator
    // calls ServerHideNow() (see IsAwaitingHide below) — that's correct for
    // the visual/kinematic toggle, but scoring (BowlingGameController.
    // CountStandingPins()) needs to know the instant a pin is confirmed
    // fallen, not whenever the group finally hides. _isDown tracks that
    // immediate state separately.
    public bool IsStanding => !_isDown;

    // True once this pin has fallen but hasn't been hidden yet — i.e. it's
    // still lying there, still visible and simulated. LobbySpawner polls
    // this across a whole lane so every pin that fell during a roll can be
    // hidden together, timed from whichever pin fell LAST, instead of each
    // pin vanishing independently on its own schedule (staggered, random-
    // looking). See LobbySpawner.UpdateBowlingPinGroupHide().
    public bool IsAwaitingHide => _isDown && _isStandingSync.Value;

    private void Awake()
    {
        _rigidbody = GetComponent<Rigidbody>();
        _colliders = GetComponentsInChildren<Collider>();
        _renderers = GetComponentsInChildren<Renderer>();

        // Matches _lastAppliedStanding's default below so Update()'s poll
        // sees no mismatch on the pin's first frame. Without this, every
        // freshly spawned pin's SyncVar defaults to false (not standing)
        // while _lastAppliedStanding starts true, so the very first Update()
        // treats that as a real change and flips the pin to hidden/kinematic
        // for the several frames before LobbySpawner's spawn coroutine gets
        // around to calling ServerSetStanding() on it — which is also what
        // set up the kinematic-velocity warning below (ServerSetStanding was
        // clearing velocity while the Rigidbody was still kinematic from
        // this spurious flip).
        _isStandingSync.Value = true;
    }

    private void Update()
    {
        if (_isStandingSync.Value != _lastAppliedStanding)
        {
            _lastAppliedStanding = _isStandingSync.Value;
            ApplyStandingVisualState(_lastAppliedStanding);
        }

        // Confirmed the frame AFTER the trigger fires, not inside the
        // trigger callback itself — gives the pin one more physics step to
        // settle so a pin just clipping the sensor boundary while still
        // upright doesn't get marked fallen on a false positive.
        if (IsServerInitialized && _pendingFallCheck)
        {
            _pendingFallCheck = false;
            if (Vector3.Angle(transform.up, Vector3.up) > _fallAngleThreshold)
                ServerMarkFallen();
        }

        // Safety net: PinFallSensor's trigger is the primary detector, but
        // it's a thin box a fast-toppling pin can tunnel through between
        // physics steps without ever firing OnTriggerExit (confirmed in
        // testing — pins occasionally landed fallen-but-undetected in the
        // pit). Self-polling the tip angle every frame here means a missed
        // trigger event can never permanently strand a fallen pin; it just
        // routes into the same one-frame-later confirmation above instead
        // of marking fallen immediately, so it's no less debounced than the
        // trigger path.
        if (IsServerInitialized && !_pendingFallCheck && _isStandingSync.Value && !_isDown
            && Vector3.Angle(transform.up, Vector3.up) > _fallAngleThreshold)
        {
            _pendingFallCheck = true;
        }
    }

    /// Called once by LobbySpawner right after this pin is spawned, so a
    /// reset always knows where "standing" means for this specific slot.
    public void SetSlotTransform(Vector3 position, Quaternion rotation)
    {
        _slotPosition = position;
        _slotRotation = rotation;
    }

    /// Called by PinFallTrigger when this pin's collider exits the
    /// fall-detection sensor band. Doesn't mark fallen immediately — flags
    /// it for an angle check next Update() instead (see comment there).
    public void ReportPossibleFall()
    {
        if (!IsServerInitialized || !_isStandingSync.Value) return;
        _pendingFallCheck = true;
    }

    // Marks the pin fallen immediately but does NOT hide it — that decision
    // now belongs to LobbySpawner's group-hide coordinator (IsAwaitingHide
    // above), which waits until every pin from this roll has finished
    // falling before hiding them all together.
    [Server]
    private void ServerMarkFallen()
    {
        if (!_isStandingSync.Value || _isDown) return;
        _isDown = true;
    }

    /// Called by LobbySpawner once this pin (and every other pin that fell
    /// in the same roll) is ready to be hidden — see IsAwaitingHide and
    /// LobbySpawner.UpdateBowlingPinGroupHide().
    [Server]
    public void ServerHideNow()
    {
        if (!_isStandingSync.Value) return;
        _isStandingSync.Value = false;
    }

    /// Resets this pin upright at its slot position — used for a fresh rack
    /// and for re-including a slot when a different pattern is selected.
    [Server]
    public void ServerSetStanding()
    {
        // Unlocked directly here rather than waiting for Update()'s poll to
        // do it — that poll runs a frame late, and this method needs the
        // Rigidbody non-kinematic immediately below to legally clear
        // velocity (Unity logs "Setting linear/angular velocity of a
        // kinematic body is not supported" otherwise, which is what was
        // happening: a pin transitioning from hidden to standing still had
        // isKinematic true at this exact point in the same call).
        _rigidbody.isKinematic = false;
        transform.SetPositionAndRotation(_slotPosition, _slotRotation);
        _rigidbody.linearVelocity = Vector3.zero;
        _rigidbody.angularVelocity = Vector3.zero;
        _isStandingSync.Value = true;
        _isDown = false;

        // The transform set above only ever runs on whichever machine is
        // executing this [Server] method — on host that's the same instance
        // players actually see, but a real remote client never gets it.
        // Pin has no NetworkTransform (unlike CueBall/PoolBall), and
        // _isStandingSync only drives ApplyStandingVisualState (colliders/
        // renderers/kinematic — see Update()), never the transform itself.
        // Without this broadcast a client's pin just gets re-enabled exactly
        // where it was last lying, which reads as "never reset." Same
        // [Server] + [ObserversRpc] pattern CueBall.ServerResetTo /
        // ObserversResetTo already use for this identical problem.
        ObserversSetStandingTransform(_slotPosition, _slotRotation);
    }

    [ObserversRpc]
    private void ObserversSetStandingTransform(Vector3 position, Quaternion rotation)
    {
        // Same kinematic guard as the [Server] block above — this RPC and
        // the _isStandingSync SyncVar change can arrive in either order, so
        // don't assume Update()'s poll already cleared isKinematic first.
        _rigidbody.isKinematic = false;
        transform.SetPositionAndRotation(position, rotation);
        _rigidbody.linearVelocity = Vector3.zero;
        _rigidbody.angularVelocity = Vector3.zero;
    }

    /// Removes this pin from play without moving it — used when the active
    /// pattern doesn't include this slot (e.g. Big Four leaves 6 hidden).
    [Server]
    public void ServerSetHidden()
    {
        _isStandingSync.Value = false;
        _isDown = true;
    }

    private void ApplyStandingVisualState(bool standing)
    {
        foreach (var col in _colliders)
            if (col != null) col.enabled = standing;

        foreach (var rend in _renderers)
            if (rend != null) rend.enabled = standing;

        _rigidbody.isKinematic = !standing;
    }

    // Ball-hits-pin and pin-hits-pin pick different clip pools (see the
    // field comment above). Anything that's neither — the lane floor, a
    // gutter/back wall — is deliberately skipped: there's no dedicated
    // sound for that yet, and playing a pin-hit clip for a pin settling
    // against the floor would be wrong. Mirrors Grabbable.OnCollisionEnter's
    // exact pattern otherwise (min velocity + per-object cooldown +
    // AudioManager.PlaySFXAtPosition), just with its own cooldown clock per
    // pin instead of one shared across every Grabbable.
    //
    // Root cause of this sound being silent for a while wasn't in this
    // method at all — LobbySpawner's Lane R Pin Prefab field had drifted to
    // a different, older prefab asset than this script/BowlingPin.prefab.
    // Confirmed fixed once that field was reassigned in the Inspector.
    private void OnCollisionEnter(Collision collision)
    {
        if (Time.time - _lastImpactTime < _impactCooldown) return;

        float impactSpeed = collision.relativeVelocity.magnitude;
        if (impactSpeed < _minImpactVelocity) return;

        AudioClip[] clips;
        if (collision.collider.GetComponentInParent<BowlingBall>() != null)
            clips = _ballImpactClips;
        else if (collision.collider.GetComponentInParent<Pin>() != null)
            clips = _pinImpactClips;
        else
            return; // floor, walls, etc. — no dedicated sound for this yet

        if (clips == null || clips.Length == 0) return;

        _lastImpactTime = Time.time;

        AudioClip clip = clips[Random.Range(0, clips.Length)];
        // Was PlaySFXVaried (non-positional, same volume for every player
        // regardless of distance/room) — PlaySFXAtPosition gives this real
        // 3D falloff instead, same reasoning as Grabbable.OnCollisionEnter.
        AudioManager.Instance?.PlaySFXAtPosition(clip, transform.position, 0.08f);
    }
}
