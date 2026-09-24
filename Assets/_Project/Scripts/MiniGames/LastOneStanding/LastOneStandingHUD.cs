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
        [SerializeField] private TextMeshProUGUI _roundInfoText;
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
        private int _totalRounds;
        private int _currentRound;

        private Dictionary<int, LastOneStandingScoreRow> _scoreRows = new();
        private int _totalPlayers;
        private int _aliveCount;

        // Remember whether the elimination banner / between-round countdown
        // panel were actually up right before SetInRoundHudVisible(false)
        // forced them down for the results screen, so restoring visibility
        // doesn't force either one back ON when it wasn't showing, or leave
        // it off when it was.
        private bool _eliminationBannerWasActive;
        private bool _countdownPanelWasActive;

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
            if (_timerText != null) _timerText.text = "0:00";
        }

        public void SetRoundInfo(int currentRound, int totalRounds)
        {
            _currentRound = currentRound;
            _totalRounds = totalRounds;
            UpdateRoundDisplay();
        }

        private void UpdateRoundDisplay()
        {
            if (_roundInfoText != null)
                _roundInfoText.text = $"Round {_currentRound} of {_totalRounds}";
        }

        // ── Timer ─────────────────────────────────────────────────

        private IEnumerator TimerCoroutine()
        {
            while (_timerRunning && _timeRemaining > 0f)
            {
                _timeRemaining -= Time.deltaTime;
                if (_timeRemaining < 0f) _timeRemaining = 0f;

                // Optional background timer display
                if (_timerText != null)
                {
                    int mins = Mathf.FloorToInt(_timeRemaining / 60f);
                    int secs = Mathf.FloorToInt(_timeRemaining % 60f);
                    _timerText.text = $"{mins}:{secs:00}";
                    _timerText.color = _timeRemaining <= _warningThreshold
                        ? _timerWarningColor : _timerNormalColor;
                }

                // Flash round info text as warning when time is low
                if (_roundInfoText != null)
                    _roundInfoText.color = _timeRemaining <= _warningThreshold
                        ? _timerWarningColor : _timerNormalColor;

                yield return null;
            }
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

        // Hides every piece of the in-round HUD — timer, round info, alive
        // count, the alive/eliminated status rows, and any elimination
        // banner/between-round countdown that happened to still be up —
        // while the shared results screen is up (see
        // LastOneStandingController.OnShowResults). Was SetScorePanelVisible,
        // which only ever touched the status rows and left the timer/round
        // info ticking away visibly underneath the results screen. Renamed
        // and broadened to match ThiefsMarketHUD/JinxedHUD/PaintTheTownHUD.
        // NOTE: this HUD's own _resultsPanel/_resultsText fields are
        // currently unused dead code (nothing calls ShowClientResults) — if
        // that ever changes, this method must NOT touch them, the same way
        // JinxedHUD's version carefully leaves its own results panel alone.
        public void SetInRoundHudVisible(bool visible)
        {
            if (_timerText != null)
                _timerText.gameObject.SetActive(visible);

            if (_roundInfoText != null)
                _roundInfoText.gameObject.SetActive(visible);

            if (_scoreRowParent != null)
                _scoreRowParent.gameObject.SetActive(visible);

            if (_aliveCountText != null)
                _aliveCountText.gameObject.SetActive(visible);

            if (_eliminationBanner != null)
            {
                if (!visible)
                {
                    _eliminationBannerWasActive = _eliminationBanner.activeSelf;
                    _eliminationBanner.SetActive(false);
                }
                else
                {
                    _eliminationBanner.SetActive(_eliminationBannerWasActive);
                }
            }

            if (_countdownPanel != null)
            {
                if (!visible)
                {
                    _countdownPanelWasActive = _countdownPanel.activeSelf;
                    _countdownPanel.SetActive(false);
                }
                else
                {
                    _countdownPanel.SetActive(_countdownPanelWasActive);
                }
            }
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

        // This whole GameObject starts inactive by default in the scene (see
        // LastOneStandingScene.unity) — otherwise it renders for anyone who
        // has this scene loaded at all, including an uninvolved host, since
        // the scene stays loaded server-side regardless of participation.
        // Called from LastOneStandingController.ClientInit(), which only
        // ever runs on an actual participant's own process (see
        // RpcInitMinigame's comment in GameRoomManager.cs), so this only
        // ever shows the HUD to a real player.
        public void ShowHUD()
        {
            gameObject.SetActive(true);
        }
    }
}
