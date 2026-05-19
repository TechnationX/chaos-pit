// EditorBootstrap.cs

using UnityEngine;
using FishNet;

public class EditorBootstrap : MonoBehaviour
{
#if UNITY_EDITOR
    private void Start()
    {
        //Debug.Log("EditorBootstrap Start fired");
        InstanceFinder.ServerManager.StartConnection();
        InstanceFinder.ClientManager.StartConnection();
    }
#endif
}