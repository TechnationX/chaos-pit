// StageSpeaker.cs
using System.Collections.Generic;
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

    [Tooltip("Hard cutoff distance for the Vivox 'stage-broadcast' voice channel (mic audio). A player this far or farther from every StageSpeaker is taken out of the channel entirely (true silence — PlayerObject leaves the channel outright rather than just turning the volume down) and put back in once they cross back inside it. Kept separate from MinDistance/MaxDistance above so instrument SFX falloff and voice range can be tuned independently.")]
    [SerializeField] private float _voiceCutoffDistance = 40f;

    public Vector3 Position => transform.position;
    public float MinDistance => _minDistance;
    public float MaxDistance => _maxDistance;
    public float VoiceCutoffDistance => _voiceCutoffDistance;

    // Every enabled StageSpeaker in the scene, across every stage — lets
    // PlayerObject find the nearest one each frame for the stage-broadcast
    // hard-cutoff check (see VoiceChatManager.SetStageBroadcastRange)
    // without needing a reference threaded in from LobbySpawner's per-stage
    // SpeakerAnchors list, which only exists for instrument SFX routing.
    public static readonly List<StageSpeaker> Active = new List<StageSpeaker>();

    private void OnEnable() => Active.Add(this);
    private void OnDisable() => Active.Remove(this);
}
