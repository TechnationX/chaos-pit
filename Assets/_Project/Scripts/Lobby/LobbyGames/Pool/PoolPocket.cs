// PoolPocket
using FishNet.Object;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Attach to a trigger collider at each pocket opening on the table (six of
/// these). The Collider needs isTrigger on (this forces it in Awake as a
/// safety net, same convention as CueTip.cs); no Rigidbody needed — a static
/// trigger works fine, same as CueTip's.
///
/// This is also a NetworkBehaviour, so each pocket needs a NetworkObject
/// component too. These are scene-placed, not runtime-spawned, so just add
/// NetworkObject directly on each pocket GameObject in the scene — FishNet
/// registers scene NetworkObjects automatically, no LobbySpawner wiring
/// needed for these.
///
/// Detects which kind of ball fell in and resets it after a short delay:
///  - CueBall  -> _cueBallResetAnchor (the table's original cue ball spawn point).
///  - PoolBall -> its named slot on the holding rack (see PoolBall.HoldingSlotName).
///    The holding rack is resolved via LobbySpawner.Instance.GetHoldingRack()
///    rather than an Inspector field, because it's spawned at runtime by
///    LobbySpawner (see PoolSetupInstance.HoldingRackPrefab) — nothing exists
///    to drag into an Inspector slot at design time. _holdingRackOverride
///    below is an optional manual fallback if you ever want to point a
///    pocket at a different holding rack than LobbySpawner's.
///
/// OnTriggerEnter fires on every peer (physics runs locally everywhere in
/// this pool system, same as the cue-ball hit detection), but only the
/// server acts on it — see the IsServerInitialized gate below — and the
/// actual reset is broadcast via each ball's ServerResetTo/ObserversRpc.
/// This keeps "a ball just vanished and reappeared" a single agreed-on event
/// across every client instead of something each client's own (possibly
/// slightly drifted) local physics decides independently.
/// </summary>
[RequireComponent(typeof(Collider))]
public class PoolPocket : NetworkBehaviour
{
    [Header("Reset Targets")]
    [Tooltip("Where the cue ball goes when it falls in ANY pocket — drag in the pool table's original cue ball spawn anchor (the same Transform used to place it at setup time).")]
    [SerializeField] private Transform _cueBallResetAnchor;
    [Tooltip("Optional manual override — leave empty to use LobbySpawner's spawned holding rack automatically. Only set this if you need a specific pocket to target a different holding rack.")]
    [SerializeField] private Transform _holdingRackOverride;

    [Header("Timing")]
    [SerializeField] private float _resetDelay = 1.5f;

    // Guards against double-queuing a reset if a ball briefly exits and
    // re-enters the trigger volume while already waiting on its delay.
    private readonly HashSet<Component> _pendingReset = new HashSet<Component>();

    private void Awake()
    {
        Collider col = GetComponent<Collider>();
        if (col != null)
            col.isTrigger = true;
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!IsServerInitialized) return; // physics runs on every peer, but only the server acts on it

        CueBall cueBall = other.GetComponentInParent<CueBall>();
        if (cueBall != null)
        {
            if (_pendingReset.Contains(cueBall)) return;
            _pendingReset.Add(cueBall);
            StartCoroutine(ResetCueBallAfterDelay(cueBall));
            return;
        }

        PoolBall poolBall = other.GetComponentInParent<PoolBall>();
        if (poolBall != null)
        {
            if (_pendingReset.Contains(poolBall)) return;
            _pendingReset.Add(poolBall);
            StartCoroutine(ResetPoolBallAfterDelay(poolBall));
        }
    }

    private IEnumerator ResetCueBallAfterDelay(CueBall cueBall)
    {
        yield return new WaitForSeconds(_resetDelay);
        _pendingReset.Remove(cueBall);

        if (_cueBallResetAnchor == null)
        {
            Debug.LogWarning("[PoolPocket] No cue ball reset anchor assigned.");
            yield break;
        }

        cueBall.ServerResetTo(_cueBallResetAnchor.position, _cueBallResetAnchor.rotation);
    }

    private IEnumerator ResetPoolBallAfterDelay(PoolBall poolBall)
    {
        yield return new WaitForSeconds(_resetDelay);
        _pendingReset.Remove(poolBall);

        Transform holdingRack = _holdingRackOverride != null ? _holdingRackOverride : LobbySpawner.Instance?.GetHoldingRack();
        if (holdingRack == null)
        {
            Debug.LogWarning("[PoolPocket] No holding rack assigned or spawned yet.");
            yield break;
        }

        Transform slot = holdingRack.Find(poolBall.HoldingSlotName);
        if (slot == null)
        {
            Debug.LogWarning($"[PoolPocket] Holding rack has no slot named \"{poolBall.HoldingSlotName}\" for {poolBall.name}.");
            yield break;
        }

        poolBall.ServerResetTo(slot.position, slot.rotation);
    }
}