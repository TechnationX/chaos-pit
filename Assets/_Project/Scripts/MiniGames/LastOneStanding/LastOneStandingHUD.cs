// LastOneStandingHUD.cs
// Client-side HUD for Last One Standing.
// Displays: countdown timer, per-player alive/eliminated status, results screen.
// Driven entirely by LastOneStandingController via OnNetworkMessage.

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using TMPro;

namespace ChaosPit.Minigames.LastOneStanding
{
    public class LastOneStandingHUD : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────
        [Header("Timer")]
        [SerializeField] private TextMeshProUGUI _timerText;
        [SerializeField] private Color _timerNormalColor  = Color.white;
        [SerializeField] private Color _timerWarningColor = Color.red;
        [SerializeField] private float _warningThreshold  = 15f;

        [Header("Player Status Panel")]
        [SerializeField] private Transform                  _scoreRowParent;
        [SerializeField] private LastOneStandingScoreRow    _scoreRowPrefab;

        [Header("Elimination Banner")]
        [SerializeField] private GameObject        _eliminationBanner;
        [SerializeField] private TextMeshProUGUI   _eliminationText;
        [SerializeField] private float             _bannerDisplayDuration = 2.5f;

        [Header("Results")]
        [SerializeField] private GameObject        _resultsPanel;
        [SerializeField] private TextMeshProUGUI   _resultsText;
        [SerializeField] private TextMeshProUGUI   _resultsCountdownText;

        [Header("Alive Count")]
        [SerializeField] private TextMeshProUGUI   _aliveCountText;

        [Header("Between Round Countdown")]
        [SerializeField] private GameObject _countdownPanel;
        [SerializeField] private TextMeshProUGUI _countdownText;

        // ── Runtime ───────────────────────────────────────────────
        private float     _timeRemaining;
        private bool      _timerRunning;
        private Coroutine _timerCoroutine;
        private Coroutine _bannerCoroutine;

        private Dictionary<int, LastOneStandingScoreRow> _scoreRows = new();
        private int _totalPlayers;
        private int _aliveCount;

        // ── Round Lifecycle ───────────────────────────────────────

        public void OnRoundStart(float duration)
        {
            //Debug.Log($"[LOS HUD] OnRoundStart called, duration: {duration}");
            _timeRemaining = duration;
            _timerRunning  = true;

            //Debug.Log($"[LOS HUD] _timerText null: {_timerText == null}");
            //Debug.Log($"[LOS HUD] _resultsPanel null: {_resultsPanel == null}");

            if (_resultsPanel      != null) _resultsPanel.SetActive(false);
            if (_eliminationBanner != null) _eliminationBanner.SetActive(false);

            // Reset all rows to alive
            foreach (var row in _scoreRows.Values)
                row.ResetForNewRound();

            _aliveCount = _totalPlayers;
            UpdateAliveCount();

            if (_timerCoroutine != null) StopCoroutine(_timerCoroutine);
            _timerCoroutine = StartCoroutine(TimerCoroutine());
            //Debug.Log($"[LOS HUD] TimerCoroutine started");
        }

        public void OnRoundEnd()
        {
            _timerRunning = false;
            if (_timerCoroutine != null) { StopCoroutine(_timerCoroutine); _timerCoroutine = null; }
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

        public void InitScoreRows(Dictionary<int, string> nameMap)
        {
            ClearScoreRows();
            if (_scoreRowParent == null || _scoreRowPrefab == null) return;

            _totalPlayers = nameMap.Count;
            _aliveCount   = _totalPlayers;

            foreach (var kvp in nameMap)
            {
                LastOneStandingScoreRow row = Instantiate(_scoreRowPrefab, _scoreRowParent);
                row.Init(kvp.Value);
                _scoreRows[kvp.Key] = row;
            }

            UpdateAliveCount();
        }

        public void MarkPlayerEliminated(int playerId, int rank)
        {
            if (_scoreRows.TryGetValue(playerId, out LastOneStandingScoreRow row))
                row.SetEliminated(rank);

            _aliveCount = Mathf.Max(0, _aliveCount - 1);
            UpdateAliveCount();
        }

        public void ClearScoreRows()
        {
            foreach (var row in _scoreRows.Values)
                if (row != null) Destroy(row.gameObject);
            _scoreRows.Clear();
        }

        // ── Alive Count ───────────────────────────────────────────

        private void UpdateAliveCount()
        {
            if (_aliveCountText != null)
                _aliveCountText.text = $"{_aliveCount} / {_totalPlayers} ALIVE";
        }

        public void UpdatePlayerScore(int playerId, int score)
        {
            if (_scoreRows.TryGetValue(playerId, out LastOneStandingScoreRow row))
                row.UpdateScore(score); // color stays alive/eliminated based on existing state
        }

        // ── Elimination Banner ────────────────────────────────────

        /// Show brief "X was eliminated!" banner. Pass null name to show "You were eliminated!"
        public void ShowEliminationBanner(string playerName)
        {
            if (_eliminationBanner == null) return;
            if (_bannerCoroutine != null) StopCoroutine(_bannerCoroutine);
            _bannerCoroutine = StartCoroutine(EliminationBannerCoroutine(playerName));
        }

        private IEnumerator EliminationBannerCoroutine(string playerName)
        {
            if (_eliminationText != null)
                _eliminationText.text = string.IsNullOrEmpty(playerName)
                    ? "You were eliminated!"
                    : $"{playerName} was eliminated!";

            _eliminationBanner.SetActive(true);
            yield return new WaitForSeconds(_bannerDisplayDuration);
            _eliminationBanner.SetActive(false);
        }

        // ── Results ───────────────────────────────────────────────

        public void ShowClientResults(List<(string label, string name, string points, string level)> entries)
        {
            if (_resultsPanel != null) _resultsPanel.SetActive(true);
            if (_resultsText  == null) return;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("── RESULTS ──");
            foreach (var e in entries)
            {
                sb.AppendLine($"{e.label}  {e.name}");
                sb.AppendLine($"   +{e.points} pts  |  Lv {e.level}");
            }
            _resultsText.text = sb.ToString();
        }

        public void SetResultsCountdown(int seconds)
        {
            if (_resultsCountdownText != null)
                _resultsCountdownText.text = $"Returning in {seconds}...";
        }

        public void ClearResultsCountdown()
        {
            if (_resultsCountdownText != null)
                _resultsCountdownText.text = string.Empty;
        }

        public void ShowBetweenRoundCountdown(int seconds)
        {
            if (_countdownPanel != null) _countdownPanel.SetActive(true);
            if (_countdownText != null) _countdownText.text = $"Next round in {seconds}...";
        }

        public void HideBetweenRoundCountdown()
        {
            if (_countdownPanel != null) _countdownPanel.SetActive(false);
        }
    }
}
