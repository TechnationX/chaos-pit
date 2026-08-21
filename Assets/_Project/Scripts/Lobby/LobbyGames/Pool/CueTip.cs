// CueTip
using UnityEngine;

/// <summary>
/// Attach to a child GameObject positioned at the business end of a Cue
/// model, with a small trigger Collider on it (e.g. a SphereCollider sized
/// roughly to the tip). Unity only calls OnTriggerEnter on the GameObject
/// that actually owns the Collider, not on parent objects, so this exists
/// purely to relay the event up to the owning Cue component.
///
/// Owner is assigned automatically by Cue.Awake() if this is wired into the
/// Cue's _tip field in the Inspector — no manual setup needed here beyond
/// adding the Collider and positioning this GameObject at the tip.
/// </summary>
[RequireComponent(typeof(Collider))]
public class CueTip : MonoBehaviour
{
    [HideInInspector] public Cue Owner;

    private void Awake()
    {
        Collider col = GetComponent<Collider>();
        if (col != null)
            col.isTrigger = true;
    }

    private void OnTriggerEnter(Collider other)
    {
        Owner?.OnTipTriggerEnter(other);
    }
}
