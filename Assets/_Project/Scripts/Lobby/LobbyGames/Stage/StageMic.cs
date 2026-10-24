// StageMic.cs
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Plain position marker + pickup range for a spawned stage mic prop — NOT a
/// NetworkBehaviour (no synced state of its own, same reasoning as
/// StageSpeaker). Lives on the Mic prefab LobbySpawner spawns per act
/// (currently always Mic_A, see StageSetupConfig.MicPrefab), registering
/// itself in a static Active list on enable/disable so PlayerObject can find
/// it without any extra wiring — exactly mirroring StageSpeaker's pattern,
/// just for the transmit side instead of the listen side.
///
/// There's no pickup/hold interaction — per explicit instruction, the mic
/// passively picks up any player's voice within PickupRange of wherever it's
/// currently spawned, the way a real stage mic picks up nearby sound. See
/// PlayerObject.UpdateStageMicPickup / VoiceChatManager.SetMicPickupRange for
/// the other half of this: a player inside range gets their transmission
/// mode switched to TransmissionMode.All (lobby-proximity AND stage-broadcast
/// simultaneously, so being near the mic adds reach rather than replacing
/// normal nearby talking), and back to lobby-proximity only once they leave.
/// </summary>
public class StageMic : MonoBehaviour
{
    [Tooltip("How close a player needs to be to this mic for their voice to be picked up and broadcast through the stage PA (stage-broadcast). Short range by design — this is the mic picking up nearby sound, not a long-range transmitter; StageSpeaker.VoiceCutoffDistance controls how far the broadcast itself carries once picked up.")]
    [SerializeField] private float _pickupRange = 3f;

    public Vector3 Position => transform.position;
    public float PickupRange => _pickupRange;

    // Every enabled StageMic in the scene, across every stage — mirrors
    // StageSpeaker.Active. In practice there's only ever one active mic at a
    // time today (LobbySpawner spawns one mic per act and despawns the
    // previous one on switch), but this stays a list for the same reason
    // StageSpeaker's is: no assumption baked in about exactly one stage/mic
    // ever existing.
    public static readonly List<StageMic> Active = new List<StageMic>();

    private void OnEnable() => Active.Add(this);
    private void OnDisable() => Active.Remove(this);
}
