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
    private Rigidbody _rigidbody;

    private void Awake()
    {
        _rigidbody = GetComponent<Rigidbody>();
    }

    public void Strike(Vector3 direction, float force)
    {
        _rigidbody.AddForce(direction.normalized * force, ForceMode.Impulse);
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