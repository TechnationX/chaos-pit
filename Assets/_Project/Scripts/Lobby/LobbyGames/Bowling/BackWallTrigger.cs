// BackWallTrigger.cs
using UnityEngine;

/// Attach to the trigger volume placed at the back of the lane. Forwards
/// "a ball reached the back" to the BowlingBall that entered it —
/// BowlingBall.ServerRegisterRollComplete() only acts on the server.
public class BackWallTrigger : MonoBehaviour
{
    private void OnTriggerEnter(Collider other)
    {
        BowlingBall ball = other.GetComponentInParent<BowlingBall>();
        if (ball != null)
            ball.ServerRegisterRollComplete();
    }
}
