// PinFallTrigger.cs
using UnityEngine;

/// Attach to the trigger volume placed over the pin deck (the "pin fallen"
/// sensor from setup). Forwards "this pin's collider left the sensor band"
/// to whichever Pin left it — Pin.ReportPossibleFall() decides whether
/// that's a real fall and only acts when running on the server.
public class PinFallTrigger : MonoBehaviour
{
    private void OnTriggerExit(Collider other)
    {
        Pin pin = other.GetComponentInParent<Pin>();
        if (pin != null)
            pin.ReportPossibleFall();
    }
}
