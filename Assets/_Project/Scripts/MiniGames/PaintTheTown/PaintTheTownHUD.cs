// PaintTheTownHUD.cs
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using TMPro;

namespace ChaosPit.Minigames.PaintTheTown
{
    /// <summary>
    /// Client-side HUD for Paint the Town.
    /// Displays: countdown timer, per-player tile counts, round-end message.
    ///
    /// Driven by PaintTheTownNetwork RPCs — no direct server communication.
    /// All values received, never computed locally (except timer visual interpolation).
    /// </summary>
    public class PaintTheTownHUD : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────
        [Header("Timer")]
        [SerializeField] private TextMeshProUGUI _timerText;
        [SerializeField] private Color _timerNormalColor  = Color.white;
        [SerializeField] private Color _timerWarningColor = Color.red;
        [SerializeField] private float _warningThreshold  = 15f;

        [Header("Score Panel")]
        [SerializeField] private Transform         _scoreRowParent;
        [SerializeField] private PaintScoreRow     _scoreRowPrefab;

        [Header("Round End")]
        [SerializeField] private GameObject        _roundEndPanel;
        [SerializeField] private TextMeshProUGUI   _roundEndText;

        // ── Runtime State ─────────────────────────────────────────
        private float     _timeRemaining;
        private bool      _timerRunning;
        private Coroutine _timerCoroutine;

        private Dictionary<int, PaintScoreRow> _scoreRows = new();

        // ── Round Lifecycle ───────────────────────────────────────

        public void OnRoundStart(float duration)
        {
            _timeRemaining = duration;
            _timerRunning  = true;

            if (_roundEndPanel != null)
                _roundEndPanel.SetActive(false);

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

            if (_roundEndPanel != null)
                _roundEndPanel.SetActive(true);

            if (_roundEndText != null)
                _roundEndText.text = "Round Over!";
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
        /// Called after color map is set. Initializes one row per player.
        /// playerNames: playerId → display name
        /// colorMap: playerId → Color
        /// </summary>
        public void InitScoreRows(Dictionary<int, string> playerNames, Dictionary<int, Color> colorMap)
        {
            if (_scoreRowParent == null || _scoreRowPrefab == null) return;

            foreach (var kvp in playerNames)
            {
                int    playerId = kvp.Key;
                string name     = kvp.Value;
                Color  color    = colorMap.TryGetValue(playerId, out Color c) ? c : Color.white;

                PaintScoreRow row = Instantiate(_scoreRowPrefab, _scoreRowParent);
                row.Init(name, color);
                _scoreRows[playerId] = row;
            }
        }

        /// <summary>Called each batch sync to update displayed tile counts.</summary>
        public void UpdateTileCounts(Dictionary<int, int> countMap)
        {
            foreach (var kvp in countMap)
            {
                if (_scoreRows.TryGetValue(kvp.Key, out PaintScoreRow row))
                    row.SetCount(kvp.Value);
            }
        }

        // ── Cleanup ───────────────────────────────────────────────

        public void ClearScoreRows()
        {
            foreach (var row in _scoreRows.Values)
            {
                if (row != null) Destroy(row.gameObject);
            }
            _scoreRows.Clear();
        }
    }
}
