// Cue
using FishNet.Component.Transforming;
using FishNet.Object;
using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// A pool cue. Extends SettlingGrabbable (see the note further down) but
/// changes what LMB/RMB do while held:
/// LMB (routed through OnInteract by InteractionManager, same wiring as any
/// other held Grabbable) triggers a shoot — a short forward thrust of the
/// cue itself. It stays in hand the whole time: kinematic, parented to
/// HandSocket, nothing detaches or launches. RMB drops it instead, mirroring
/// Throwable's existing RMB-for-secondary-action convention (raw input
/// poll, bypassing OnInteract) — LMB is no longer available for dropping
/// since it's now dedicated to Shoot.
///
/// Contact with the cue ball is detected via a trigger collider at the
/// cue's tip (see CueTip.cs — attach it to a child GameObject positioned at
/// the business end of the cue model), NOT a raycast/aim check. "Aimed at
/// the cue ball" falls out naturally from whether the tip's swept path
/// overlaps the ball's collider during the thrust — no separate aim logic
/// needed.
///
/// Networking mirrors Throwable's pattern: the owner requests a shoot via
/// ServerRpc, the server validates and broadcasts via ObserversRpc, and
/// every observing client (including the shooter) runs the same thrust
/// coroutine and detects the tip/ball overlap on its own local physics —
/// but only for LOCAL COSMETIC PREDICTION now (see OnTipTriggerEnter).
/// The ACTUAL force application is authoritative, reported by the
/// shooter alone via ServerReportHit and applied once, server-side.
///
/// This used to be fully decentralized ("every peer applies the hit to
/// its own local Rigidbody, good enough since physics converges"), but
/// that broke down specifically for this one-shot trigger overlap: the
/// cue is parented to the shooting player's HandSocket, whose transform
/// is itself replicated with normal network latency/interpolation to
/// every OTHER peer. The shooter's own client has zero-latency knowledge
/// of their own hand position, so their local overlap check is reliable
/// — but a remote observer's (including host's, when a client is
/// shooting) copy of that same swing is checking overlap against a
/// slightly-lagging position. Over the ~0.08–0.18s thrust
/// (_thrustDuration/_returnDuration) that's often enough for the tip to
/// cleanly miss the ball on the laggy peer's simulation even though it
/// clearly hit on the shooter's — reported as "client hits the ball, but
/// it never moves on the host." Ball-vs-ball collisions during normal
/// rolling don't have this failure mode (continuous multi-step contact,
/// not a single fast trigger check), so this fix is scoped to the strike
/// moment only.
///
/// Extends SettlingGrabbable (not plain Grabbable) so a drop doesn't hand
/// off to real dynamic Rigidbody physics — that was the cause of the cue
/// glitching/clipping into the floor on drop, same failure mode
/// SettlingGrabbable was originally built to fix for the pool rack. Instead
/// the Rigidbody stays kinematic permanently and OnObserversDrop (inherited,
/// unchanged here) raycasts straight down and lerps the cue to rest on
/// whatever it finds. Tune _settleRestOffset in the Inspector to roughly the
/// distance from the cue's pivot to the bottom of its collider — the
/// rack's tuned value won't be right for the cue's very different shape.
/// One tradeoff: the cue no longer tumbles/bounces on drop, it settles
/// directly to rest — see SettlingGrabbable's own class comment.
/// </summary>
public class Cue : SettlingGrabbable
{
    [Header("Cue Tip")]
    [Tooltip("Child GameObject with a CueTip component + trigger Collider, positioned at the business end of the cue model.")]
    [SerializeField] private CueTip _tip;

    [Header("Hand Orientation")]
    [Tooltip("Grabbable normally zeroes local rotation on grab (transform.localRotation = identity), which points the cue however its model happens to be oriented — usually wrong. This Euler offset is applied instead. Tune in Play mode: grab the cue, adjust these values until it points correctly, then copy them back into the prefab's default.")]
    [SerializeField] private Vector3 _handRotationOffset = Vector3.zero;
    [Tooltip("Grabbable normally zeroes local position on grab (transform.localPosition = zero), which places the cue's pivot exactly at the hand socket — usually not where you want it to visually sit. This offset (in the hand socket's local space) is applied instead. This is also the rest position the shoot thrust animates from/to.")]
    [SerializeField] private Vector3 _handPositionOffset = Vector3.zero;

    [Header("Shoot Settings")]
    [Tooltip("Local-space direction the cue thrusts along when shooting. Usually Vector3.forward — flip the axis/sign if the cue thrusts the wrong way for how this model is oriented.")]
    [SerializeField] private Vector3 _thrustDirection = Vector3.forward;
    [SerializeField] private float _thrustDistance = 0.25f;
    [SerializeField] private float _thrustDuration = 0.08f;
    [SerializeField] private float _returnDuration = 0.18f;
    [Tooltip("Impulse force applied to the cue ball's Rigidbody on contact.")]
    [SerializeField] private float _hitForce = 6f;
    [Tooltip("Minimum time between shots — also debounced immediately on click so rapid double-clicks can't queue two shots before the first round-trips to the server.")]
    [SerializeField] private float _shotCooldown = 0.4f;

    private bool _isShooting;
    private bool _hasHitThisShot;
    private float _cooldownUntil;

    // Server-side guard so ServerReportHit can only ever apply force once
    // per shot, even if the ServerRpc somehow arrived twice (retry, or a
    // malicious/buggy client). Reset at the top of ShootRoutine() — which
    // the server runs its own copy of, same as every other observer.
    private bool _serverStruckThisShot;

    // World-space direction the tip is actually travelling during the current
    // shot. Captured once at the start of ShootRoutine() from the same local
    // vector that drives the thrust lerp, then reused for the strike — see
    // the comment on OnTipTriggerEnter for why this replaced transform.forward.
    private Vector3 _shotWorldDirection;

    protected override void Awake()
    {
        base.Awake();
        if (_tip != null)
            _tip.Owner = this;
    }

    // LMB while held shoots instead of dropping — overrides Grabbable's default
    // "LMB while held = drop" behavior. Not-held case (grab) is unchanged.
    public override void OnInteract(PlayerObject player)
    {
        if (_isHeld)
        {
            if (player.IsHoldingObject && player.HeldObject == this)
                RequestShoot(player);
            return;
        }

        if (player.IsHoldingObject) return;
        ServerGrab(player);
    }

    // Was a plain `private void Update()` — changed to `protected override`
    // calling base.Update() so Grabbable's per-frame held-state polling
    // (ApplyHeldVisualState) still runs for Cue instances instead of being
    // hidden by this override. Same fix already applied to Throwable.cs;
    // Cue.cs was missed the first time since it extends Grabbable directly
    // rather than Throwable.
    protected override void Update()
    {
        base.Update();

        if (!_isHeld || _holdingPlayer == null) return;
        if (!_holdingPlayer.IsOwner) return;

        // Live-tuning support: normally _handPositionOffset/_handRotationOffset
        // are applied once, at grab time (OnObserversGrab below), so editing
        // them in the Inspector while already holding the cue did nothing until
        // you dropped and re-grabbed. Reapplying them here every frame instead
        // makes Inspector edits show up immediately — grab the cue in Play mode,
        // tweak the two fields, watch it move live, then copy the final values
        // into the prefab's default. Skipped while _isShooting so this doesn't
        // fight ShootRoutine's thrust lerp (which also writes localPosition).
        // Owner-only, same as the RMB-drop check below, so this has zero
        // networking effect — every other peer still only sees the grab-time
        // OnObserversGrab placement plus whatever ShootRoutine broadcasts.
        if (!_isShooting)
        {
            transform.localPosition = _handPositionOffset;
            transform.localRotation = Quaternion.Euler(_handRotationOffset);
        }

        // RMB drops — mirrors Throwable's RMB-for-secondary-action pattern,
        // since LMB is now dedicated to shooting instead of dropping.
        if (Mouse.current != null && Mouse.current.rightButton.wasPressedThisFrame)
            ServerDropRpc(_holdingPlayer);
    }

    private void RequestShoot(PlayerObject player)
    {
        if (_isShooting || Time.time < _cooldownUntil) return;
        _cooldownUntil = Time.time + _shotCooldown; // debounce immediately, before the RPC round-trip
        ServerShoot(player);
    }

    [ServerRpc(RequireOwnership = false)]
    private void ServerShoot(PlayerObject player)
    {
        if (!_isHeld || _holdingPlayer != player) return;
        ObserversShoot();
    }

    [ObserversRpc]
    private void ObserversShoot()
    {
        StartCoroutine(ShootRoutine());
    }

    private IEnumerator ShootRoutine()
    {
        _isShooting = true;
        _hasHitThisShot = false;
        _serverStruckThisShot = false;

        // _thrustDirection is authored relative to the cue's own forward, but
        // transform.localPosition is relative to the hand socket's axes — rotate
        // by _handRotationOffset so the thrust still tracks the cue's actual
        // (corrected) pointing direction instead of the socket's raw axes.
        // _handPositionOffset is the rest pose (where OnObserversGrab placed it),
        // so the thrust animates from/to that instead of the socket's raw zero.
        Vector3 localThrustDir = Quaternion.Euler(_handRotationOffset) * _thrustDirection.normalized;
        Vector3 restLocalPos = _handPositionOffset;
        Vector3 forwardLocalPos = restLocalPos + localThrustDir * _thrustDistance;

        // localThrustDir is expressed in the parent's (hand socket's) local
        // axes, same as transform.localPosition is. Convert it to a world-space
        // direction the same way — via the parent's rotation — and cache it for
        // OnTipTriggerEnter. This is the direction the tip is actually moving
        // through the world during this shot; it has nothing to do with
        // transform.forward (the cue's raw local Z axis), which is why using
        // transform.forward for the strike was sending the ball off at an
        // unrelated angle.
        _shotWorldDirection = transform.parent != null
            ? transform.parent.TransformDirection(localThrustDir)
            : transform.TransformDirection(localThrustDir);

        float t = 0f;
        while (t < _thrustDuration)
        {
            t += Time.deltaTime;
            transform.localPosition = Vector3.Lerp(restLocalPos, forwardLocalPos, t / _thrustDuration);
            yield return null;
        }
        transform.localPosition = forwardLocalPos;

        t = 0f;
        while (t < _returnDuration)
        {
            t += Time.deltaTime;
            transform.localPosition = Vector3.Lerp(forwardLocalPos, restLocalPos, t / _returnDuration);
            yield return null;
        }
        transform.localPosition = restLocalPos;

        _isShooting = false;
    }

    // Called by CueTip when its trigger collider overlaps something during the thrust.
    public void OnTipTriggerEnter(Collider other)
    {
        if (!_isShooting || _hasHitThisShot) return;

        CueBall cueBall = other.GetComponentInParent<CueBall>();
        if (cueBall == null) return; // not the cue ball — the cue can only ever affect the cue ball, everything else is silently ignored

        _hasHitThisShot = true;

        // Local cosmetic prediction only — every peer (including the host's
        // own client-side view of itself) still plays the strike sound and,
        // if it's NOT the server, still applies a local Strike() so there's
        // zero visible delay for whoever's watching. The host IS the server,
        // so it skips this and waits for the authoritative path below to
        // avoid applying the impulse twice.
        if (!IsServerInitialized)
        {
            cueBall.Strike(_shotWorldDirection, _hitForce);
        }
        cueBall.PlayCueStrikeSound();

        // Only the player actually holding the cue gets to report a hit —
        // this runs on every observer's machine, so without this check a
        // bystander client could also fire the RPC. ServerReportHit
        // double-checks the same thing server-side (never trust the client
        // alone), this just avoids a wasted RPC from everyone else.
        if (_holdingPlayer != null && _holdingPlayer.IsOwner)
        {
            ServerReportHit(_holdingPlayer, cueBall);
        }
    }

    // Authoritative strike. Only the connection that owns _holdingPlayer can
    // report a hit for this shot, and it's applied at most once — the
    // server's own ShootRoutine() (it runs one too, like every observer)
    // already computed _shotWorldDirection from the SAME thrust the shooter
    // saw, so this doesn't trust any client-supplied physics values, only
    // "did the legitimate shooter's tip touch the ball."
    [ServerRpc(RequireOwnership = false)]
    private void ServerReportHit(PlayerObject player, CueBall cueBall)
    {
        if (!_isHeld || _holdingPlayer != player) return; // not the current legitimate shooter
        if (cueBall == null || _serverStruckThisShot) return; // no ball reference, or already applied this shot
        _serverStruckThisShot = true;

        cueBall.Strike(_shotWorldDirection, _hitForce);
        cueBall.PlayCueStrikeSound();
    }

    // Guard against a runaway thrust coroutine if the cue gets dropped mid-shot.
    // base.OnObserversDrop now resolves to SettlingGrabbable's version (see
    // the class comment above) — it starts the raycast-and-settle routine
    // instead of flipping the Rigidbody back to dynamic, which is the actual
    // fix for the floor-clipping glitch. Nothing else in this override needed
    // to change for that; it was already just deferring to base.
    protected override void OnObserversDrop(NetworkObject playerNetObj)
    {
        StopAllCoroutines();
        _isShooting = false;
        base.OnObserversDrop(playerNetObj);
    }

    // Same as Grabbable.OnObserversGrab, except it applies _handRotationOffset
    // and _handPositionOffset instead of hardcoding Quaternion.identity /
    // Vector3.zero — that's the one block that differs. Duplicated rather than
    // calling base + patching afterward, since the base version sets these
    // itself; there's no hook to intercept just those lines. StopAllCoroutines()
    // is added here (SettlingGrabbable's own OnObserversGrab does the same)
    // to cancel a grab-mid-settle race if the cue is picked back up while
    // still lerping down from a previous drop — this override fully replaces
    // SettlingGrabbable.OnObserversGrab rather than calling it, same as it
    // always fully replaced Grabbable's version, so that guard has to be
    // repeated here.
    protected override void OnObserversGrab(NetworkObject playerNetObj)
    {
        StopAllCoroutines();

        PlayerObject player = playerNetObj.GetComponent<PlayerObject>();
        if (player == null) return;

        _holdingPlayer = player;
        _rigidbody.isKinematic = true;

        var nt = GetComponent<NetworkTransform>();
        if (nt != null) nt.enabled = false;

        Transform handSocket = player.HandSocket;
        if (handSocket != null)
        {
            transform.SetParent(handSocket);
            transform.localPosition = _handPositionOffset;
            transform.localRotation = Quaternion.Euler(_handRotationOffset);
        }

        player.SetHeldObject(this);
    }
}