// BowlingFrameScorer
using System.Collections.Generic;

/// <summary>
/// Standard bowling scoring, generalized to any frame count (5, 8, 10, ...)
/// rather than hardcoded to 10 — the only special case is the LAST frame,
/// which gets bonus rolls after a strike or spare exactly like a real 10th
/// frame does, regardless of how many frames the game is configured for.
///
/// Pure C# — no MonoBehaviour, no FishNet — so the scoring math itself can
/// be verified in isolation before anything networked is built on top of
/// it. Feed it pins-knocked-down-per-roll via AddRoll(); it maintains the
/// frame breakdown itself.
/// </summary>
public class BowlingFrameScorer
{
    private readonly int _frameCount;
    private readonly List<int> _rolls = new List<int>();
    private readonly List<FrameResult> _frames = new List<FrameResult>();

    public BowlingFrameScorer(int frameCount)
    {
        _frameCount = frameCount;
    }

    public IReadOnlyList<int> Rolls => _rolls;
    public IReadOnlyList<FrameResult> Frames => _frames;

    public void AddRoll(int pinsKnockedDown)
    {
        _rolls.Add(pinsKnockedDown);
        Recompute();
    }

    /// The frame most recently touched by AddRoll() — may already be
    /// complete (just closed) or still open (waiting on more rolls).
    public FrameResult GetCurrentFrame()
    {
        return _frames.Count > 0 ? _frames[_frames.Count - 1] : null;
    }

    /// The running total through the furthest frame whose score is
    /// currently resolvable — null if no frame has resolved yet.
    public int? GetCurrentTotal()
    {
        int? total = null;
        foreach (FrameResult frame in _frames)
        {
            if (frame.RunningTotal.HasValue)
                total = frame.RunningTotal;
        }
        return total;
    }

    public bool IsGameComplete()
    {
        return _frames.Count == _frameCount && _frames[_frames.Count - 1].IsComplete;
    }

    private void Recompute()
    {
        _frames.Clear();
        int rollIdx = 0;

        for (int frameNum = 1; frameNum <= _frameCount && rollIdx < _rolls.Count; frameNum++)
        {
            bool isLastFrame = frameNum == _frameCount;
            FrameResult frame = new FrameResult { FrameNumber = frameNum };

            rollIdx = isLastFrame ? BuildLastFrame(frame, rollIdx) : BuildRegularFrame(frame, rollIdx);

            _frames.Add(frame);
        }

        ComputeScores();
    }

    private int BuildRegularFrame(FrameResult frame, int rollIdx)
    {
        if (_rolls[rollIdx] == 10)
        {
            frame.Rolls.Add(_rolls[rollIdx]);
            frame.IsStrike = true;
            frame.IsComplete = true;
            return rollIdx + 1;
        }

        frame.Rolls.Add(_rolls[rollIdx]);
        rollIdx++;

        if (rollIdx >= _rolls.Count)
            return rollIdx; // only the first roll has happened — frame still open

        frame.Rolls.Add(_rolls[rollIdx]);
        rollIdx++;
        frame.IsComplete = true;
        frame.IsSpare = frame.Rolls[0] + frame.Rolls[1] == 10;
        return rollIdx;
    }

    private int BuildLastFrame(FrameResult frame, int rollIdx)
    {
        frame.Rolls.Add(_rolls[rollIdx]);
        rollIdx++;

        if (frame.Rolls[0] == 10)
        {
            frame.IsStrike = true;
            rollIdx = TakeUpTo(frame, rollIdx, 3);
            frame.IsComplete = frame.Rolls.Count == 3;
            return rollIdx;
        }

        if (rollIdx >= _rolls.Count)
            return rollIdx; // waiting on roll 2

        frame.Rolls.Add(_rolls[rollIdx]);
        rollIdx++;

        if (frame.Rolls[0] + frame.Rolls[1] == 10)
        {
            frame.IsSpare = true;
            rollIdx = TakeUpTo(frame, rollIdx, 3);
            frame.IsComplete = frame.Rolls.Count == 3;
            return rollIdx;
        }

        frame.IsComplete = true;
        return rollIdx;
    }

    private int TakeUpTo(FrameResult frame, int rollIdx, int targetCount)
    {
        while (frame.Rolls.Count < targetCount && rollIdx < _rolls.Count)
        {
            frame.Rolls.Add(_rolls[rollIdx]);
            rollIdx++;
        }
        return rollIdx;
    }

    private void ComputeScores()
    {
        for (int i = 0; i < _frames.Count; i++)
        {
            FrameResult frame = _frames[i];
            bool isLastFrame = frame.FrameNumber == _frameCount;

            if (isLastFrame)
            {
                if (frame.IsComplete)
                    frame.FrameScore = Sum(frame.Rolls);
                continue;
            }

            if (!frame.IsComplete) continue;

            if (frame.IsStrike)
            {
                int[] lookahead = GetRollsAfterFrame(i, 2);
                if (lookahead != null)
                    frame.FrameScore = 10 + lookahead[0] + lookahead[1];
            }
            else if (frame.IsSpare)
            {
                int[] lookahead = GetRollsAfterFrame(i, 1);
                if (lookahead != null)
                    frame.FrameScore = 10 + lookahead[0];
            }
            else
            {
                frame.FrameScore = Sum(frame.Rolls);
            }
        }

        int running = 0;
        for (int i = 0; i < _frames.Count; i++)
        {
            if (!_frames[i].FrameScore.HasValue) break;
            running += _frames[i].FrameScore.Value;
            _frames[i].RunningTotal = running;
        }
    }

    // Returns the next `count` rolls after the frame at _frames[frameIndex],
    // pulled from whichever later frames have them, or null if they haven't
    // happened yet (e.g. a strike with no follow-up rolls recorded yet).
    private int[] GetRollsAfterFrame(int frameIndex, int count)
    {
        List<int> found = new List<int>();
        for (int i = frameIndex + 1; i < _frames.Count && found.Count < count; i++)
            found.AddRange(_frames[i].Rolls);

        if (found.Count < count) return null;
        return found.GetRange(0, count).ToArray();
    }

    private static int Sum(List<int> rolls)
    {
        int total = 0;
        foreach (int r in rolls) total += r;
        return total;
    }
}

public class FrameResult
{
    public int FrameNumber;
    public List<int> Rolls = new List<int>();
    public bool IsStrike;
    public bool IsSpare;
    public bool IsComplete;
    public int? FrameScore;
    public int? RunningTotal;
}
