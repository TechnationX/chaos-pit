// AudioManager.cs

using UnityEngine;

public class AudioManager : SingletonBehaviour<AudioManager>
{
    [Header("Audio Sources")]
    [SerializeField] private AudioSource musicSource;
    [SerializeField] private AudioSource sfxSource;

    [Header("Default Volume")]
    [Range(0f, 1f)][SerializeField] private float defaultMusicVolume = 0.5f;
    [Range(0f, 1f)][SerializeField] private float defaultSFXVolume = 1f;

    [Header("Mixer Routing")]
    [SerializeField] private UnityEngine.Audio.AudioMixerGroup musicMixerGroup;
    [SerializeField] private UnityEngine.Audio.AudioMixerGroup sfxMixerGroup;

    [Header("Default SFX")]
    [SerializeField] private AudioClip defaultClickClip;
    public AudioClip DefaultClickClip => defaultClickClip;

    [Header("Positional SFX")]
    [Tooltip("Default 3D falloff range for PlaySFXAtPosition — full volume within Min Distance, silent beyond Max Distance. Override per-call (e.g. a celebration stinger that should carry further than a small prop clatter) via the method's optional parameters.")]
    [SerializeField] private float _defaultSFXMinDistance = 2f;
    [SerializeField] private float _defaultSFXMaxDistance = 15f;

    protected override void Awake()
    {
        base.Awake();

        // Auto-create AudioSources if not assigned in Inspector
        if (musicSource == null)
        {
            musicSource = gameObject.AddComponent<AudioSource>();
            musicSource.loop = true;
            musicSource.playOnAwake = false;
        }

        if (sfxSource == null)
        {
            sfxSource = gameObject.AddComponent<AudioSource>();
            sfxSource.loop = false;
            sfxSource.playOnAwake = false;
        }

        musicSource.outputAudioMixerGroup = musicMixerGroup;
        sfxSource.outputAudioMixerGroup = sfxMixerGroup;

        SetMusicVolume(defaultMusicVolume);
        SetSFXVolume(defaultSFXVolume);

        // Debug.Log("[AudioManager] Initialized.");
    }

    // Play a music track. Stops current track first.
    public void PlayMusic(AudioClip clip)
    {
        if (clip == null) return;

        if (musicSource.clip == clip && musicSource.isPlaying) return;

        musicSource.clip = clip;
        musicSource.Play();
    }

    public void StopMusic()
    {
        musicSource.Stop();
    }

    // Fire and forget SFX
    public void PlaySFX(AudioClip clip)
    {
        if (clip == null) return;
        sfxSource.PlayOneShot(clip);
    }

    public void SetMusicVolume(float volume)
    {
        musicSource.volume = Mathf.Clamp01(volume);
    }

    public void SetSFXVolume(float volume)
    {
        sfxSource.volume = Mathf.Clamp01(volume);
    }

    // Slight pitch variance so repeated impacts (e.g. objects hitting the ground) don't sound identical
    public void PlaySFXVaried(AudioClip clip, float pitchRange = 0.05f)
    {
        if (clip == null) return;
        sfxSource.pitch = 1f + Random.Range(-pitchRange, pitchRange);
        sfxSource.PlayOneShot(clip);
        sfxSource.pitch = 1f; // reset for other SFX calls that don't want variance
    }

    // PlaySFX/PlaySFXVaried above both route through the ONE shared
    // sfxSource — fine for global/UI cues (menu clicks, etc.), but wrong for
    // anything with a physical location in the world: every player hears
    // those at identical volume no matter how far away they are, which is
    // exactly the "hearing things in other rooms" problem for impact sounds.
    // This spins up a short-lived, properly 3D AudioSource at the given
    // position instead (real distance falloff), then destroys itself once
    // the clip finishes. minDistance/maxDistance default to the class-wide
    // falloff range above; pass explicit values to override per-call (e.g. a
    // louder/further-carrying event like a celebration stinger).
    public void PlaySFXAtPosition(AudioClip clip, Vector3 position, float pitchRange = 0f, float volume = 1f, float? minDistance = null, float? maxDistance = null)
    {
        if (clip == null) return;

        var go = new GameObject("SFX (positional)");
        go.transform.position = position;

        var source = go.AddComponent<AudioSource>();
        source.clip = clip;
        source.outputAudioMixerGroup = sfxMixerGroup;
        source.spatialBlend = 1f; // 3D
        source.rolloffMode = AudioRolloffMode.Logarithmic;
        source.minDistance = minDistance ?? _defaultSFXMinDistance;
        source.maxDistance = maxDistance ?? _defaultSFXMaxDistance;
        source.pitch = 1f + Random.Range(-pitchRange, pitchRange);
        source.volume = Mathf.Clamp01(volume) * defaultSFXVolume;
        source.Play();

        Destroy(go, clip.length / Mathf.Max(0.01f, source.pitch));
    }
}