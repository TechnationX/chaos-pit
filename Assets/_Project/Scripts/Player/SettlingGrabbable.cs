// SettlingGrabbable
using FishNet.Component.Transforming;
using FishNet.Object;
using System.Collections;
using UnityEngine;

/// <summary>
/// A Grabbable for props whose collider MUST stay non-convex (a concave
/// MeshCollider that actually matches the model — e.g. the pool rack's open
/// triangle pocket) and therefore can never be handed to PhysX as a dynamic
/// Rigidbody: Unity only allows non-convex MeshColliders on kinematic or
/// static bodies. Base Grabbable.OnObserversDrop() unconditionally flips
/// isKinematic back to false on drop, which is exactly what broke the rack
/// twice — first it fell through the floor (invalid non-convex + dynamic
/// combo), then making the collider convex to "fix" that filled in the open
/// pocket so balls rested on top of the hull instead of inside it.
///
/// This class sidesteps the conflict entirely instead of trading one bug for
/// the other: the Rigidbody never leaves kinematic, so PhysX's convex
/// requirement never applies, so the collider can stay exactly as
/// detailed/concave as the model actually is. On drop, instead of letting
/// real physics resolve the fall, a short scripted routine raycasts straight
/// down and lerps the object to rest on whatever it finds — looks like a
/// controlled fall, not a physics tumble/bounce. If you need real dynamic
/// physics (tumbling, bouncing off things while falling) for a prop, this is
/// the wrong base class — use plain Grabbable instead.
///
/// Built for the pool rack, but nothing here is pool-specific — reusable for
/// any other prop with a concave collision mesh that needs to rest AND be
/// droppable.
/// </summary>
public class SettlingGrabbable : Grabbable
{
    [Header("Settle On Drop")]
    [Tooltip("Layers the downward raycast can land on (table surface, floor, etc.). Exclude whatever layer this object's own colliders and other held/grabbable props are on, so the raycast doesn't hit itself or something it's resting near.")]
    [SerializeField] private LayerMask _settleGroundLayer = ~0;
    [Tooltip("How far below the drop point to search for ground. If nothing is found within this distance, the object falls straight down by this same distance as a fallback rather than staying stuck in midair.")]
    [SerializeField] private float _settleMaxDropDistance = 2f;
    [Tooltip("Vertical offset added above the raycast hit point, so the collider's bottom lands on the surface instead of the object's pivot embedding into it. Tune to roughly the distance from this object's pivot to the bottom of its collider.")]
    [SerializeField] private float _settleRestOffset = 0f;
    [Tooltip("How long the settle lerp takes, in seconds.")]
    [SerializeField] private float _settleDuration = 0.3f;

    // Force kinematic immediately on spawn, rather than only when grabbed.
    // OnObserversGrab already sets isKinematic = true, but that's reactive —
    // it does nothing about the object's state before it's ever touched. If
    // the Rigidbody's "Is Kinematic" checkbox happens to be unchecked in the
    // Inspector (prefab default drifted, got reset while editing, etc.), the
    // object would sit there as a dynamic Rigidbody with a concave
    // MeshCollider from the moment the scene loads — which is exactly the
    // "Concave Mesh Colliders are not supported on dynamic Rigidbody
    // GameObjects" warning. This makes the kinematic requirement a guarantee
    // instead of something that depends on remembering to check a box.
    protected override void Awake()
    {
        base.Awake();
        if (_rigidbody != null)
            _rigidbody.isKinematic = true;
    }

    // Cancel a grab-mid-settle race: if the object is picked back up while
    // still lerping down from a previous drop, stop the coroutine so it
    // doesn't keep fighting the hand-socket parenting set by OnObserversGrab.
    protected override void OnObserversGrab(NetworkObject playerNetObj)
    {
        StopAllCoroutines();
        base.OnObserversGrab(playerNetObj);
    }

    // Same as Grabbable.OnObserversDrop, except it never sets isKinematic =
    // false — that's the one line removed — and starts SettleRoutine() in
    // its place. Duplicated rather than calling base + patching afterward
    // since the base version sets isKinematic itself; there's no hook to
    // intercept just that one line.
    protected override void OnObserversDrop(NetworkObject playerNetObj)
    {
        PlayerObject player = playerNetObj.GetComponent<PlayerObject>();
        if (player == null) return;

        _holdingPlayer = player;

        var nt = GetComponent<NetworkTransform>();
        if (nt != null) nt.enabled = true;

        transform.SetParent(null);
        player.SetHeldObject(null);

        StopAllCoroutines();
        StartCoroutine(SettleRoutine());
    }

    private IEnumerator SettleRoutine()
    {
        Vector3 startPos = transform.position;
        Vector3 targetPos;

        if (Physics.Raycast(startPos, Vector3.down, out RaycastHit hit, _settleMaxDropDistance, _settleGroundLayer, QueryTriggerInteraction.Ignore))
            targetPos = hit.point + Vector3.up * _settleRestOffset;
        else
            targetPos = startPos + Vector3.down * _settleMaxDropDistance; // fallback so it never just hangs in midair

        float t = 0f;
        while (t < _settleDuration)
        {
            t += Time.deltaTime;
            transform.position = Vector3.Lerp(startPos, targetPos, t / _settleDuration);
            yield return null;
        }
        transform.position = targetPos;
    }
}
