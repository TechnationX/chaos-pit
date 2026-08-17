// LobbyMusicTrigger.cs
using UnityEngine;

public class SceneMusicTrigger : MonoBehaviour
{
    [SerializeField] private AudioClip _sceneMusic;

    private void Start()
    {
        AudioManager.Instance?.PlayMusic(_sceneMusic);
    }
}