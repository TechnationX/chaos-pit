// DisconnectReason.cs
// Attach to: SessionManager GameObject in Bootstrap scene

using UnityEngine;
using UnityEngine.SceneManagement;

public class DisconnectReason : SingletonBehaviour<DisconnectReason>
{
    public string PendingMessage { get; private set; } = string.Empty;

    public void Set(string message)
    {
        PendingMessage = message;
    }

    public string Consume()
    {
        string msg = PendingMessage;
        PendingMessage = string.Empty;
        return msg;
    }
}