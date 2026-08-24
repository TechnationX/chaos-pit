// BowlingScoreboardText.cs
using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// Purely visual — parses BowlingGameController.ScoreboardSnapshot and
/// renders it as a live world-space full frame-by-frame scoreboard grid
/// (real bowling scoresheet style: one row per player, one box per frame,
/// each box showing that frame's rolls on the left and its running total
/// on the right). Same polling convention as BowlingPanelText: checks the
/// value every frame, only rebuilds the displayed text when it actually
/// changed.
///
/// Not a NetworkBehaviour — it doesn't need to be. ScoreboardSnapshot is
/// already a replicated SyncVar<string> on BowlingGameController; this
/// script just reads that public accessor locally on whichever client is
/// looking at the board. FrameCount (also already public on the
/// controller) supplies the column count, so it doesn't need to be
/// re-encoded into the snapshot string.
///
/// Parses the "STATE|current|playersBlock" format documented on
/// BowlingGameController._scoreboardSnapshot — see that comment for the
/// exact grammar and a worked example.
///
/// Grid alignment uses TMP's &lt;mspace&gt; rich-text tag (forces a fixed
/// per-character advance width, so this lines up with any font asset, not
/// just a literal monospace one) rather than building the grid out of real
/// UI RectTransforms/cells — keeps this a single Text component, same
/// setup shape as BowlingPanelText, no extra scene construction needed
/// beyond the one Canvas + TextMeshProUGUI.
/// </summary>
public class BowlingScoreboardText : MonoBehaviour
{
    [Tooltip("The lane's BowlingGameController to read the live scoreboard from.")]
    [SerializeField] private BowlingGameController _gameController;
    [Tooltip("Leave unassigned to use a TextMeshProUGUI on this same GameObject.")]
    [SerializeField] private TextMeshProUGUI _text;

    [Header("Grid layout")]
    [Tooltip("Fixed character width of the player-name column.")]
    [SerializeField] private int _nameColumnWidth = 10;
    [Tooltip("Fixed character width of each frame column — needs to fit the widest last-frame notation ('X X X', 5 chars) plus a space plus a 3-digit running total.")]
    [SerializeField] private int _frameColumnWidth = 9;
    [Tooltip("TMP <mspace> em value used to force uniform character width for the grid, regardless of font. Tune if columns don't line up with your font asset.")]
    [SerializeField] private string _monospaceEm = "0.55em";
    [Tooltip("TMP hex color used to highlight the current player's row and their active frame cell.")]
    [SerializeField] private string _highlightColorHex = "#FFD966";

    // Sentinel so the very first Update() always applies, even if the real
    // snapshot happens to be an empty string.
    private string _lastAppliedSnapshot = "\0";

    private void Awake()
    {
        if (_text == null)
            _text = GetComponent<TextMeshProUGUI>();
    }

    private void Update()
    {
        if (_gameController == null || _text == null) return;

        string snapshot = _gameController.ScoreboardSnapshot;
        if (snapshot == _lastAppliedSnapshot) return;

        _lastAppliedSnapshot = snapshot;
        _text.text = Render(snapshot);
    }

    private string Render(string snapshot)
    {
        if (string.IsNullOrEmpty(snapshot))
            return "Bowling";

        string[] top = snapshot.Split('|');
        if (top.Length < 3) return snapshot; // unrecognized — show raw rather than crash

        string state = top[0];
        string currentPart = top[1];
        string playersPart = top[2];

        string currentName = null;
        int currentFrame = -1;
        if (!string.IsNullOrEmpty(currentPart))
        {
            string[] cp = currentPart.Split(':');
            currentName = cp[0];
            if (cp.Length > 1) int.TryParse(cp[1], out currentFrame);
        }

        List<PlayerRow> rows = ParsePlayers(playersPart);
        int frameCount = Mathf.Max(_gameController.FrameCount, 1);

        string header;
        if (state == "WAITING")
            header = rows.Count == 0 ? "Waiting for players" : "Waiting for players — press Start when ready";
        else if (state == "FINISHED")
            header = "Game Over!";
        else
            header = currentName != null ? $"Frame {currentFrame} — {currentName}'s turn" : "Bowling";

        string grid = BuildGrid(rows, frameCount, currentName, currentFrame, state == "FINISHED");

        return string.IsNullOrEmpty(grid) ? header : $"{header}\n\n{grid}";
    }

    private struct PlayerRow
    {
        public string Name;
        public List<(string rollsCsv, string total)> Frames;
    }

    private static List<PlayerRow> ParsePlayers(string playersPart)
    {
        List<PlayerRow> rows = new List<PlayerRow>();
        if (string.IsNullOrEmpty(playersPart)) return rows;

        foreach (string block in playersPart.Split('~'))
        {
            if (string.IsNullOrEmpty(block)) continue;

            int colonIndex = block.IndexOf(':');
            string name = colonIndex >= 0 ? block.Substring(0, colonIndex) : block;
            string framesCsv = colonIndex >= 0 ? block.Substring(colonIndex + 1) : "";

            List<(string, string)> frames = new List<(string, string)>();
            if (!string.IsNullOrEmpty(framesCsv))
            {
                foreach (string token in framesCsv.Split(','))
                {
                    if (string.IsNullOrEmpty(token)) continue;
                    int dashIndex = token.LastIndexOf('-');
                    string rollsCsv = dashIndex >= 0 ? token.Substring(0, dashIndex) : token;
                    string total = dashIndex >= 0 ? token.Substring(dashIndex + 1) : "";
                    frames.Add((rollsCsv, total));
                }
            }

            rows.Add(new PlayerRow { Name = name, Frames = frames });
        }
        return rows;
    }

    private string BuildGrid(List<PlayerRow> rows, int frameCount, string currentName, int currentFrame, bool isFinished)
    {
        if (rows.Count == 0) return "";

        if (isFinished)
            rows.Sort((a, b) => TotalOf(b).CompareTo(TotalOf(a)));

        List<string> lines = new List<string>();

        // Header: blank name column + frame numbers 1..frameCount.
        List<string> headerCells = new List<string> { "".PadRight(_nameColumnWidth) };
        for (int f = 1; f <= frameCount; f++)
            headerCells.Add(f.ToString().PadRight(_frameColumnWidth));
        lines.Add(string.Join("", headerCells));

        foreach (PlayerRow row in rows)
        {
            bool isCurrentRow = row.Name == currentName;
            string namePart = FitWidth(row.Name, _nameColumnWidth);
            if (isCurrentRow) namePart = $"<b>{namePart}</b>";

            List<string> cells = new List<string> { namePart };
            for (int f = 1; f <= frameCount; f++)
            {
                string cell;
                if (f <= row.Frames.Count)
                    cell = RenderFrameCell(row.Frames[f - 1]);
                else
                    cell = "".PadRight(_frameColumnWidth);

                if (isCurrentRow && f == currentFrame)
                    cell = $"<color={_highlightColorHex}>{cell}</color>";

                cells.Add(cell);
            }
            lines.Add(string.Join("", cells));
        }

        return $"<mspace={_monospaceEm}>{string.Join("\n", lines)}</mspace>";
    }

    private static int TotalOf(PlayerRow row)
    {
        if (row.Frames.Count == 0) return 0;
        string total = row.Frames[row.Frames.Count - 1].total;
        return int.TryParse(total, out int value) ? value : 0;
    }

    // One frame's box: scoresheet-style marks (rolls) on the left, running
    // total right-aligned on the right — "score adding on the right" of
    // each frame, not just a single total column at the end of the row.
    private string RenderFrameCell((string rollsCsv, string total) frame)
    {
        List<int> rolls = new List<int>();
        foreach (string r in frame.rollsCsv.Split(';'))
            if (int.TryParse(r, out int v)) rolls.Add(v);

        string marks = string.Join(" ", ToMarks(rolls));
        string total = frame.total;

        if (string.IsNullOrEmpty(total))
            return FitWidth(marks, _frameColumnWidth);

        // Right-align the total, left-align the marks, at least one space
        // between them. If the marks are too wide to leave room for that
        // (e.g. "X X X" plus a 3-digit total in a narrow column), trim the
        // marks rather than the total — the running score is the more
        // important half to keep fully visible.
        int maxMarksWidth = Mathf.Max(_frameColumnWidth - total.Length - 1, 0);
        if (marks.Length > maxMarksWidth)
            marks = marks.Substring(0, maxMarksWidth);

        string cell = marks.PadRight(Mathf.Max(_frameColumnWidth - total.Length, 0)) + total;
        return FitWidth(cell, _frameColumnWidth);
    }

    // Converts a raw roll sequence into classic scoresheet notation: a 10
    // is always "X" (strike — including a fresh-rack bonus roll in the
    // last frame); two consecutive rolls summing to 10 (where the first
    // isn't itself a strike) render as "digit /" (spare); anything else is
    // just the pin count, with 0 shown as "-" (gutter). Works uniformly for
    // both regular 2-roll frames and the last frame's up-to-3-roll bonus
    // sequence, since a strike or spare always consumes exactly the rolls
    // it's paired with before the next mark starts.
    private static List<string> ToMarks(List<int> rolls)
    {
        List<string> marks = new List<string>();
        int i = 0;
        while (i < rolls.Count)
        {
            int r = rolls[i];
            if (r == 10)
            {
                marks.Add("X");
                i++;
            }
            else if (i + 1 < rolls.Count && r + rolls[i + 1] == 10)
            {
                marks.Add(r == 0 ? "-" : r.ToString());
                marks.Add("/");
                i += 2;
            }
            else
            {
                marks.Add(r == 0 ? "-" : r.ToString());
                i++;
            }
        }
        return marks;
    }

    private static string FitWidth(string s, int width)
    {
        if (s.Length > width) return s.Substring(0, width);
        return s.PadRight(width);
    }
}
