// DebugCollision.cs

using UnityEngine;

public class DebugCollision : MonoBehaviour
{
    private void OnCollisionEnter(Collision collision)
    {
        Debug.Log($"Collision detected with: {collision.gameObject.name} on layer: {collision.gameObject.layer}");
    }

    private void OnTriggerEnter(Collider other)
    {
        Debug.Log($"Trigger detected with: {other.gameObject.name} on layer: {other.gameObject.layer}");
    }
}