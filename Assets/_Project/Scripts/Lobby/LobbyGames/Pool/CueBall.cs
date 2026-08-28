// CueBall
using FishNet.Object;
using UnityEngine;

/// <summary>
/// Marker + hit handler for the cue ball specifically. Cue.OnTipTriggerEnter
/// only reacts to colliders that resolve to a CueBall component via
/// GetComponentInParent — this is what makes "the cue stick can only hit
/// the cue ball" true. Numbered balls and everything else don't have this
/// component, so the cue's tip trigger silently ignores them; no Physics
/// Layer Matrix changes are required for that filtering to work, though
/// you can still add a dedicated layer as an extra safety net if you want.
///
/// Runs identically on every client — Cue's shoot routine executes via
/// ObserversRpc, so each client's own physics independently detects contact
/// and calls Strike() on its own local Rigidbody. Same "everyone simulates
/// the same input" pattern Throwable already uses for its AddForce-on-throw.
/// Keep this ball's NetworkTransform permanently enabled (unlike Grabbable's
/// held objects, never toggle it off) so the server's simulation
/// periodically corrects any client-side physics drift.
///
/// Now a NetworkBehaviour (was a plain MonoBehaviour) so it can broadcast a
/// server-authoritative reset via ServerResetTo/ObserversResetTo — see
/// PoolPocket.cs, which calls ServerResetTo when the cue ball falls in a
/// pocket. Strike() itself is unchanged: still a plain local AddForce, still
/// runs independently on every client exactly as before.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class CueBall : NetworkBehaviour
{
    // Impact SFX — no dedicated sound for hitting the rail/table felt (see
    // OnCollisionEnter below), only cue-strike and ball-vs-ball. Lives here
    // rather than on Cue.cs since there's only one CueBall prefab but
    // several cue color variants — putting the clips here avoids
    // duplicating the same clip assignment across every cue prefab.
    [Header("Impact SFX")]
    [Tooltip("Played when the cue strikes this ball — see Cue.OnTipTriggerEnter, which calls PlayCueStrikeSound() directly.")]
    [SerializeField] private AudioClip[] _cueStrikeClips;
    [Tooltip("Played when this ball collides with another ball (cue or numbered). Rail/table hits are deliberately skipped — no dedicated sound for those.")]
    [SerializeField] private AudioClip[] _ballImpactClips;
    [SerializeField] private float _minBallImpactVelocity = 1f;
    [SerializeField] private float _ballImpactCooldown = 0.1f;

    private float _lastBallImpactTime = -999f;

    private Rigidbody _rigidbody;

    private void Awake()
    {
        _rigidbody = GetComponent<Rigidbody>();
    }

    public void Strike(Vector3 direction, float force)
    {
        _rigidbody.AddForce(direction.normalized * force, ForceMode.Impulse);
    }

    // Called directly by Cue.OnTipTriggerEnter alongside Strike() — a single,
    // unambiguous call site (that method already debounces to one hit per
    // shot via _hasHitThisShot), so no cooldown/velocity gate needed here
    // the way the ball-vs-ball case below needs one.
    public void PlayCueStrikeSound()
    {
        if (_cueStrikeClips == null || _cueStrikeClips.Length == 0) return;
        AudioClip clip = _cueStrikeClips[Random.Range(0, _cueStrikeClips.Length)];
        AudioManager.Instance?.PlaySFXAtPosition(clip, transform.position, 0.05f);
    }

    // Ball-vs-ball impact only — anything that isn't a CueBall/PoolBall on
    // the other side (rail, table felt) is silently skipped, which is what
    // keeps sidewall/rail hits silent without needing a separate exclusion
    // list. Both balls in a collision fire OnCollisionEnter independently;
    // only the lower-InstanceID side actually plays the clip so a single
    // hit doesn't stack two copies of the same sound on top of each other.
    private void OnCollisionEnter(Collision collision)
    {
        GameObject other = ResolveOtherBall(collision.collider);
        if (other == null) return; // rail/table felt — no dedicated sound for that

        if (gameObject.GetInstanceID() > other.GetInstanceID()) return;

        if (Time.time - _lastBallImpactTime < _ballImpactCooldown) return;
        float impactSpeed = collision.relativeVelocity.magnitude;
        if (impactSpeed < _minBallImpactVelocity) return;
        if (_ballImpactClips == null || _ballImpactClips.Length == 0) return;

        _lastBallImpactTime = Time.time;
        AudioClip clip = _ballImpactClips[Random.Range(0, _ballImpactClips.Length)];
        AudioManager.Instance?.PlaySFXAtPosition(clip, transform.position, 0.05f);
    }

    private static GameObject ResolveOtherBall(Collider other)
    {
        CueBall cueBall = other.GetComponentInParent<CueBall>();
        if (cueBall != null) return cueBall.gameObject;
        PoolBall poolBall = other.GetComponentInParent<PoolBall>();
        if (poolBall != null) return poolBall.gameObject;
        return null;
    }

    // Called directly by PoolPocket's server-side trigger logic (not itself
    // an RPC — the pocket is already running server-only code when it calls
    // this, via the IsServerInitialized gate in PoolPocket.OnTriggerEnter).
    // Broadcasts the actual teleport to every client so they all reset at
    // the same moment instead of each client's own physics deciding
    // independently when the ball entered the pocket.
    [Server]
    public void ServerResetTo(Vector3 position, Quaternion rotation)
    {
        ObserversResetTo(position, rotation);
    }

    [ObserversRpc]
    private void ObserversResetTo(Vector3 position, Quaternion rotation)
    {
        _rigidbody.linearVelocity = Vector3.zero;
        _rigidbody.angularVelocity = Vector3.zero;
        transform.SetPositionAndRotation(position, rotation);
    }
}