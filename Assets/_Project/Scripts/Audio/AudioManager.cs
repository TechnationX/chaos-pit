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
}