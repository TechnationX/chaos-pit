// PoolBall
using FishNet.Object;
using UnityEngine;

/// <summary>
/// Marker + reset handler for a numbered ball — the counterpart to
/// CueBall.cs, but for every ball that ISN'T the cue ball. PoolPocket uses
/// GetComponentInParent to tell the two apart: something with a CueBall
/// resets to the table spawn anchor, something with a PoolBall resets to a
/// named slot on the holding rack instead.
///
/// _holdingSlotName should match a child object's name on the holding rack
/// prefab (e.g. "BallHolder_8") — resolved via Transform.Find at reset time,
/// same pattern PoolSetupConfig already uses to place balls on the triangle
/// rack at spawn. Set one value per numbered-ball prefab in the Inspector.
///
/// FULL SERVER AUTHORITY: only the server's own copy of this ball actually
/// simulates physics. Every other peer's Rigidbody is kept permanently
/// kinematic (see OnStartServer/OnStartClient below) and just displays
/// whatever position/rotation NetworkTransform pushes out — NetworkTransform
/// is now server-authoritative (_clientAuthoritative: 0 on the prefab), not
/// client-authoritative. This replaces the old "every peer independently
/// simulates the same input" approach, which let each peer's local physics
/// drift out of sync with everyone else's (different peers could see a
/// break scatter the balls differently, or end up in different final
/// positions). A kinematic Rigidbody never fires OnCollisionEnter against
/// another kinematic Rigidbody, which is why the ball-vs-ball impact SFX
/// below had to move from "every peer detects and plays its own copy
/// locally" to "server detects and broadcasts." See CueBall.cs's matching
/// comment — the two scripts use the identical pattern.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class PoolBall : NetworkBehaviour
{
    [Tooltip("Name of this ball's slot on the holding rack (e.g. \"BallHolder_8\") — must match a child Transform's name on the holding rack prefab exactly.")]
    [SerializeField] private string _holdingSlotName;
    public string HoldingSlotName => _holdingSlotName;

    [Tooltip("Disable gravity once parked on the holding rack, so it stays exactly where it's placed with no floor/support needed under the slot. Rigidbody stays non-kinematic the whole time — it can still be pushed by other physics interactions, it just won't fall.")]
    [SerializeField] private bool _disableGravityWhenParked = true;

    // Impact SFX — see CueBall.cs's identical block for the full reasoning
    // (rail/table hits deliberately skipped, lower-InstanceID side plays to
    // avoid a double-fire on one hit). No cue-strike clips here — the cue
    // can only ever hit the cue ball (see Cue.OnTipTriggerEnter), never a
    // numbered ball directly.
    [Header("Impact SFX")]
    [Tooltip("Played when this ball collides with another ball (cue or numbered). Rail/table hits are deliberately skipped — no dedicated sound for those.")]
    [SerializeField] private AudioClip[] _ballImpactClips;
    [SerializeField] private float _minBallImpactVelocity = 1f;
    [SerializeField] private float _ballImpactCooldown = 0.1f;

    private float _lastBallImpactTime = -999f;

    private Rigidbody _rigidbody;

    // Captured once at spawn, same pattern Grabbable uses for
    // _originalPosition/_originalRotation (see its ForceReset()). This is
    // the "provision" for a future full-table reset button: it lets that
    // button just call ServerResetToSpawn() on every ball without having to
    // separately track or look up where each one originally started.
    private Vector3 _spawnPosition;
    private Quaternion _spawnRotation;

    private void Awake()
    {
        _rigidbody = GetComponent<Rigidbody>();
        _spawnPosition = transform.position;
        _spawnRotation = transform.rotation;
    }

    // Kinematic baseline for full server authority — see the class comment.
    // Set here rather than in OnStartNetwork(): FishNet's own doc comment on
    // IsServerInitialized says it "is set true right before server start
    // callbacks," meaning it isn't reliable yet inside OnStartNetwork()
    // (which runs before OnStartServer). Splitting across OnStartServer/
    // OnStartClient avoids that entirely — on host both fire, but
    // OnStartServer always runs first, so OnStartClient's IsServerInitialized
    // check below already sees true and correctly leaves a host's ball
    // non-kinematic. LobbySpawner's ServerSetLocked(true) call right after
    // spawn (racking) still wins either way — see ObserversSetLocked below.
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

    // See CueBall.ServerResetTo for the full explanation — same pattern,
    // called directly from PoolPocket's already-server-side code.
    //
    // restoreGravity distinguishes the two reset situations this ball can be
    // in: parking on the holding rack (a pocketed ball, gravity off so it
    // floats at its slot with no support needed underneath — the existing
    // PoolPocket call site, which omits this and gets the default false) vs.
    // a full table reset putting it back into live play (gravity must come
    // back on, or it'll just float in place at the spawn position forever).
    [Server]
    public void ServerResetTo(Vector3 position, Quaternion rotation, bool restoreGravity = false)
    {
        ObserversResetTo(position, rotation, restoreGravity);
    }

    // Convenience wrapper for the future reset-table button: puts this ball
    // back exactly where it started with gravity back on, no need for
    // whatever calls this to know or track the original position itself.
    [Server]
    public void ServerResetToSpawn()
    {
        ServerResetTo(_spawnPosition, _spawnRotation, restoreGravity: true);
    }

    [ObserversRpc]
    private void ObserversResetTo(Vector3 position, Quaternion rotation, bool restoreGravity)
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

        if (restoreGravity)
            _rigidbody.useGravity = true;
        else if (_disableGravityWhenParked)
            _rigidbody.useGravity = false;
    }

    // Locks/unlocks this ball as completely fixed — kinematic, so nothing
    // can move it: not gravity, not a collision, not the cue, nothing.
    // Used to hold racked balls perfectly in formation from the moment they
    // spawn (LobbySpawner calls ServerSetLocked(true) right after spawning
    // each one onto the triangle rack) until PoolRackGrabbable unlocks them
    // the instant the rack is grabbed. Same Server/ObserversRpc broadcast
    // pattern as ServerResetTo — every client needs to agree on this
    // together, not decide independently.
    [Server]
    public void ServerSetLocked(bool locked)
    {
        ObserversSetLocked(locked);
    }

    [ObserversRpc]
    private void ObserversSetLocked(bool locked)
    {
        // Full server authority: unlocking must only let the SERVER's own
        // copy of this ball start simulating — every other peer's Rigidbody
        // has to stay kinematic regardless of the locked flag, or it goes
        // back to independently resimulating this ball's physics the moment
        // the rack is grabbed (see the class comment). Locking still applies
        // everywhere unconditionally, since a locked ball must never move on
        // any peer.
        _rigidbody.isKinematic = locked || !IsServerInitialized;
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
}