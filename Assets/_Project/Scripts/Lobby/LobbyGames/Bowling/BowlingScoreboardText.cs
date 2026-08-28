// BowlingScoreboardText.cs
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Purely visual — parses BowlingGameController.ScoreboardSnapshot and
/// renders it as a live world-space full frame-by-frame scoreboard grid.
///
/// Phase 4 revision: this used to render everything as one TextMeshProUGUI
/// block using a monospace tag to fake column alignment. That couldn't
/// give real per-frame borders, a small-rolls-on-top/total-on-bottom
/// layout inside each frame box, or consistent spacing — so this version
/// instead BUILDS the grid out of real UI elements at runtime: one bordered
/// cell (a small Image "border" rect with an inset Image "fill" rect
/// inside it) per box, with TextMeshProUGUI labels placed inside. No new
/// prefabs needed — everything here is created with `new GameObject(...)`
/// and destroyed/rebuilt whenever the snapshot actually changes, same
/// "poll every frame, only rebuild on change" convention as before.
///
/// Column widths are FRACTIONS of this object's own RectTransform width
/// (read live via _root.rect.width), not fixed sizes — so the grid always
/// fills whatever panel it's placed in, regardless of what units that
/// panel's Canvas happens to use. Row heights below the header split the
/// remaining panel height evenly across however many players have joined.
///
/// Not a NetworkBehaviour — ScoreboardSnapshot is already a replicated
/// SyncVar<string> on BowlingGameController; this just reads that public
/// accessor locally on whichever client is looking at the board.
///
/// Parses the "STATE|current|playersBlock" format documented on
/// BowlingGameController._scoreboardSnapshot — see that comment for the
/// exact grammar and a worked example.
///
/// FINISHED state replaces the frame grid entirely with a winner banner
/// + final-standings list (see BuildWinnerScreen) for as long as
/// BowlingGameController.WinnerScreenDuration lasts server-side, then the
/// server auto-resets the lane back to WAITING and the grid comes back on
/// its own — nothing here needs to know that reset happened, WAITING was
/// already a normal state this script handles.
/// </summary>
[RequireComponent(typeof(RectTransform))]
public class BowlingScoreboardText : MonoBehaviour
{
    [Header("References")]
    [Tooltip("The lane's BowlingGameController to read the live scoreboard from.")]
    [SerializeField] private BowlingGameController _gameController;
    [Tooltip("Optional — leave unassigned to use TMP's default font asset.")]
    [SerializeField] private TMP_FontAsset _fontAsset;

    [Header("Column widths (fractions of this panel's width — should sum to well under 1; frame columns split whatever's left over evenly)")]
    [SerializeField] private float _nameColumnFraction = 0.17f;
    [SerializeField] private float _totalColumnFraction = 0.10f;

    [Tooltip("Player rows are sized as if this many players had joined, regardless of how many actually have — e.g. with 1 player joined and this at 4, that row still gets 1/4 of the available height instead of stretching to fill it. If more players than this join, rows shrink further to still fit everyone.")]
    [SerializeField] private int _assumedMaxPlayerRows = 4;

    [Header("Row heights (fractions of this panel's height — NOT absolute units. This panel's RectTransform turned out to use a much larger/smaller coordinate space than assumed on the first pass, which is exactly why fixed-unit values silently collapsed to nothing — see the class comment)")]
    [SerializeField] private float _statusRowHeightFraction = 0.08f;
    [SerializeField] private float _headerRowHeightFraction = 0.06f;

    [Header("Cell style")]
    [Tooltip("Border thickness as a fraction of the panel's height — scales with whatever coordinate space this panel actually uses instead of assuming one.")]
    [SerializeField] private float _borderThicknessFraction = 0.006f;
    [SerializeField] private Color _borderColor = new Color(0.05f, 0.06f, 0.12f);
    [SerializeField] private Color _activeBorderColor = new Color(1f, 0.85f, 0.3f);
    [SerializeField] private Color _headerFillColor = new Color(0.14f, 0.16f, 0.26f);
    [SerializeField] private Color _frameFillColor = new Color(0.20f, 0.22f, 0.32f);
    [SerializeField] private Color _activeFrameFillColor = new Color(0.30f, 0.50f, 0.80f);
    [SerializeField] private Color _totalFillColor = new Color(0.10f, 0.12f, 0.22f);
    [SerializeField] private Color _winnerBannerFillColor = new Color(0.85f, 0.65f, 0.10f);
    [SerializeField] private Color _winnerBannerTextColor = new Color(0.08f, 0.06f, 0.02f);
    [SerializeField] private Color[] _nameBadgeColors = new Color[]
    {
        new Color(0.72f, 0.15f, 0.15f), // red
        new Color(0.50f, 0.32f, 0.12f), // brown
        new Color(0.85f, 0.50f, 0.10f), // orange
        new Color(0.15f, 0.40f, 0.70f), // blue
    };

    [Header("Text style")]
    [SerializeField] private Color _statusTextColor = Color.white;
    [SerializeField] private Color _headerTextColor = Color.white;
    [SerializeField] private Color _nameTextColor = Color.white;
    [SerializeField] private Color _rollTextColor = new Color(0.85f, 0.85f, 0.9f);
    [SerializeField] private Color _frameTotalTextColor = Color.white;
    [SerializeField] private Color _grandTotalTextColor = new Color(1f, 0.85f, 0.3f);

    [Tooltip("Font sizes below are each a fraction of THEIR OWN row's height, not an absolute size — e.g. the name badge's font is _nameFontScale * that row's height. TMP font size is expressed in the same local-space units as the RectTransform it's on, so an absolute number is only ever right for one specific coordinate scale; a fraction of the actual row height is correct no matter what that scale turns out to be.")]
    [SerializeField] private float _statusFontScale = 0.55f;
    [SerializeField] private float _headerFontScale = 0.55f;
    [SerializeField] private float _nameFontScale = 0.28f;
    [SerializeField] private float _rollFontScale = 0.20f;
    [SerializeField] private float _frameTotalFontScale = 0.34f;
    [SerializeField] private float _grandTotalFontScale = 0.34f;

    [Header("Winner screen (shown instead of the grid once the game finishes)")]
    [Tooltip("Fraction of the space below the status line given to the big winner banner — the rest goes to the final-standings list below it.")]
    [SerializeField] private float _winnerBannerHeightFraction = 0.30f;
    [SerializeField] private float _winnerBannerFontScale = 0.32f;
    [SerializeField] private float _standingsRankColumnFraction = 0.08f;
    [SerializeField] private float _standingsFontScale = 0.30f;
    [Tooltip("Fraction of the space below the banner given to the live \"next game in Ns\" countdown row, taken out of what the standings list would otherwise get.")]
    [SerializeField] private float _countdownRowHeightFraction = 0.08f;
    [SerializeField] private float _countdownFontScale = 0.5f;
    [SerializeField] private Color _countdownTextColor = new Color(0.85f, 0.85f, 0.9f);

    private RectTransform _root;

    // Computed once per Rebuild() from the panel's actual measured size —
    // see CreateCell, which reads this instead of a hardcoded constant.
    private float _liveBorderThickness;

    // Sentinel so the very first Update() always applies, even if the real
    // snapshot happens to be an empty string.
    private string _lastAppliedSnapshot = "\0";

    // FrameCount is its own SyncVar, NOT encoded into ScoreboardSnapshot —
    // so cycling it via the panel's Cycle Frame Count button (pre-start,
    // after players have already joined) changes the number of columns
    // this grid should have, but never touches the snapshot string, and a
    // snapshot-only change check would silently miss it entirely. Polled
    // separately here, same convention BowlingPanelText already uses for
    // its own "Frames: N" readout.
    private int _lastAppliedFrameCount = -1;

    // Live-ticking "next game in Ns" label on the winner screen. Cached
    // across frames so Update() can rewrite just its text every frame
    // without a full Rebuild() — Rebuild only runs when the snapshot or
    // FrameCount actually change, and time passing alone never touches
    // either. Nulled out at the top of every Rebuild() (ClearChildren
    // just destroyed it) and reassigned by BuildWinnerScreen if the new
    // state is still FINISHED.
    private TextMeshProUGUI _countdownLabel;

    // Seeded off THIS client's own first-detected transition into
    // FINISHED, not a server timestamp — Time.time isn't comparable across
    // peers (each process's clock starts at its own launch), but every
    // peer sees the snapshot flip to FINISHED within a network tick of
    // every other, close enough for a seconds-level countdown display.
    private float _winnerScreenStartTime = -1f;
    private bool _wasFinished;

    private void Awake()
    {
        _root = GetComponent<RectTransform>();

        // If this GameObject still has the old single-text-block component
        // from before this rewrite, disable it rather than requiring it be
        // manually removed in the Editor — the generated grid replaces it.
        TextMeshProUGUI legacyText = GetComponent<TextMeshProUGUI>();
        if (legacyText != null) legacyText.enabled = false;
    }

    private void Update()
    {
        if (_gameController == null || _root == null) return;

        string snapshot = _gameController.ScoreboardSnapshot;
        int frameCount = _gameController.FrameCount;

        // ScoreboardSnapshot is a SyncVar<string> — a freshly-joined client
        // reads it as null for the first frame or two, before the server's
        // initial sync arrives. Treat that as "not finished" rather than
        // letting StartsWith() null-ref (was throwing repeatedly on join).
        snapshot ??= "";

        bool isFinishedNow = snapshot.StartsWith("FINISHED");
        if (isFinishedNow && !_wasFinished) _winnerScreenStartTime = Time.time;
        _wasFinished = isFinishedNow;

        if (snapshot != _lastAppliedSnapshot || frameCount != _lastAppliedFrameCount)
        {
            float panelWidth = _root.rect.width;
            float panelHeight = _root.rect.height;
            if (panelWidth > 0f && panelHeight > 0f) // else not laid out yet — retry next frame
            {
                _lastAppliedSnapshot = snapshot;
                _lastAppliedFrameCount = frameCount;
                Rebuild(snapshot, panelWidth, panelHeight);
            }
        }

        // Ticks the countdown label every frame, independent of the
        // structural rebuild above — see _countdownLabel's field comment.
        if (isFinishedNow && _countdownLabel != null)
        {
            float remaining = Mathf.Max(0f, _gameController.WinnerScreenDuration - (Time.time - _winnerScreenStartTime));
            _countdownLabel.text = $"Next game in {Mathf.CeilToInt(remaining)}...";
        }
    }

    // ─── Snapshot parsing (unchanged grammar from the text-block version) ──

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

    // Mirrors BowlingFrameScorer.GetCurrentTotal() server-side: walk frames
    // from the end backward and return the first one that actually
    // resolved a running total, so a total is available even mid-frame
    // (e.g. right after a strike, before its bonus rolls land).
    private static string CurrentTotalOf(PlayerRow row)
    {
        for (int i = row.Frames.Count - 1; i >= 0; i--)
        {
            string t = row.Frames[i].total;
            if (!string.IsNullOrEmpty(t)) return t;
        }
        return "";
    }

    // Converts a raw roll sequence into classic scoresheet notation: a 10
    // is always "X" (strike); two consecutive rolls summing to 10 (where
    // the first isn't itself a strike) render as "digit /" (spare);
    // anything else is just the pin count, with 0 shown as "-" (gutter).
    // Works uniformly for both regular 2-roll frames and the last frame's
    // up-to-3-roll bonus sequence.
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

    // ─── Grid construction ──────────────────────────────────────────────

    private void Rebuild(string snapshot, float panelWidth, float panelHeight)
    {
        ClearChildren();
        _countdownLabel = null; // ClearChildren just destroyed it, if it existed — BuildWinnerScreen recreates it below if we're still/again FINISHED
        _liveBorderThickness = panelHeight * _borderThicknessFraction;

        string[] top = snapshot.Split('|');
        if (top.Length < 3) return; // unrecognized — leave blank rather than crash

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

        string statusText;
        if (state == "WAITING")
            statusText = rows.Count == 0 ? "Waiting for players" : "Waiting for players — press Start when ready";
        else if (state == "FINISHED")
            statusText = "Game Over!";
        else
            statusText = currentName != null ? $"Frame {currentFrame} — {currentName}'s turn" : "Bowling";

        float statusRowHeight = panelHeight * _statusRowHeightFraction;
        float headerRowHeight = panelHeight * _headerRowHeightFraction;

        float y = 0f;
        CreateStatusLabel(statusText, panelWidth, y, statusRowHeight);
        y += statusRowHeight;

        if (rows.Count == 0) return; // nothing joined yet — status line is enough

        if (state == "FINISHED")
        {
            // Winner screen replaces the frame grid entirely once the game
            // is over — the grid comes back on its own once the server
            // auto-resets the lane back to WAITING (see
            // BowlingGameController.ServerFinishGameRoutine), which just
            // flows through the normal WAITING path below since nothing
            // client-side needs to know the reset happened.
            rows.Sort((a, b) => ParseInt(CurrentTotalOf(b)).CompareTo(ParseInt(CurrentTotalOf(a))));
            BuildWinnerScreen(rows, panelWidth, panelHeight, y);
            return;
        }

        float nameWidth = panelWidth * _nameColumnFraction;
        float totalWidth = panelWidth * _totalColumnFraction;
        float framesWidth = Mathf.Max(panelWidth - nameWidth - totalWidth, 0f);
        float frameColWidth = framesWidth / frameCount;

        CreateHeaderRow(frameCount, nameWidth, frameColWidth, totalWidth, y, headerRowHeight);
        y += headerRowHeight;

        float remainingHeight = Mathf.Max(panelHeight - y, 0f);
        float rowHeight = remainingHeight / Mathf.Max(_assumedMaxPlayerRows, rows.Count);

        for (int i = 0; i < rows.Count; i++)
        {
            CreatePlayerRow(rows[i], i, frameCount, nameWidth, frameColWidth, totalWidth, y, rowHeight, currentName, currentFrame);
            y += rowHeight;
        }
    }

    private void CreateHeaderRow(int frameCount, float nameWidth, float frameColWidth, float totalWidth, float y, float headerRowHeight)
    {
        float fontSize = headerRowHeight * _headerFontScale;

        // Blank corner cell over the name column — keeps the grid lines
        // continuous even though it has no label.
        CreateCell("HeaderCorner", 0f, y, nameWidth, headerRowHeight, _borderColor, _headerFillColor);

        for (int f = 1; f <= frameCount; f++)
        {
            RectTransform fill = CreateCell($"HeaderFrame{f}", nameWidth + (f - 1) * frameColWidth, y, frameColWidth, headerRowHeight, _borderColor, _headerFillColor);
            CreateLabel(fill, "Num", f.ToString(), fontSize, _headerTextColor, TextAlignmentOptions.Center, bold: true);
        }

        RectTransform totalFill = CreateCell("HeaderTotal", nameWidth + frameCount * frameColWidth, y, totalWidth, headerRowHeight, _borderColor, _headerFillColor);
        CreateLabel(totalFill, "Label", "TOTAL", fontSize, _headerTextColor, TextAlignmentOptions.Center, bold: true);
    }

    private void CreatePlayerRow(PlayerRow row, int rowIndex, int frameCount, float nameWidth, float frameColWidth, float totalWidth, float y, float rowHeight, string currentName, int currentFrame)
    {
        bool isCurrentRow = row.Name == currentName;
        float nameFontSize = rowHeight * _nameFontScale;
        float rollFontSize = rowHeight * _rollFontScale;
        float frameTotalFontSize = rowHeight * _frameTotalFontScale;
        float grandTotalFontSize = rowHeight * _grandTotalFontScale;

        Color badgeColor = _nameBadgeColors.Length > 0 ? _nameBadgeColors[rowIndex % _nameBadgeColors.Length] : _headerFillColor;
        RectTransform nameFill = CreateCell("Name", 0f, y, nameWidth, rowHeight, isCurrentRow ? _activeBorderColor : _borderColor, badgeColor);
        CreateLabel(nameFill, "Label", row.Name.ToUpperInvariant(), nameFontSize, _nameTextColor, TextAlignmentOptions.Center, bold: true);

        for (int f = 1; f <= frameCount; f++)
        {
            bool isActiveCell = isCurrentRow && f == currentFrame;
            Color fillColor = isActiveCell ? _activeFrameFillColor : _frameFillColor;
            Color borderColor = isActiveCell ? _activeBorderColor : _borderColor;

            RectTransform cellFill = CreateCell($"Frame{f}", nameWidth + (f - 1) * frameColWidth, y, frameColWidth, rowHeight, borderColor, fillColor);

            if (f > row.Frames.Count) continue; // not reached yet — empty box, grid lines only

            (string rollsCsv, string total) frame = row.Frames[f - 1];
            List<int> rolls = new List<int>();
            foreach (string r in frame.rollsCsv.Split(';'))
                if (int.TryParse(r, out int v)) rolls.Add(v);
            string rollsText = string.Join(" ", ToMarks(rolls));

            // Top half of the cell: small roll marks. Bottom half: the
            // frame's running total, larger, blank until it resolves.
            RectTransform rollsArea = CreateFractionChild(cellFill, "Rolls", 0f, 0.55f, 1f, 1f);
            CreateLabel(rollsArea, "Text", rollsText, rollFontSize, _rollTextColor, TextAlignmentOptions.Center);

            RectTransform totalArea = CreateFractionChild(cellFill, "Total", 0f, 0f, 1f, 0.55f);
            CreateLabel(totalArea, "Text", frame.total, frameTotalFontSize, _frameTotalTextColor, TextAlignmentOptions.Center, bold: true);
        }

        string grandTotalText = CurrentTotalOf(row);
        if (string.IsNullOrEmpty(grandTotalText)) grandTotalText = "0";
        RectTransform totalFill = CreateCell("Total", nameWidth + frameCount * frameColWidth, y, totalWidth, rowHeight, isCurrentRow ? _activeBorderColor : _borderColor, _totalFillColor);
        CreateLabel(totalFill, "Label", grandTotalText, grandTotalFontSize, _grandTotalTextColor, TextAlignmentOptions.Center, bold: true);
    }

    // Winner banner up top (rows are already sorted descending, so
    // rows[0] is the winner) plus a compact final-standings list below —
    // shown for BowlingGameController's _winnerScreenDuration before the
    // server auto-resets the lane back to WAITING.
    private void BuildWinnerScreen(List<PlayerRow> rows, float panelWidth, float panelHeight, float y)
    {
        float remainingHeight = Mathf.Max(panelHeight - y, 0f);
        float bannerHeight = remainingHeight * _winnerBannerHeightFraction;
        float countdownRowHeight = remainingHeight * _countdownRowHeightFraction;
        float standingsHeight = Mathf.Max(remainingHeight - bannerHeight - countdownRowHeight, 0f);

        PlayerRow winner = rows[0];
        string winnerTotal = CurrentTotalOf(winner);
        if (string.IsNullOrEmpty(winnerTotal)) winnerTotal = "0";

        RectTransform bannerFill = CreateCell("WinnerBanner", 0f, y, panelWidth, bannerHeight, _activeBorderColor, _winnerBannerFillColor);
        CreateLabel(bannerFill, "Text", $"{winner.Name.ToUpperInvariant()} WINS! {winnerTotal} PTS", bannerHeight * _winnerBannerFontScale, _winnerBannerTextColor, TextAlignmentOptions.Center, bold: true);
        y += bannerHeight;

        // Plain rect + label, no border/fill — same pattern as
        // CreateStatusLabel. Update() rewrites this label's text every
        // frame while FINISHED (see the isFinishedNow block there); this
        // call just creates it and seeds the first frame's text.
        RectTransform countdownRect = CreateRect(_root, "Countdown", 0f, y, panelWidth, countdownRowHeight);
        _countdownLabel = CreateLabel(countdownRect, "Text", "", countdownRowHeight * _countdownFontScale, _countdownTextColor, TextAlignmentOptions.Center);
        y += countdownRowHeight;

        if (rows.Count <= 1 || standingsHeight <= 0f) return; // nothing extra to rank with only one player

        float rankWidth = panelWidth * _standingsRankColumnFraction;
        float scoreWidth = panelWidth * _totalColumnFraction;
        float nameWidth = Mathf.Max(panelWidth - rankWidth - scoreWidth, 0f);
        float rowHeight = standingsHeight / rows.Count;
        float fontSize = rowHeight * _standingsFontScale;

        for (int i = 0; i < rows.Count; i++)
        {
            PlayerRow row = rows[i];
            string total = CurrentTotalOf(row);
            if (string.IsNullOrEmpty(total)) total = "0";

            RectTransform rankFill = CreateCell($"Rank{i}", 0f, y, rankWidth, rowHeight, _borderColor, _headerFillColor);
            CreateLabel(rankFill, "Text", (i + 1).ToString(), fontSize, _headerTextColor, TextAlignmentOptions.Center, bold: true);

            Color badgeColor = _nameBadgeColors.Length > 0 ? _nameBadgeColors[i % _nameBadgeColors.Length] : _headerFillColor;
            RectTransform nameFill = CreateCell($"StandingName{i}", rankWidth, y, nameWidth, rowHeight, _borderColor, badgeColor);
            CreateLabel(nameFill, "Text", row.Name.ToUpperInvariant(), fontSize, _nameTextColor, TextAlignmentOptions.Center, bold: true);

            RectTransform scoreFill = CreateCell($"StandingScore{i}", rankWidth + nameWidth, y, scoreWidth, rowHeight, _borderColor, _totalFillColor);
            CreateLabel(scoreFill, "Text", total, fontSize, _grandTotalTextColor, TextAlignmentOptions.Center, bold: true);

            y += rowHeight;
        }
    }

    private void CreateStatusLabel(string text, float panelWidth, float y, float statusRowHeight)
    {
        RectTransform rt = CreateRect(_root, "Status", 0f, y, panelWidth, statusRowHeight);
        CreateLabel(rt, "Text", text, statusRowHeight * _statusFontScale, _statusTextColor, TextAlignmentOptions.Center, bold: true);
    }

    // ─── Low-level UI helpers ───────────────────────────────────────────

    // Top-left-anchored, fixed-size rect at a given offset from this
    // panel's top-left corner — the basic building block every cell and
    // label position is computed from.
    private static RectTransform CreateRect(Transform parent, string name, float x, float yTop, float width, float height)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        RectTransform rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(x, -yTop);
        rt.sizeDelta = new Vector2(width, height);
        return rt;
    }

    // A child that stretches to fill its parent exactly — used for the
    // inset "fill" rect inside a bordered cell, and for anything that
    // should simply cover its cell (like a label).
    private static RectTransform CreateStretchChild(Transform parent, string name)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        RectTransform rt = go.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        return rt;
    }

    // A child positioned by fractional anchors within its parent (0..1 on
    // both axes) — used to split a frame cell into a "rolls" strip on top
    // and a "total" strip on the bottom without any absolute-size math.
    private static RectTransform CreateFractionChild(Transform parent, string name, float xMin, float yMin, float xMax, float yMax)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        RectTransform rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(xMin, yMin);
        rt.anchorMax = new Vector2(xMax, yMax);
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        return rt;
    }

    // One "bordered box": an outer rect tinted with borderColor, and an
    // inset child rect (inset by _liveBorderThickness on every side, a
    // fraction of the panel's actual measured height — see Rebuild())
    // tinted with fillColor sitting on top of it — the visible gap between
    // the two is what reads as a grid line, no sprite/border asset needed.
    // Returns the fill rect, which is what content gets parented into.
    private RectTransform CreateCell(string name, float x, float yTop, float width, float height, Color borderColor, Color fillColor)
    {
        RectTransform outer = CreateRect(_root, name, x, yTop, width, height);
        outer.gameObject.AddComponent<Image>().color = borderColor;

        RectTransform fill = CreateStretchChild(outer, "Fill");
        fill.offsetMin = new Vector2(_liveBorderThickness, _liveBorderThickness);
        fill.offsetMax = new Vector2(-_liveBorderThickness, -_liveBorderThickness);
        fill.gameObject.AddComponent<Image>().color = fillColor;

        return fill;
    }

    private TextMeshProUGUI CreateLabel(Transform parent, string name, string text, float fontSize, Color color, TextAlignmentOptions align, bool bold = false)
    {
        RectTransform rt = CreateStretchChild(parent, name);
        TextMeshProUGUI tmp = rt.gameObject.AddComponent<TextMeshProUGUI>();

        // Font/style MUST be set before .text — a freshly AddComponent'd
        // TextMeshProUGUI builds its mesh/material off whatever font is
        // assigned at the moment .text is set. Setting .font afterward
        // (the previous order) left the initial mesh built against no
        // font at all, which is why every label rendered as nothing even
        // with a font asset assigned in the Inspector.
        if (_fontAsset != null) tmp.font = _fontAsset;
        tmp.fontSize = fontSize;
        tmp.color = color;
        tmp.alignment = align;
        tmp.enableWordWrapping = false;
        tmp.overflowMode = TextOverflowModes.Truncate;
        if (bold) tmp.fontStyle = FontStyles.Bold;

        tmp.text = text;
        tmp.ForceMeshUpdate(); // belt-and-suspenders: guarantees the mesh is built now, not on some later canvas update pass

        return tmp;
    }

    private void ClearChildren()
    {
        for (int i = _root.childCount - 1; i >= 0; i--)
            Destroy(_root.GetChild(i).gameObject);
    }

    private static int ParseInt(string s) => int.TryParse(s, out int v) ? v : 0;
}
