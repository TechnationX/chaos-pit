// InstrumentInteractable.cs
using System.Collections.Generic;
using FishNet.Object;
using UnityEngine;

/// <summary>
/// Sits on a spawned instrument prefab (piano now, guitar once that prefab
/// exists) and plays a canned sound clip through the stage's speakers when
/// interacted with — same IInteractable pattern as PoolResetButton/Grabbable,
/// not a UI element.
///
/// Deliberately split into two RPC hops (ServerPlayInstrumentSound ->
/// ObserversPlayInstrumentSound) rather than just picking and playing a clip
/// locally on interact, even though today's "press to play a random clip"
/// behavior doesn't strictly need the server round-trip: this is the
/// upgrade path the user asked to keep open. A future note-by-note version
/// (real playable interaction instead of one canned clip) only needs to
/// change WHAT ServerPlayInstrumentSound decides to play (e.g. take a note
/// index parameter instead of picking randomly) — the routing half
/// (ObserversPlayInstrumentSound -> AudioManager.PlaySFXAtPosition sourced
/// from _speakerAnchors) stays untouched either way.
///
/// _speakerAnchors are scene references (the stage's StageSpeaker props —
/// e.g. two left, two right, one center) that this prefab can't hold at
/// design time — LobbySpawner assigns them in code right after Instantiate,
/// same as BowlingBall.SetHolderAnchor(). Sound plays from EVERY assigned
/// speaker at once rather than picking just one — a listener naturally hears
/// it loudest from whichever speaker(s) are physically closest to them
/// (ordinary 3D distance falloff per AudioSource), which is a reasonable
/// stand-in for real per-speaker routing without needing any of that
/// complexity.
/// </summary>
public class InstrumentInteractable : NetworkBehaviour, IInteractable
{
    [SerializeField] private string _promptLabel = "Play";
    [Tooltip("One of these is picked at random each time this instrument is played. Assign per-instrument in the Inspector (e.g. a few short piano riffs vs. a guitar strum).")]
    [SerializeField] private AudioClip[] _triggerClips;
    [Range(0f, 1f)]
    [SerializeField] private float _volume = 1f;
    [Tooltip("Pitch variance so repeated presses of the same clip don't sound identical — same convention as Grabbable's impact SFX. Applied independently per speaker below, so the copies playing simultaneously don't sound perfectly phase-locked either.")]
    [SerializeField] private float _pitchRange = 0.05f;

    private List<StageSpeaker> _speakerAnchors = new List<StageSpeaker>();

    public string PromptLabel => _promptLabel;

    // Assigned by LobbySpawner right after this instrument is spawned — see
    // StageSetupInstance.SpeakerAnchors and SpawnStageSetupsRoutine/
    // SwitchStageActRoutine. Empty/null means this instance hasn't been
    // wired up yet (or was instantiated outside the normal stage-spawn path)
    // — falls back to playing from the instrument's own position below.
    public void SetSpeakerAnchors(List<StageSpeaker> speakers)
    {
        _speakerAnchors = speakers ?? new List<StageSpeaker>();
    }

    public void OnInteract(PlayerObject player)
    {
        // No IsServerInitialized gate — same reasoning as PoolResetButton:
        // this must be callable by any client's local interaction, not just
        // whichever client happens to also be host.
        ServerPlayInstrumentSound();
    }

    [ServerRpc(RequireOwnership = false)]
    private void ServerPlayInstrumentSound()
    {
        if (_triggerClips == null || _triggerClips.Length == 0)
        {
            Debug.LogWarning($"[InstrumentInteractable] {name} has no trigger clips assigned.");
            return;
        }

        int clipIndex = Random.Range(0, _triggerClips.Length);
        ObserversPlayInstrumentSound(clipIndex);
    }

    [ObserversRpc]
    private void ObserversPlayInstrumentSound(int clipIndex)
    {
        if (_triggerClips == null || clipIndex < 0 || clipIndex >= _triggerClips.Length) return;

        AudioClip clip = _triggerClips[clipIndex];
        if (clip == null || AudioManager.Instance == null) return;

        if (_speakerAnchors == null || _speakerAnchors.Count == 0)
        {
            // No speakers wired up (or this stage has none assigned) — same
            // single-call fallback as before, from the instrument's own
            // position.
            AudioManager.Instance.PlaySFXAtPosition(clip, transform.position, _pitchRange, _volume, null, null);
            return;
        }

        foreach (StageSpeaker speaker in _speakerAnchors)
        {
            if (speaker == null) continue;
            AudioManager.Instance.PlaySFXAtPosition(clip, speaker.Position, _pitchRange, _volume, speaker.MinDistance, speaker.MaxDistance);
        }
    }
}
