// GameRoomState.cs

public enum GameRoomState
{
    Idle,           // no players, station available
    Waiting,        // players queued, host selecting game
    Countdown,      // countdown running before scene load
    Loading,        // scene loading in progress
    InProgress,     // game running
    Results,        // results screen showing
    Returning       // players returning to lobby
}