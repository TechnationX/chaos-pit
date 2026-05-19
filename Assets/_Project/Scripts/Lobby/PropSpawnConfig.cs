// PropSpawnConfig.cs

using UnityEngine;

[CreateAssetMenu(fileName = "PropSpawnConfig", menuName = "Lobby/Prop Spawn Config")]
public class PropSpawnConfig : ScriptableObject
{
    public enum PropType { Grabbable, Throwable }

    [System.Serializable]
    public class PropEntry
    {
        public string Label;
        public GameObject Prefab;
        public PropType Type;
        public Vector3 Position;
        public Vector3 Rotation;
        public Vector3 Scale = Vector3.one;
    }

    [Header("Prop Entries")]
    public PropEntry[] Entries;
}