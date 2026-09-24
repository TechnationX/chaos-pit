// PoolRackGrabbable
using FishNet.Object;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Pool-specific extension of SettlingGrabbable, for the triangle rack.
/// Replaces the plain SettlingGrabbable component on the rack prefab.
///
/// Every ball spawned onto this rack is locked completely fixed (Rigidbody
/// kinematic — immune to gravity, collisions, the cue, everything) from the
/// moment it spawns, via PoolBall.ServerSetLocked(true) — see
/// LobbySpawner.SpawnPoolSetups(), which calls that right after spawning
/// each racked ball and then hands this component the list via
/// SetRackedBalls(). The instant this rack is grabbed, every ball on that
/// list gets unlocked (ServerSetLocked(false)) and becomes a normal dynamic
/// physics object again, free to scatter — same moment the player starts
/// lifting the rack off, not a separate/later trigger.
///
/// This component has no way to discover which balls belong to it on its
/// own: they're spawned as siblings under LobbySpawner's _propParent, not
/// as children of the rack, so SetRackedBalls() has to be called externally.
/// </summary>
public class PoolRackGrabbable : SettlingGrabbable
{
    private readonly List<PoolBall> _rackedBalls = new List<PoolBall>();

    public void SetRackedBalls(List<PoolBall> balls)
    {
        _rackedBalls.Clear();
        if (balls != null)
            _rackedBalls.AddRange(balls);
    }

    // LMB (OnInteract) no longer drops the rack — matches Cue.cs's
    // RMB-for-drop convention instead of the base Grabbable default. Grab
    // (the !_isHeld path) still goes through base.OnInteract unchanged; only
    // the "already held, LMB pressed" drop path is suppressed here, with the
    // RMB poll in Update() below doing the actual drop.
    public override void OnInteract(PlayerObject player)
    {
        if (_isHeld) return;
        base.OnInteract(player);
    }

    // Same RMB-drop pattern as Cue.cs's Update() override: raw mouse poll,
    // bypassing OnInteract entirely, owner-only so remote observers don't
    // also fire the drop RPC for input that isn't theirs.
    protected override void Update()
    {
        base.Update();

        if (!_isHeld || _holdingPlayer == null) return;
        if (!_holdingPlayer.IsOwner) return;

        if (Mouse.current != null && Mouse.current.rightButton.wasPressedThisFrame)
            ServerDropRpc(_holdingPlayer);
    }

    protected override void OnObserversGrab(NetworkObject playerNetObj)
    {
        base.OnObserversGrab(playerNetObj);

        // Only the server actually unlocks the balls — same IsServerInitialized
        // gate PoolPocket uses. OnObserversGrab fires on every peer (that's
        // how the visual grab/parenting syncs to everyone), but "make these
        // balls movable again" is a discrete state change that should happen
        // once, authoritatively, not be decided independently by each client.
        if (!IsServerInitialized) return;

        foreach (PoolBall ball in _rackedBalls)
        {
            if (ball != null)
                ball.ServerSetLocked(false);
        }
        _rackedBalls.Clear();
    }
}
