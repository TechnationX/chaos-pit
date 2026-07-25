// LocalPlayerLoadout.cs
using UnityEngine;

public class LocalPlayerLoadout : MonoBehaviour
{
    public static LocalPlayerLoadout Instance { get; private set; }

    // Packed bytes built in CharacterCreator scene, read in Lobby scene on spawn
    public byte[] PendingLoadoutData { get; private set; }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    public void SetLoadout(byte[] packedData)
    {
        PendingLoadoutData = packedData;
    }
}