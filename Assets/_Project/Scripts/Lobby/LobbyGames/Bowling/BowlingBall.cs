// BowlingBall.cs
using FishNet.Object;
using UnityEngine;

/// <summary>
/// A bowling ball — grab/throw behavior is entirely inherited from
/// Throwable. This adds lane-specific behavior only: detecting the roll is
/// over (via BackWallTrigger) and returning to its holder anchor after a
/// short delay.
///
/// Reset uses the same [Server] + [ObserversRpc] broadcast pattern as
/// PoolBall.ServerResetTo — see Grabbable.ForceReset()'s comment for why:
/// relying on NetworkTransform alone to replicate a big instant position
/// jump isn't reliable, so the new transform is explicitly broadcast to
/// every client instead of just set on the server's own copy.
/// </summary>
public class BowlingBall : Throwable
{
    [Header("Bowling")]
    // Not [SerializeField] — both of these are scene references (an anchor
    // Transform and a BowlingGameController, both placed in the scene), and
    // a prefab asset isn't allowed to store a reference to a scene object.
    // LobbySpawner assigns both in code, right after Instantiate(), via
    // SetHolderAnchor()/SetGameController() below — see SpawnBowlingLaneRoutine.
    private Transform _holderAnchor;
    private BowlingGameController _gameController;
    [Tooltip("Seconds after reaching the back of the lane before the ball resets to its holder.")]
    [SerializeField] private float _returnDelay = 1.5f;

    private float _returnTimer;
    private bool _pendingReturn;

    // Rolling SFX needs its OWN local AudioSource, not AudioManager's
    // shared singleton — AudioManager plays everything through one
    // non-positional sfxSource via PlayOneShot, fine for one-shot impacts
    // (see Grabbable/Pin), but wrong for a continuous sound that must
    // follow this specific ball and fade with its own speed. Purely local
    // — no server gate, no networking — every peer plays this off their
    // own simulated copy of this ball's Rigidbody, same "everyone
    // simulates independently" convention Grabbable's impact SFX already
    // relies on.
    [Header("Rolling SFX")]
    [SerializeField] private AudioClip _rollClip;
    [Tooltip("Below this speed (m/s) the rolling sound stops entirely — treated as stopped/settling, not rolling.")]
    [SerializeField] private float _minRollSpeed = 0.3f;
    [Tooltip("Speed (m/s) at which the rolling sound reaches full volume/pitch — anything faster is clamped to the same max.")]
    [SerializeField] private float _maxRollSpeedForVolume = 6f;
    [SerializeField] private float _rollVolume = 0.6f;
    [Tooltip("How much pitch rises at max speed, e.g. 0.15 means pitch reaches 1.15x when rolling flat-out.")]
    [SerializeField] private float _rollPitchVariance = 0.15f;
    [Tooltip("Seconds of no surface contact still counted as \"grounded\" — smooths a small bounce on landing (a brief hop between the lane and the ball) so the rolling sound doesn't flicker on/off, without masking a real, sustained airborne stretch like the throw arc.")]
    [SerializeField] private float _groundedGraceDuration = 0.15f;
    [Tooltip("3D falloff range for the rolling sound — full volume within Min Distance, silent beyond Max Distance. Was left at Unity's AudioSource defaults (min 1 / max 500) before, which is effectively audible everywhere; tightened so a lane's rolling ball doesn't carry into other rooms.")]
    [SerializeField] private float _rollMinDistance = 1f;
    [SerializeField] private float _rollMaxDistance = 15f;

    private AudioSource _rollSource;
    private float _lastGroundedTime = -999f;

    protected override void Awake()
    {
        base.Awake();

        _rollSource = gameObject.AddComponent<AudioSource>();
        _rollSource.clip = _rollClip;
        _rollSource.loop = true;
        _rollSource.playOnAwake = false;
        _rollSource.spatialBlend = 1f; // 3D — follows the ball, unlike AudioManager's shared 2D sfxSource
        _rollSource.rolloffMode = AudioRolloffMode.Logarithmic;
        _rollSource.minDistance = _rollMinDistance;
        _rollSource.maxDistance = _rollMaxDistance;
        _rollSource.volume = 0f;
    }

    // Must be "protected override" calling base.Update(), not a plain
    // private Update() — a plain override here would hide
    // Grabbable/Throwable's Update() (held-state polling, throw input) the
    // same way Cue.cs's old Update() did before that was fixed.
    protected override void Update()
    {
        base.Update();

        UpdateRollingSfx();

        if (!IsServerInitialized || !_pendingReturn) return;

        // A player can grab the ball after ServerRegisterRollComplete()
        // started the countdown but before it fires — IsHeld was only
        // checked at that start, not continuously. Cancel the pending
        // return rather than yanking the ball back later: without this,
        // ServerResetToHolder() would try to clear velocity on a Rigidbody
        // that's now kinematic (held), hitting the same "Setting velocity of
        // a kinematic body" warning fixed on Pin.ServerSetStanding().
        if (IsHeld)
        {
            _pendingReturn = false;
            return;
        }

        _returnTimer -= Time.deltaTime;
        if (_returnTimer <= 0f)
        {
            _pendingReturn = false;
            ServerResetToHolder();
        }
    }

    /// Called once by LobbySpawner right after this ball is instantiated —
    /// see SpawnBowlingLaneRoutine. Can't be wired up via the Inspector on
    /// the prefab itself (see the field comment above), so it's assigned
    /// here in code instead.
    public void SetHolderAnchor(Transform holderAnchor)
    {
        _holderAnchor = holderAnchor;
    }

    /// Called once by LobbySpawner right after this ball is instantiated —
    /// same reasoning as SetHolderAnchor() above. Leave the lane's
    /// GameController unassigned in LobbySpawner's Inspector to keep that
    /// lane pure free-play with no scoring.
    public void SetGameController(BowlingGameController gameController)
    {
        _gameController = gameController;
    }

    /// Called by BackWallTrigger when this ball reaches the back of the
    /// lane. Ignored if a return is already pending, or the ball is
    /// currently held (a player retrieving it shouldn't have it yanked back
    /// to the holder mid-carry).
    public void ServerRegisterRollComplete()
    {
        if (!IsServerInitialized || _pendingReturn || IsHeld) return;
        _pendingReturn = true;
        _returnTimer = _returnDelay;
        _gameController?.ServerOnRollComplete(this);
    }

    [Server]
    private void ServerResetToHolder()
    {
        if (_holderAnchor == null)
        {
            Debug.LogWarning($"[BowlingBall] {name} has no HolderAnchor assigned — can't reset.");
            return;
        }
        ObserversResetToHolder(_holderAnchor.position, _holderAnchor.rotation);
    }

    [ObserversRpc]
    private void ObserversResetToHolder(Vector3 position, Quaternion rotation)
    {
        _rigidbody.linearVelocity = Vector3.zero;
        _rigidbody.angularVelocity = Vector3.zero;
        transform.SetPositionAndRotation(position, rotation);
    }

    // Grabbable's inherited Impact Clips (see its OnCollisionEnter) is for
    // ground/wall hits — pins already get their own dedicated sound from
    // Pin.cs's _ballImpactClips, so skip calling into the base behavior
    // when the other side of the collision is a pin. Without this, every
    // pin the ball knocks over would layer BOTH the pin's ball-impact clip
    // and this ball's own generic impact clip at the same moment.
    //
    // Also tracks surface contact for the rolling sound below (see
    // _surfaceContactCount) — speed alone isn't enough to know the ball is
    // actually rolling, since it's still moving fast (just under gravity,
    // not friction) during the throw arc before it ever lands.
    protected override void OnCollisionEnter(Collision collision)
    {
        _surfaceContactCount++;

        if (collision.collider.GetComponentInParent<Pin>() != null) return;
        base.OnCollisionEnter(collision);
    }

    // Counter rather than a single bool — the ball can be touching more
    // than one collider at once (lane floor AND a pin mid-strike), and a
    // plain bool set by whichever collision happens to exit last would
    // wrongly flip to "not grounded" while still resting on the lane.
    // Not a perfect substitute for a true "is this specific contact the
    // lane surface" check, but good enough to distinguish "airborne" from
    // "touching literally anything" — which is really what "in contact
    // with the ground" needs to rule out here (the throw arc).
    private int _surfaceContactCount;

    private void OnCollisionExit(Collision collision)
    {
        _surfaceContactCount = Mathf.Max(0, _surfaceContactCount - 1);
    }

    // Volume/pitch scale with the ball's own current speed rather than
    // being a flat on/off — a slow final roll into the pins sounds
    // different from the ball leaving the player's hand at full force.
    // IsHeld is checked directly rather than trusting velocity alone: a
    // held ball's Rigidbody is kinematic (see Grabbable.OnObserversGrab),
    // so its velocity reads zero anyway, but checking explicitly is
    // cheaper than relying on that incidentally being true.
    private void UpdateRollingSfx()
    {
        if (_rollClip == null || _rollSource == null) return;

        if (_surfaceContactCount > 0) _lastGroundedTime = Time.time;

        // Grace window rather than the raw contact flag — a brief mid-roll
        // hop (contact count touches 0 for a frame or two) still reads as
        // grounded, but a real airborne stretch like the throw arc quickly
        // exceeds the window and correctly silences the sound.
        bool isGrounded = Time.time - _lastGroundedTime <= _groundedGraceDuration;
        float speed = (IsHeld || !isGrounded) ? 0f : _rigidbody.linearVelocity.magnitude;

        if (speed < _minRollSpeed)
        {
            if (_rollSource.isPlaying) _rollSource.Stop();
            return;
        }

        float t = Mathf.Clamp01(speed / _maxRollSpeedForVolume);
        _rollSource.volume = t * _rollVolume;
        _rollSource.pitch = 1f + t * _rollPitchVariance;

        if (!_rollSource.isPlaying) _rollSource.Play();
    }
}
