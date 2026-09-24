// StageSpeaker.cs
using UnityEngine;

/// <summary>
/// Plain position marker + falloff config for a stage's speaker prop — NOT a
/// NetworkBehaviour, since it holds no synced state of its own, just where
/// sound coming off this stage should appear to originate from and how far
/// it should carry. InstrumentInteractable reads this (assigned onto it by
/// LobbySpawner right after the instrument spawns — see
/// StageSetupInstance.SpeakerAnchor and SpawnStageSetupsRoutine/
/// SwitchStageActRoutine) instead of playing sound from the instrument's own
/// Transform, so every instrument on this stage carries through the same
/// speaker regardless of where the instrument itself happens to sit.
///
/// Kept as its own component (rather than just a bare Transform reference)
/// so the falloff range lives in one place per physical speaker prop and can
/// be tuned per stage. Phase 3 (mic broadcast positioning) is expected to
/// read this same component too, once the mic's Vivox audio gets an
/// in-world positional source instead of broadcasting uniformly to the whole
/// channel — that's why this isn't folded directly into StageSetupInstance
/// as a couple of float fields.
/// </summary>
public class StageSpeaker : MonoBehaviour
{
    [Tooltip("3D falloff range for instrument audio played through this speaker (see AudioManager.PlaySFXAtPosition). Kept roughly matched to the Vivox 'stage-broadcast' channel's audible/conversational distances (see VoiceChatManager) so instrument sound and mic voice carry about the same distance from this stage.")]
    [SerializeField] private float _minDistance = 8f;
    [SerializeField] private float _maxDistance = 40f;

    public Vector3 Position => transform.position;
    public float MinDistance => _minDistance;
    public float MaxDistance => _maxDistance;
}
