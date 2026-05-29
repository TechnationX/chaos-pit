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

    public GameRoomSession(int stationIndex, float countdownDuration)
    {
        StationIndex = stationIndex;
        CountdownDuration = countdownDuration;
        Registry = Resources.Load<MiniGameRegistry>("MiniGameRegistry");

        if (Registry == null)
            Debug.LogWarning($"[GameRoomSession] MiniGameRegistry not found in Resources folder.");
    }
}