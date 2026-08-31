// TemplateHUD.cs
// TEMPLATE — Rename class and namespace to match your minigame.
// Wire all references in the Inspector on your MinigameHUD canvas.

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using TMPro;

namespace ChaosPit.Minigames.Template
{
    /// <summary>
    /// Client-side HUD for the template minigame.
    /// Displays: countdown timer, per-player score rows, results screen.
    ///
    /// Driven entirely by TemplateController via OnNetworkMessage — no direct server calls.
    /// </summary>
    public class TemplateHUD : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────
        [Header("Timer")]
        [SerializeField] private TextMeshProUGUI _timerText;
        [SerializeField] private Color _timerNormalColor  = Color.white;
        [SerializeField] private Color _timerWarningColor = Color.red;
        [SerializeField] private float _warningThreshold  = 15f;

        [Header("Score Panel")]
        [SerializeField] private Transform     _scoreRowParent;
        [SerializeField] private TemplateScoreRow _scoreRowPrefab;

        // Results display now lives on the shared ResultsCanvas.prefab /
        // ResultsScreenUI (see MiniGameController.ShowResults /
        // ShowResultsClientOnly) — this HUD no longer owns a results panel.

        // ── Runtime State ─────────────────────────────────────────
        private float     _timeRemaining;
        private bool      _timerRunning;
        private Coroutine _timerCoroutine;

        private Dictionary<int, TemplateScoreRow> _scoreRows = new();

        // ── Round Lifecycle ───────────────────────────────────────

        public void OnRoundStart(float duration)
        {
            _timeRemaining = duration;
            _timerRunning  = true;

            if (_timerCoroutine != null) StopCoroutine(_timerCoroutine);
            _timerCoroutine = StartCoroutine(TimerCoroutine());
        }

        public void OnRoundEnd()
        {
            _timerRunning = false;

            if (_timerCoroutine != null)
            {
                StopCoroutine(_timerCoroutine);
                _timerCoroutine = null;
            }

            SetTimerDisplay(0f);
        }

        // ── Timer ─────────────────────────────────────────────────

        private IEnumerator TimerCoroutine()
        {
            while (_timerRunning && _timeRemaining > 0f)
            {
                _timeRemaining -= Time.deltaTime;
                if (_timeRemaining < 0f) _timeRemaining = 0f;
                SetTimerDisplay(_timeRemaining);
                yield return null;
            }
        }

        private void SetTimerDisplay(float seconds)
        {
            if (_timerText == null) return;
            int mins = Mathf.FloorToInt(seconds / 60f);
            int secs = Mathf.FloorToInt(seconds % 60f);
            _timerText.text  = $"{mins}:{secs:00}";
            _timerText.color = seconds <= _warningThreshold ? _timerWarningColor : _timerNormalColor;
        }

        // ── Score Rows ────────────────────────────────────────────

        /// <summary>
        /// Initialize one row per player. Called after player identity arrives via tmpl_players.
        /// nameMap: playerId → display name
        /// TODO: extend signature if you need per-player colors (see PaintTheTownHUD for reference)
        /// </summary>
        public void InitScoreRows(Dictionary<int, string> nameMap)
        {
            ClearScoreRows();
            if (_scoreRowParent == null || _scoreRowPrefab == null) return;

            foreach (var kvp in nameMap)
            {
                TemplateScoreRow row = Instantiate(_scoreRowPrefab, _scoreRowParent);
                row.Init(kvp.Value);
                _scoreRows[kvp.Key] = row;
            }
        }

        /// <summary>
        /// Update a single score value. Call this each batch sync from the controller.
        /// TODO: replace int score with whatever metric your game tracks per player
        /// </summary>
        public void UpdateScore(int playerId, int score)
        {
            if (_scoreRows.TryGetValue(playerId, out TemplateScoreRow row))
                row.SetScore(score);
        }

        /// <summary>Update all scores at once from a dictionary.</summary>
        public void UpdateScores(Dictionary<int, int> scoreMap)
        {
            foreach (var kvp in scoreMap)
                UpdateScore(kvp.Key, kvp.Value);
        }

        public void ClearScoreRows()
        {
            foreach (var row in _scoreRows.Values)
                if (row != null) Destroy(row.gameObject);
            _scoreRows.Clear();
        }

    }
}
