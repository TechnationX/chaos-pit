// PlayerSpawnConfig.cs

using UnityEngine;

[CreateAssetMenu(fileName = "PlayerSpawnConfig", menuName = "Lobby/Player Spawn Config")]
public class PlayerSpawnConfig : ScriptableObject
{
    [System.Serializable]
    public class SpawnPoint
    {
        public string Label;
        public Vector3 Position;
        public Vector3 Rotation;
    }

    [Header("Player Prefab")]
    public GameObject PlayerPrefab;

    [Header("Player Spawn Points")]
    public SpawnPoint[] SpawnPoints = new SpawnPoint[]
    {
        new SpawnPoint { Label = "SpawnA", Position = new Vector3( 3, 0,  3), Rotation = Vector3.zero },
        new SpawnPoint { Label = "SpawnB", Position = new Vector3(-3, 0,  3), Rotation = Vector3.zero },
        new SpawnPoint { Label = "SpawnC", Position = new Vector3( 3, 0, -3), Rotation = Vector3.zero },
        new SpawnPoint { Label = "SpawnD", Position = new Vector3(-3, 0, -3), Rotation = Vector3.zero }
    };
}