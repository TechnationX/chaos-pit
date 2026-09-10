// PlayerSpawnConfig.cs

using UnityEngine;

[CreateAssetMenu(fileName = "PlayerSpawnConfig", menuName = "Lobby/Player Spawn Config")]
public class PlayerSpawnConfig : ScriptableObject
{
    [Header("Player Prefab")]
    public GameObject PlayerPrefab;
}
