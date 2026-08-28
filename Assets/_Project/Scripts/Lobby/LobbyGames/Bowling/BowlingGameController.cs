// BowlingGameController.cs
using System.Collections;
using System.Collections.Generic;
using System.Text;
using FishNet.Object;
using FishNet.Object.Synchronizing;
using UnityEngine;

/// <summary>
/// Server-authoritative scoring, turn order, and frame progression for one
/// bowling lane. Phase 3 added a player roster and per-frame turn rotation
/// on top of Phase 2's single-scorer math. Phase 4 adds a live world-space
/// scoreboard: ScoreboardSnapshot (backed by _scoreboardSnapshot, a single
/// SyncVar<string>) is a compact serialization of the whole lane's state —
/// see that field's comment for the exact format — polled and parsed by
/// BowlingScoreboardText the same way BowlingPanelText polls FrameCount/
/// PinCount. Console logging (LogScoreboard/LogFinalStandings) stays in
/// place alongside it for debugging.
///
/// Turn order is real-bowling style: a player throws every roll of their
/// current frame (including last-frame bonus rolls) before the turn passes
/// to the next player — not a per-roll rotation.
///
/// Join/leave/start/config are driven by BowlingPanelButton — a panel of
/// physical buttons, one per action. Players join freely before the game
/// starts, can leave freely before the game starts, and any already-joined
/// player can trigger the explicit "start" action, which locks the roster
/// in join order. No joins or leaves once started. Frame count and pin
/// layout are also only changeable pre-start, each cycled through a fixed
/// option list one button-press at a time (no dropdown — see
/// BowlingPanelButton).
///
/// Wired to a ball via BowlingBall's optional _gameController field —
/// leave that unassigned on any lane meant to stay pure free-play with no
/// scoring.
///
/// Once every player finishes, the lane doesn't just sit there — after
/// _winnerScreenDuration (so BowlingScoreboardText has time to show a
/// winner screen), ServerFinishGameRoutine auto-resets: same roster,
/// scores cleared, pins re-racked, back to WAITING. An explicit Start
/// press is still required to actually begin the rematch.
/// </summary>
public class BowlingGameController : NetworkBehaviour
{
    [Header("Lane")]
    [Tooltip("Index into LobbySpawner's _bowlingLanes list — which lane this controller scores.")]
    [SerializeField] private int _laneIndex;

    [Header("Game Settings")]
    [Tooltip("Starting frame count for a full game — 5, 8, and 10 are the typical options. This is only the STARTING value — the panel's Cycle Frame Count button changes it at runtime (see _frameCount below).")]
    [SerializeField] private int _initialFrameCount = 10;

    [Tooltip("Maximum players allowed to join this lane before the panel's Join button stops accepting new joins.")]
    [SerializeField] private int _maxPlayers = 4;

    [Tooltip("Seconds to wait after a roll completes before counting standing pins — gives any still-toppling pins time to be confirmed fallen (Pin's own ReportPossibleFall/angle-check settling happens within this window). Tune per-lane in the Inspector if 5s is too long/short.")]
    [SerializeField] private float _pinSettleDelay = 5f;

    [Tooltip("Seconds to show the winner/final-standings screen after the last roll before automatically resetting the lane for a new game — same roster, scores cleared, pins re-racked, but still WAITING until someone presses Start again (see ServerFinishGameRoutine).")]
    [SerializeField] private float _winnerScreenDuration = 10f;

    [Tooltip("Short stinger played at this lane's position the instant the winner screen appears (see ObserversPlayCelebration) — every client runs this locally, but it's 3D/distance-limited like the other bowling SFX, not a flat broadcast, so other rooms don't hear it. Keep it at or under Winner Screen Duration above — a longer clip gets cut short by the auto-reset, and a console warning fires if it's mismatched during testing.")]
    [SerializeField] private AudioClip _celebrationClip;
    [Tooltip("Max distance the celebration stinger carries — wider than a single pin-hit/ball-impact sound (see AudioManager's default SFX falloff) since this marks a whole game ending, not one small physical event.")]
    [SerializeField] private float _celebrationMaxDistance = 30f;

    // Cycled through by BowlingPanelButton's CycleFrameCount action, one
    // step per press (no dropdown to pick from directly — see
    // BowlingPanelButton's class comment for why).
    private static readonly int[] _frameCountOptions = { 5, 8, 10 };

    // Both SyncVars (not plain fields, unlike Phase 3) — BowlingPanelText
    // needs to read the live value from EVERY client, not just the server,
    // now that the panel shows "Frames: 10"/"Pins: 10" as text instead of
    // just logging it. Polled from BowlingPanelText.Update() rather than
    // reacted to via OnChange — same convention as Pin.cs's
    // _isStandingSync (OnChange wasn't found reliable on host in this
    // project). _frameCount starts at _initialFrameCount (set in Awake()).
    private readonly SyncVar<int> _frameCount = new SyncVar<int>();

    // Tracks which of the lane's PinConfig.Patterns is currently selected —
    // NOT the same as the config asset's own ActivePatternIndex, which
    // stays untouched (mutating a shared ScriptableObject asset at runtime
    // would leak across every lane using that same config). Initialized
    // from the lane's actual currently-racked pattern once the lane
    // finishes setting up (see ServerWaitForLaneReadyRoutine), so the
    // panel's initial Pins display is accurate even before the first cycle
    // press, rather than defaulting to an assumed index.
    private readonly SyncVar<int> _selectedPatternIndex = new SyncVar<int>();

    // Phase 4: the whole scoreboard, serialized into one compact string —
    // same "single SyncVar, redraw on change" convention as _frameCount/
    // _selectedPatternIndex above, just carrying a bigger payload. Chosen
    // over a FishNet SyncList<T> of per-player structs because the data's
    // small (a handful of players, up to 10 frames) and a string keeps the
    // client-side parsing (BowlingScoreboardText) trivial and easy to log/
    // debug, at the cost of re-serializing the whole thing on every change
    // rather than just the delta — negligible at this scale.
    //
    // Format: "STATE|current|playersBlock"
    //   STATE: WAITING | PLAYING | FINISHED
    //   current: "CurrentName:FrameNum" during PLAYING, empty otherwise —
    //     which row/frame BowlingScoreboardText should highlight.
    //   playersBlock: "~"-separated player blocks, each
    //     "Name:frameToken,frameToken,..." — one token per frame the
    //     player has actually started (not yet-reached frames are simply
    //     absent; the client pads up to FrameCount blank columns itself).
    //     A frameToken is "roll;roll;...-total" (semicolon-separated rolls,
    //     '-' separates the roll list from the running total, total is
    //     empty when that frame hasn't resolved a score yet).
    //   Example: "PLAYING|Alice:3|Alice:10-20,7;2-29~Bob:8;1-9,10-"
    //     (Alice: frame 1 was a strike running-total 20, frame 2 rolls 7+2
    //     running-total 29, now on frame 3; Bob: frame 1 rolls 8+1 total 9,
    //     frame 2 a strike with the bonus-roll total not resolved yet.)
    private readonly SyncVar<string> _scoreboardSnapshot = new SyncVar<string>();

    /// Live scoreboard snapshot — for BowlingScoreboardText to parse and
    /// display. See the format comment on _scoreboardSnapshot above.
    public string ScoreboardSnapshot => _scoreboardSnapshot.Value;

    /// Live frame count — for BowlingPanelText to display as "Frames: N".
    public int FrameCount => _frameCount.Value;

    /// How long the winner screen stays up before auto-resetting — read by
    /// BowlingScoreboardText if it wants to show/estimate a countdown.
    /// Plain field, not a SyncVar: this is a scene-placed, Inspector-set
    /// config value baked into the saved scene, identical on every peer
    /// already, not something that changes at runtime.
    public float WinnerScreenDuration => _winnerScreenDuration;

    /// Pin count of the currently-selected pattern — for BowlingPanelText
    /// to display as "Pins: N". Returns 0 if nothing's resolved yet.
    public int PinCount
    {
        get
        {
            BowlingLaneInstance lane = LobbySpawner.Instance?.GetBowlingLane(_laneIndex);
            int patternIndex = _selectedPatternIndex.Value;
            if (lane?.PinConfig?.Patterns == null || patternIndex < 0 || patternIndex >= lane.PinConfig.Patterns.Count)
                return 0;
            return lane.PinConfig.Patterns[patternIndex].ActiveSlotIndices.Count;
        }
    }

    /// Name of the currently-selected pin pattern (e.g. "Full Rack",
    /// whatever PatternName is set to in BowlingPinConfig) — for
    /// BowlingPanelText to display as "Layout: N" instead of just a pin
    /// count. Same lookup as PinCount, just reading PatternName instead of
    /// ActiveSlotIndices.Count. Returns "" if nothing's resolved yet.
    public string PatternName
    {
        get
        {
            BowlingLaneInstance lane = LobbySpawner.Instance?.GetBowlingLane(_laneIndex);
            int patternIndex = _selectedPatternIndex.Value;
            if (lane?.PinConfig?.Patterns == null || patternIndex < 0 || patternIndex >= lane.PinConfig.Patterns.Count)
                return "";
            return lane.PinConfig.Patterns[patternIndex].PatternName;
        }
    }

    // One entry per joined player — their identity (the PlayerObject handed
    // to us by IInteractable, same as every other interact station in the
    // project) plus their own independent frame scorer. Private/nested since
    // nothing outside this controller needs to touch it directly.
    private class BowlingPlayerEntry
    {
        public PlayerObject Player;
        public BowlingFrameScorer Scorer;
        public bool IsFinished;
    }

    private readonly List<BowlingPlayerEntry> _players = new List<BowlingPlayerEntry>();
    private int _currentPlayerIndex;
    private bool _gameStarted;

    // True once LobbySpawner has fully finished spawning/racking this lane
    // (see BowlingLaneInstance.IsSetupComplete) — joins are fine before
    // this, but starting the game isn't, since CountStandingPins() would
    // read a partial rack (this exact bug hit Phase 2's first roll).
    private bool _laneReady;

    private int _standingPinsBeforeRoll;
    private bool _awaitingSettle;
    private float _settleTimer;

    private void Awake()
    {
        _frameCount.Value = _initialFrameCount;
    }

    public override void OnStartServer()
    {
        base.OnStartServer();
        StartCoroutine(ServerWaitForLaneReadyRoutine());
    }

    private void Update()
    {
        if (!IsServerInitialized || !_awaitingSettle) return;

        _settleTimer -= Time.deltaTime;
        if (_settleTimer <= 0f)
        {
            _awaitingSettle = false;
            ServerResolveRoll();
        }
    }

    // Waits for LobbySpawner to have FULLY finished spawning and racking
    // this lane (SpawnBowlingLaneRoutine is itself spread across frames).
    // Renamed from Phase 2's ServerStartGameRoutine — it no longer starts
    // anything itself now that starting is an explicit player action via
    // BowlingJoinStation, it just tracks when the lane is safe to start.
    private IEnumerator ServerWaitForLaneReadyRoutine()
    {
        BowlingLaneInstance lane = LobbySpawner.Instance.GetBowlingLane(_laneIndex);
        while (lane == null || !lane.IsSetupComplete)
        {
            yield return null;
            lane = LobbySpawner.Instance.GetBowlingLane(_laneIndex);
        }
        _laneReady = true;

        // Starts the panel's Pins display accurate from the get-go — matches
        // whatever pattern LobbySpawner actually racked at spawn, rather
        // than defaulting to an assumed index.
        _selectedPatternIndex.Value = lane.ActivePatternIndex;

        // Establishes the scoreboard's initial WAITING state as soon as the
        // lane's ready, rather than leaving _scoreboardSnapshot at its
        // never-assigned default (null) until the first join.
        RebuildScoreboardSnapshot();
    }

    /// Called by BowlingPanelButton's Join action.
    [Server]
    public void ServerJoin(PlayerObject player)
    {
        if (_gameStarted || player == null) return;

        if (_players.Exists(p => p.Player == player))
            return; // already joined — no-op, not an error

        if (_players.Count >= _maxPlayers)
        {
            Debug.LogWarning($"[BowlingGameController] Lane {_laneIndex}: {GetDisplayName(player)} tried to join, but the lane is full ({_maxPlayers} max).");
            return;
        }

        _players.Add(new BowlingPlayerEntry { Player = player, IsFinished = false });
        Debug.Log($"[BowlingGameController] Lane {_laneIndex}: {GetDisplayName(player)} joined ({_players.Count}/{_maxPlayers} player(s) so far).");
        RebuildScoreboardSnapshot();
    }

    /// Called by BowlingPanelButton's Leave action. Pre-start only — once
    /// the game locks in, the roster (and turn order derived from it) is
    /// fixed, same as no new joins being accepted.
    [Server]
    public void ServerLeave(PlayerObject player)
    {
        if (_gameStarted || player == null) return;

        int removedCount = _players.RemoveAll(p => p.Player == player);
        if (removedCount > 0)
        {
            Debug.Log($"[BowlingGameController] Lane {_laneIndex}: {GetDisplayName(player)} left ({_players.Count}/{_maxPlayers} player(s) remaining).");
            RebuildScoreboardSnapshot();
        }
    }

    /// Called by BowlingPanelButton's Start action. Any already-joined
    /// player can trigger it, not just whoever joined first — but you do
    /// have to be on the roster to press it.
    [Server]
    public void ServerStart(PlayerObject player)
    {
        if (_gameStarted || player == null) return;

        if (!_players.Exists(p => p.Player == player))
        {
            Debug.LogWarning($"[BowlingGameController] Lane {_laneIndex}: {GetDisplayName(player)} tried to start, but hasn't joined.");
            return;
        }

        ServerStartGame();
    }

    /// Called by BowlingPanelButton's CycleFrameCount action. Pre-start
    /// only — changing this once a BowlingFrameScorer exists per player
    /// would desync frame numbering mid-game.
    [Server]
    public void ServerCycleFrameCount()
    {
        if (_gameStarted)
        {
            Debug.LogWarning($"[BowlingGameController] Lane {_laneIndex}: can't change frame count after the game has started.");
            return;
        }

        int currentIndex = System.Array.IndexOf(_frameCountOptions, _frameCount.Value);
        int nextIndex = (currentIndex + 1) % _frameCountOptions.Length; // -1 (not found) wraps to 0
        _frameCount.Value = _frameCountOptions[nextIndex];

        Debug.Log($"[BowlingGameController] Lane {_laneIndex}: frame count set to {_frameCount.Value}.");
    }

    /// Called by BowlingPanelButton's CyclePinLayout action. Pre-start only
    /// — applies immediately via LobbySpawner's existing pattern-switch
    /// (pure state toggling, no spawn/despawn — see LobbySpawner's
    /// ApplyBowlingPattern comment), so you can see the rack change as you
    /// cycle through options.
    [Server]
    public void ServerCyclePinLayout()
    {
        if (_gameStarted)
        {
            Debug.LogWarning($"[BowlingGameController] Lane {_laneIndex}: can't change pin layout after the game has started.");
            return;
        }

        BowlingLaneInstance lane = LobbySpawner.Instance.GetBowlingLane(_laneIndex);
        if (lane?.PinConfig?.Patterns == null || lane.PinConfig.Patterns.Count == 0)
        {
            Debug.LogWarning($"[BowlingGameController] Lane {_laneIndex}: no pin patterns configured to cycle through.");
            return;
        }

        _selectedPatternIndex.Value = (_selectedPatternIndex.Value + 1) % lane.PinConfig.Patterns.Count;
        LobbySpawner.Instance.SwitchBowlingPattern(_laneIndex, _selectedPatternIndex.Value);

        string patternName = lane.PinConfig.Patterns[_selectedPatternIndex.Value].PatternName;
        Debug.Log($"[BowlingGameController] Lane {_laneIndex}: pin layout set to '{patternName}' ({_selectedPatternIndex.Value + 1}/{lane.PinConfig.Patterns.Count}) — {PinCount} pins.");
    }

    [Server]
    private void ServerStartGame()
    {
        if (_gameStarted) return;

        if (!_laneReady)
        {
            Debug.LogWarning($"[BowlingGameController] Lane {_laneIndex}: can't start yet — lane is still setting up.");
            return;
        }
        if (_players.Count == 0)
        {
            Debug.LogWarning($"[BowlingGameController] Lane {_laneIndex}: can't start — no players joined.");
            return;
        }

        foreach (BowlingPlayerEntry p in _players)
            p.Scorer = new BowlingFrameScorer(_frameCount.Value);

        _gameStarted = true;
        _currentPlayerIndex = 0;
        LobbySpawner.Instance.ResetBowlingLane(_laneIndex);
        _standingPinsBeforeRoll = CountStandingPins();
        RebuildScoreboardSnapshot();

        Debug.Log($"[BowlingGameController] Lane {_laneIndex}: game started — {_players.Count} player(s), {_frameCount.Value} frames. {GetDisplayName(_players[0].Player)} goes first.");
    }

    /// Called by BowlingBall.ServerRegisterRollComplete() when a ball on
    /// this lane reaches the back — starts the pin-settle countdown rather
    /// than counting pins immediately, since a pin can still be mid-topple
    /// (or mid its own fall confirmation) at that exact moment.
    public void ServerOnRollComplete(BowlingBall ball)
    {
        if (!IsServerInitialized || !_gameStarted || _awaitingSettle) return;
        _awaitingSettle = true;
        _settleTimer = _pinSettleDelay;
    }

    private void ServerResolveRoll()
    {
        BowlingPlayerEntry current = _players[_currentPlayerIndex];

        int standingNow = CountStandingPins();
        int pinsDown = Mathf.Max(0, _standingPinsBeforeRoll - standingNow);
        current.Scorer.AddRoll(pinsDown);

        LogScoreboard(current, pinsDown);
        RebuildScoreboardSnapshot();

        if (current.Scorer.IsGameComplete())
        {
            current.IsFinished = true;
            int? finalScore = current.Scorer.GetCurrentTotal();
            Debug.Log($"[BowlingGameController] Lane {_laneIndex}: {GetDisplayName(current.Player)} finished — final score {(finalScore.HasValue ? finalScore.Value.ToString() : "pending")}.");

            if (AllPlayersFinished())
            {
                LogFinalStandings();
                RebuildScoreboardSnapshot(); // switches the snapshot's state to FINISHED
                ObserversPlayCelebration();
                StartCoroutine(ServerFinishGameRoutine());
                return;
            }

            ServerAdvanceToNextPlayer();
            return;
        }

        FrameResult currentFrame = current.Scorer.GetCurrentFrame();
        bool isLastFrame = currentFrame.FrameNumber == _frameCount.Value;

        if (currentFrame.IsComplete)
        {
            // This player's frame is over (strike, spare, or open frame
            // finished) but they still have more frames to play — turn
            // passes to the next player, real-bowling style.
            ServerAdvanceToNextPlayer();
        }
        else if (isLastFrame && standingNow == 0)
        {
            // A bonus roll within the still-open final frame just cleared
            // the rack — re-rack fresh for the next roll (real 10th-frame
            // rules). Deliberately checking standingNow here rather than
            // currentFrame.IsStrike: IsStrike reflects ONLY roll 1 and
            // never changes afterward, so checking it would re-rack fresh
            // on every remaining bonus roll regardless of what that roll
            // actually knocked down (e.g. strike, then a 7 that leaves 3
            // pins standing — that 7 was wrongly getting a fresh rack too,
            // instead of the 3 pins it actually left). standingNow == 0
            // correctly covers every case that should re-rack: the strike
            // itself, a second consecutive strike, and a spare completing.
            LobbySpawner.Instance.ResetBowlingLane(_laneIndex);
            _standingPinsBeforeRoll = CountStandingPins();
        }
        else
        {
            // Open, mid-frame roll — same player continues against
            // whatever's left standing.
            _standingPinsBeforeRoll = standingNow;
        }
    }

    // Moves _currentPlayerIndex to the next player who hasn't finished yet,
    // wrapping around the roster, then re-racks for their frame. Only
    // called when at least one other player is still playing — the
    // AllPlayersFinished() check in ServerResolveRoll() handles the case
    // where nobody's left.
    private void ServerAdvanceToNextPlayer()
    {
        int startIndex = _currentPlayerIndex;
        int nextIndex = (_currentPlayerIndex + 1) % _players.Count;

        while (_players[nextIndex].IsFinished && nextIndex != startIndex)
            nextIndex = (nextIndex + 1) % _players.Count;

        _currentPlayerIndex = nextIndex;
        LobbySpawner.Instance.ResetBowlingLane(_laneIndex);
        _standingPinsBeforeRoll = CountStandingPins();

        BowlingPlayerEntry next = _players[_currentPlayerIndex];
        Debug.Log($"[BowlingGameController] Lane {_laneIndex}: {GetDisplayName(next.Player)}'s turn — frame {next.Scorer.Frames.Count + 1}.");
        RebuildScoreboardSnapshot();
    }

    private bool AllPlayersFinished()
    {
        foreach (BowlingPlayerEntry p in _players)
            if (!p.IsFinished) return false;
        return true;
    }

    // Fired once, right as the snapshot flips to FINISHED — every client
    // runs this locally, but PlaySFXAtPosition (not the flat PlaySFX) means
    // it's still distance-limited from this lane's position, same as the
    // other bowling SFX — otherwise every room in the lobby would hear
    // every lane's win stinger at full volume.
    [ObserversRpc]
    private void ObserversPlayCelebration()
    {
        if (_celebrationClip == null) return;

        // Not a hard requirement — just a heads-up while tuning the clip
        // and Winner Screen Duration together, since ServerFinishGameRoutine
        // resets the lane at exactly _winnerScreenDuration regardless of
        // whether the clip is still playing.
        if (_celebrationClip.length > _winnerScreenDuration)
            Debug.LogWarning($"[BowlingGameController] Lane {_laneIndex}: celebration clip ({_celebrationClip.length:0.0}s) is longer than Winner Screen Duration ({_winnerScreenDuration:0.0}s) — it'll be cut off by the auto-reset.");

        AudioManager.Instance?.PlaySFXAtPosition(_celebrationClip, transform.position, 0f, 1f, null, _celebrationMaxDistance);
    }

    private void LogFinalStandings()
    {
        StringBuilder sb = new StringBuilder();
        sb.Append($"[BowlingGameController] Lane {_laneIndex}: GAME COMPLETE — ");

        foreach (BowlingPlayerEntry p in _players)
        {
            int? score = p.Scorer.GetCurrentTotal();
            sb.Append($"{GetDisplayName(p.Player)}: {(score.HasValue ? score.Value.ToString() : "0")}  ");
        }

        Debug.Log(sb.ToString());
    }

    // Was: once every player finished, the lane just sat there forever —
    // _gameStarted never went back to false, so Join/Leave/Start all
    // silently no-op'd (they all early-return on _gameStarted) and there
    // was no way to play again without leaving/re-entering the lane
    // entirely. Fix: after the winner screen has had time to show
    // (_winnerScreenDuration), automatically reset — keep the same
    // roster, clear scores, re-rack pins — but land back in WAITING
    // rather than auto-starting, so a Start press is still required.
    private IEnumerator ServerFinishGameRoutine()
    {
        yield return new WaitForSeconds(_winnerScreenDuration);
        if (!IsServerInitialized) yield break; // lane could've been despawned mid-countdown

        ServerResetForNewGame();
    }

    [Server]
    private void ServerResetForNewGame()
    {
        _gameStarted = false;
        _currentPlayerIndex = 0;

        foreach (BowlingPlayerEntry p in _players)
        {
            p.Scorer = null; // BuildPlayersBlock treats a null Scorer as "no frames yet" — same as a freshly-joined player, so this reads as a clean 0
            p.IsFinished = false;
        }

        LobbySpawner.Instance.ResetBowlingLane(_laneIndex);
        RebuildScoreboardSnapshot(); // _gameStarted is false again, so this naturally flips the snapshot's state back to WAITING

        Debug.Log($"[BowlingGameController] Lane {_laneIndex}: game reset for a rematch — same {_players.Count} player(s), scores cleared, waiting on Start.");
    }

    private int CountStandingPins()
    {
        BowlingLaneInstance lane = LobbySpawner.Instance.GetBowlingLane(_laneIndex);
        if (lane?.SpawnedPins == null) return 0;

        int count = 0;
        foreach (Pin pin in lane.SpawnedPins)
            if (pin != null && pin.IsStanding) count++;
        return count;
    }

    // PlayerObject.PlayerName is a stale placeholder ("Player_<clientId>")
    // assigned once at spawn (see LobbySpawner) — it's set before the real
    // player-chosen name even exists server-side, and nothing ever updates
    // it afterward. The actual selected name (same one the leaderboard
    // shows) lives on PlayerProfileSync.DisplayName, on the same
    // GameObject as PlayerObject. Falls back to the old placeholder only
    // if PlayerProfileSync is somehow missing, so this never throws/blanks.
    private static string GetDisplayName(PlayerObject player)
    {
        PlayerProfileSync profileSync = player.GetComponent<PlayerProfileSync>();
        string name = profileSync != null ? profileSync.DisplayName.Value : null;
        return string.IsNullOrEmpty(name) ? player.PlayerName : name;
    }

    // Serializes the whole lane's scoreboard into _scoreboardSnapshot — see
    // the field's comment above for the exact format. Only reassigns the
    // SyncVar when the string actually changed, matching BowlingPanelText's
    // "only redraw when the value changes" convention (avoids spamming a
    // SyncVar write, and every SyncVar write, every frame for no reason).
    //
    // Full frame-by-frame grid (Phase 4 revision) — every joined player's
    // complete frame history, not just their current frame/total, so
    // BowlingScoreboardText can render a real per-player, per-frame
    // scoresheet instead of a single live-total list.
    [Server]
    private void RebuildScoreboardSnapshot()
    {
        string state;
        string currentBlock = "";

        if (!_gameStarted)
        {
            state = "WAITING";
        }
        else if (AllPlayersFinished())
        {
            state = "FINISHED";
        }
        else
        {
            state = "PLAYING";
            BowlingPlayerEntry current = _players[_currentPlayerIndex];
            int frameNumber = GetDisplayFrameNumber(current.Scorer);
            currentBlock = $"{SanitizeForSnapshot(GetDisplayName(current.Player))}:{frameNumber}";
        }

        string snapshot = $"{state}|{currentBlock}|{BuildPlayersBlock()}";

        if (snapshot != _scoreboardSnapshot.Value)
            _scoreboardSnapshot.Value = snapshot;
    }

    // The frame a player is CURRENTLY on / about to bowl. GetCurrentFrame()
    // only returns a frame object once at least one roll has been recorded
    // for it, so right after a frame closes (and before the next roll is
    // thrown) it still points at the just-closed frame — this showed up as
    // the scoreboard's frame number appearing stuck/stale until a ball was
    // actually thrown for the next frame. Fix: once the last-known frame is
    // complete, the player is already on the next one, even though no roll
    // exists for it yet; only fall back to that frame's own number while
    // it's still open (mid-frame).
    private int GetDisplayFrameNumber(BowlingFrameScorer scorer)
    {
        FrameResult last = scorer.GetCurrentFrame();
        if (last == null) return 1;
        if (!last.IsComplete) return last.FrameNumber;
        return Mathf.Min(last.FrameNumber + 1, _frameCount.Value);
    }

    // Player names are free text (set in the character creator) and could
    // otherwise collide with the snapshot format's own delimiters —
    // ',' ':' ';' '>' '|' '~' '-' — which would desync BowlingScoreboardText's
    // parsing (e.g. a name containing '~' would look like it starts a new
    // player block early). Strips those out before a name ever goes into
    // the snapshot string, rather than trying to escape/unescape them on
    // the parsing side. Two different players sanitizing to the same
    // displayed string is an acceptable, purely cosmetic ambiguity —
    // nothing downstream keys off the snapshot's names, only PlayerObject
    // references do.
    private static string SanitizeForSnapshot(string name)
    {
        if (string.IsNullOrEmpty(name)) return "?";

        char[] forbidden = { ',', ':', ';', '>', '|', '~', '-' };
        foreach (char c in forbidden)
            name = name.Replace(c, ' ');
        return name.Trim();
    }

    // "~"-separated player blocks, each "Name:frameToken,frameToken,...".
    // A frameToken is "roll;roll;...-total" (rolls ';'-separated, total
    // empty when that frame hasn't resolved a score yet — mid-frame, or a
    // last-frame bonus roll still pending). Only frames the player has
    // actually started appear at all; BowlingScoreboardText fills the
    // remaining columns up to FrameCount as blank boxes. Shared by every
    // state — WAITING players just have no frame tokens yet (Scorer is
    // null before the game starts), so the same parser/renderer on the
    // client handles all three states without a separate code path.
    private string BuildPlayersBlock()
    {
        List<string> playerBlocks = new List<string>();
        foreach (BowlingPlayerEntry p in _players)
        {
            List<string> frameTokens = new List<string>();
            if (p.Scorer != null)
            {
                foreach (FrameResult frame in p.Scorer.Frames)
                {
                    string rolls = string.Join(";", frame.Rolls);
                    string total = frame.RunningTotal.HasValue ? frame.RunningTotal.Value.ToString() : "";
                    frameTokens.Add($"{rolls}-{total}");
                }
            }
            playerBlocks.Add($"{SanitizeForSnapshot(GetDisplayName(p.Player))}:{string.Join(",", frameTokens)}");
        }
        return string.Join("~", playerBlocks);
    }

    private void LogScoreboard(BowlingPlayerEntry player, int lastRollPins)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append($"[BowlingGameController] Lane {_laneIndex} — {GetDisplayName(player.Player)}'s roll: {lastRollPins} pins. ");

        foreach (FrameResult frame in player.Scorer.Frames)
        {
            string rolls = string.Join(",", frame.Rolls);
            string total = frame.RunningTotal.HasValue ? frame.RunningTotal.Value.ToString() : "-";
            sb.Append($"[F{frame.FrameNumber}: {rolls} => {total}] ");
        }

        Debug.Log(sb.ToString());
    }
}
