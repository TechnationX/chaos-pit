// BowlingBall.cs
using FishNet.Object;
using UnityEngine;

/// <summary>
/// A bowling ball — grab/throw behavior is entirely inherited from
/// Throwable. This adds lane-specific behavior only: detecting the roll is
/// over (via BackWallTrigger) and returning to its holder anchor after a
/// short delay.
///
/// Reset uses the same [Server] + [ObserversRpc] broadcast pattern as
/// PoolBall.ServerResetTo — see Grabbable.ForceReset()'s comment for why:
/// relying on NetworkTransform alone to replicate a big instant position
/// jump isn't reliable, so the new transform is explicitly broadcast to
/// every client instead of just set on the server's own copy.
/// </summary>
public class BowlingBall : Throwable
{
    [Header("Bowling")]
    // Not [SerializeField] — both of these are scene references (an anchor
    // Transform and a BowlingGameController, both placed in the scene), and
    // a prefab asset isn't allowed to store a reference to a scene object.
    // LobbySpawner assigns both in code, right after Instantiate(), via
    // SetHolderAnchor()/SetGameController() below — see SpawnBowlingLaneRoutine.
    private Transform _holderAnchor;
    private BowlingGameController _gameController;
    [Tooltip("Seconds after reaching the back of the lane before the ball resets to its holder.")]
    [SerializeField] private float _returnDelay = 1.5f;

    private float _returnTimer;
    private bool _pendingReturn;

    // Must be "protected override" calling base.Update(), not a plain
    // private Update() — a plain override here would hide
    // Grabbable/Throwable's Update() (held-state polling, throw input) the
    // same way Cue.cs's old Update() did before that was fixed.
    protected override void Update()
    {
        base.Update();

        if (!IsServerInitialized || !_pendingReturn) return;

        // A player can grab the ball after ServerRegisterRollComplete()
        // started the countdown but before it fires — IsHeld was only
        // checked at that start, not continuously. Cancel the pending
        // return rather than yanking the ball back later: without this,
        // ServerResetToHolder() would try to clear velocity on a Rigidbody
        // that's now kinematic (held), hitting the same "Setting velocity of
        // a kinematic body" warning fixed on Pin.ServerSetStanding().
        if (IsHeld)
        {
            _pendingReturn = false;
            return;
        }

        _returnTimer -= Time.deltaTime;
        if (_returnTimer <= 0f)
        {
            _pendingReturn = false;
            ServerResetToHolder();
        }
    }

    /// Called once by LobbySpawner right after this ball is instantiated —
    /// see SpawnBowlingLaneRoutine. Can't be wired up via the Inspector on
    /// the prefab itself (see the field comment above), so it's assigned
    /// here in code instead.
    public void SetHolderAnchor(Transform holderAnchor)
    {
        _holderAnchor = holderAnchor;
    }

    /// Called once by LobbySpawner right after this ball is instantiated —
    /// same reasoning as SetHolderAnchor() above. Leave the lane's
    /// GameController unassigned in LobbySpawner's Inspector to keep that
    /// lane pure free-play with no scoring.
    public void SetGameController(BowlingGameController gameController)
    {
        _gameController = gameController;
    }

    /// Called by BackWallTrigger when this ball reaches the back of the
    /// lane. Ignored if a return is already pending, or the ball is
    /// currently held (a player retrieving it shouldn't have it yanked back
    /// to the holder mid-carry).
    public void ServerRegisterRollComplete()
    {
        if (!IsServerInitialized || _pendingReturn || IsHeld) return;
        _pendingReturn = true;
        _returnTimer = _returnDelay;
        _gameController?.ServerOnRollComplete(this);
    }

    [Server]
    private void ServerResetToHolder()
    {
        if (_holderAnchor == null)
        {
            Debug.LogWarning($"[BowlingBall] {name} has no HolderAnchor assigned — can't reset.");
            return;
        }
        ObserversResetToHolder(_holderAnchor.position, _holderAnchor.rotation);
    }

    [ObserversRpc]
    private void ObserversResetToHolder(Vector3 position, Quaternion rotation)
    {
        _rigidbody.linearVelocity = Vector3.zero;
        _rigidbody.angularVelocity = Vector3.zero;
        transform.SetPositionAndRotation(position, rotation);
    }
}
