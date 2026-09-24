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
/// Strike() itself runs as a plain local AddForce, called both by Cue.cs's
/// local cosmetic-prediction path (every peer, purely visual/audio, applies
/// zero real force server-side) and by Cue.ServerReportHit's authoritative
/// path (server only, applies the real impulse) — see Cue.cs's class
/// comment for the full explanation of that split. Strike() doesn't need to
/// know which caller it is; only the Rigidbody's kinematic state below
/// decides whether the AddForce actually does anything.
///
/// FULL SERVER AUTHORITY: only the server's own copy of this ball actually
/// simulates physics. Every other peer's Rigidbody is kept permanently
/// kinematic (see OnStartServer/OnStartClient below) and just displays
/// whatever position/rotation NetworkTransform pushes out — NetworkTransform
/// is now server-authoritative (_clientAuthoritative: 0 on the prefab), not
/// client-authoritative. This replaces the old "every peer independently
/// simulates the same input" approach, which let each peer's local physics
/// drift out of sync with everyone else's after a strike (different peers
/// could see the ball end up in different places / bounce differently off
/// rails and other balls). A kinematic Rigidbody never fires
/// OnCollisionEnter against another kinematic Rigidbody, which is why the
/// ball-vs-ball impact SFX below had to move from "every peer detects and
/// plays its own copy locally" to "server detects and broadcasts."
///
/// Now a NetworkBehaviour (was a plain MonoBehaviour) so it can broadcast a
/// server-authoritative reset via ServerResetTo/ObserversResetTo — see
/// PoolPocket.cs, which calls ServerResetTo when the cue ball falls in a
/// pocket.
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

    // Kinematic baseline for full server authority — see the class comment.
    // Set here rather than in OnStartNetwork(): FishNet's own doc comment on
    // IsServerInitialized says it "is set true right before server start
    // callbacks," meaning it isn't reliable yet inside OnStartNetwork()
    // (which runs before OnStartServer). Splitting across OnStartServer/
    // OnStartClient avoids that entirely — on host both fire, but
    // OnStartServer always runs first, so OnStartClient's IsServerInitialized
    // check below already sees true and correctly leaves a host's ball
    // non-kinematic.
    public override void OnStartServer()
    {
        base.OnStartServer();
        _rigidbody.isKinematic = false;
    }

    public override void OnStartClient()
    {
        base.OnStartClient();
        if (!IsServerInitialized)
            _rigidbody.isKinematic = true;
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
    //
    // Server-only now — see the class comment. Non-server peers' balls are
    // kinematic, and Unity never raises OnCollisionEnter between two
    // kinematic Rigidbodies, so this would simply stop firing on clients
    // anyway; detecting on the server and broadcasting which clip to play
    // is what keeps the sound audible for everyone instead of going silent
    // on non-host players.
    private void OnCollisionEnter(Collision collision)
    {
        if (!IsServerInitialized) return;

        GameObject other = ResolveOtherBall(collision.collider);
        if (other == null) return; // rail/table felt — no dedicated sound for that

        if (gameObject.GetInstanceID() > other.GetInstanceID()) return;

        if (Time.time - _lastBallImpactTime < _ballImpactCooldown) return;
        float impactSpeed = collision.relativeVelocity.magnitude;
        if (impactSpeed < _minBallImpactVelocity) return;
        if (_ballImpactClips == null || _ballImpactClips.Length == 0) return;

        _lastBallImpactTime = Time.time;
        int clipIndex = Random.Range(0, _ballImpactClips.Length);
        ObserversPlayBallImpactSfx(clipIndex, transform.position);
    }

    // Broadcasts the clip the server already chose so every peer (including
    // the server's own host client) plays the identical sound at the same
    // moment, replacing the old "every peer detects and picks its own random
    // clip independently" approach that stopped working once balls went
    // kinematic on non-server peers.
    [ObserversRpc]
    private void ObserversPlayBallImpactSfx(int clipIndex, Vector3 position)
    {
        if (_ballImpactClips == null || clipIndex < 0 || clipIndex >= _ballImpactClips.Length) return;
        AudioManager.Instance?.PlaySFXAtPosition(_ballImpactClips[clipIndex], position, 0.05f);
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
        // Only clear velocity where the Rigidbody is actually non-kinematic
        // (the server) — Unity logs a warning when velocity is set on a
        // kinematic body, which every non-server peer's ball now is (see
        // the class comment).
        if (IsServerInitialized)
        {
            _rigidbody.linearVelocity = Vector3.zero;
            _rigidbody.angularVelocity = Vector3.zero;
        }
        transform.SetPositionAndRotation(position, rotation);
    }
}