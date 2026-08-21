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
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class PoolBall : NetworkBehaviour
{
    [Tooltip("Name of this ball's slot on the holding rack (e.g. \"BallHolder_8\") — must match a child Transform's name on the holding rack prefab exactly.")]
    [SerializeField] private string _holdingSlotName;
    public string HoldingSlotName => _holdingSlotName;

    [Tooltip("Disable gravity once parked on the holding rack, so it stays exactly where it's placed with no floor/support needed under the slot. Rigidbody stays non-kinematic the whole time — it can still be pushed by other physics interactions, it just won't fall.")]
    [SerializeField] private bool _disableGravityWhenParked = true;

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
        _rigidbody.linearVelocity = Vector3.zero;
        _rigidbody.angularVelocity = Vector3.zero;
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
        _rigidbody.isKinematic = locked;
    }
}