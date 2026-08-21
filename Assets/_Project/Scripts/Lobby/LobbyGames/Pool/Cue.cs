// Cue
using FishNet.Component.Transforming;
using FishNet.Object;
using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// A pool cue. Extends Grabbable but changes what LMB/RMB do while held:
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
/// coroutine and independently detects/applies the hit on its own local
/// physics. This is "good enough" for a casual party game, same as
/// Throwable's AddForce-on-every-client approach — it isn't frame-perfect
/// deterministic, but the cue ball's NetworkTransform (kept permanently
/// enabled, never toggled) periodically corrects any drift from the server.
/// </summary>
public class Cue : Grabbable
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

    private void Update()
    {
        if (!_isHeld || _holdingPlayer == null) return;
        if (!_holdingPlayer.IsOwner) return;

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
        // Use the direction the tip is actually travelling this shot, not
        // transform.forward — see _shotWorldDirection's declaration for why.
        cueBall.Strike(_shotWorldDirection, _hitForce);
    }

    // Guard against a runaway thrust coroutine if the cue gets dropped mid-shot.
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
    // itself; there's no hook to intercept just those lines.
    protected override void OnObserversGrab(NetworkObject playerNetObj)
    {
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