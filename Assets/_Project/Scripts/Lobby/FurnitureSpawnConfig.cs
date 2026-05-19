// FurnitureSpawnConfig.cs

using UnityEngine;

[CreateAssetMenu(fileName = "FurnitureSpawnConfig", menuName = "Lobby/Furniture Spawn Config")]
public class FurnitureSpawnConfig : ScriptableObject
{
    [System.Serializable]
    public class FurnitureEntry
    {
        public string Label;
        public GameObject Prefab;
        public Vector3 Position;
        public Vector3 Rotation;
        public Vector3 Scale = Vector3.one;
    }

    [Header("Furniture Entries")]
    public FurnitureEntry[] Entries;
}