// AudioManager.cs
// Place in: Assets/_Project/Scripts/Audio/
// Owns music playback, SFX playback, and volume settings.
// Singleton. Lives in Bootstrap scene.

using UnityEngine;

public class AudioManager : SingletonBehaviour<AudioManager>
{
    [Header("Audio Sources")]
    [SerializeField] private AudioSource musicSource;
    [SerializeField] private AudioSource sfxSource;

    [Header("Default Volume")]
    [Range(0f, 1f)][SerializeField] private float defaultMusicVolume = 0.5f;
    [Range(0f, 1f)][SerializeField] private float defaultSFXVolume = 1f;

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

        SetMusicVolume(defaultMusicVolume);
        SetSFXVolume(defaultSFXVolume);

        Debug.Log("[AudioManager] Initialized.");
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
}