// GameRoomSession.cs

using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class GameRoomSession
{
    public int StationIndex;
    public GameRoomState State = GameRoomState.Idle;
    public List<PlayerObject> Players = new List<PlayerObject>();
    public PlayerObject HostPlayer;
    public MiniGameRegistryEntry SelectedGame;
    public MiniGameController ActiveController;
    public Coroutine CountdownCoroutine;
    public float CountdownDuration;
    public MiniGameRegistry Registry;

    // Private is the default mode for a fresh room (replaces the old "no
    // game selected yet" idle state entirely). Selecting a real minigame
    // clears this; selecting "Private" again from the cycle restores it.
    public bool IsPrivateMode = true;

    // Only meaningful while IsPrivateMode is true. Toggled instantly by
    // RequestStartCountdown while in Private mode — no countdown, no scene
    // load, players stay put. Blocks new joins while true.
    public bool IsLocked = false;

    public GameRoomSession(int stationIndex, float countdownDuration)
    {
        StationIndex = stationIndex;
        CountdownDuration = countdownDuration;
        Registry = Resources.Load<MiniGameRegistry>("MiniGameRegistry");

        if (Registry == null)
            Debug.LogWarning($"[GameRoomSession] MiniGameRegistry not found in Resources folder.");
    }
}