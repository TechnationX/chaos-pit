// LobbyCustomizationSession.cs
using System;

// Hand-off between CustomizationStation (Lobby) and CharacterCreatorContinueHandler
// (CharacterCreator scene, loaded on top of the Lobby). While a session is active,
// the creator's Continue button returns the saved appearance here instead of
// loading the Main Menu. Static because the two live in different scenes and only
// one local player can ever be customizing at a time.
public static class LobbyCustomizationSession
{
    private static Action<byte[]> _onSaved;

    public static bool IsActive => _onSaved != null;

    public static void Begin(Action<byte[]> onSaved) => _onSaved = onSaved;

    // Clears without calling back. CustomizationStation calls this on close and in
    // OnDestroy, so a session can't leak into a later Main Menu visit to the creator
    // (e.g. if the session ends while the creator is open).
    public static void End() => _onSaved = null;

    public static void Complete(byte[] packedAppearance)
    {
        Action<byte[]> callback = _onSaved;
        _onSaved = null;
        callback?.Invoke(packedAppearance);
    }
}
