// JinxedHUD.cs
// Client-side HUD for Jinxed minigame.
// Displays: round timer, per-player status rows, tag prompt, cooldown bar,
// jinx/tag banner, round info, and results screen.
// Driven by JinxedController via OnNetworkMessage.

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace ChaosPit.Minigames.Jinxed
{
    public class JinxedHUD : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────
        [Header("Timer")]
        [SerializeField] private TextMeshProUGUI _timerText;
        [SerializeField] private Color _timerNormalColor = Color.white;
        [SerializeField] private Color _timerWarningColor = Color.red;
        [SerializeField] private float _warningThreshold = 15f;

        [Header("Round Info")]
        [SerializeField] private TextMeshProUGUI _roundText;

        [Header("Player Status Panel")]
        [SerializeField] private Transform _scoreRowParent;
        [SerializeField] private JinxedScoreRow _scoreRowPrefab;

        [Header("Banner")]
        [SerializeField] private GameObject _bannerRoot;
        [SerializeField] private TextMeshProUGUI _bannerText;
        [SerializeField] private float _bannerDuration = 2.5f;

        [Header("Results")]
        [SerializeField] private GameObject _resultsPanel;
        [SerializeField] private TextMeshProUGUI _resultsText;
        [SerializeField] private TextMeshProUGUI _resultsCountdownText;

        // ── Runtime ───────────────────────────────────────────────
        private Dictionary<int, JinxedScoreRow> _scoreRows = new();

        private float _cooldownDuration = 0f;
        private float _cooldownRemaining = 0f;
        private bool _onCooldown = false;

        private Coroutine _bannerCoroutine;

        // ── Unity ─────────────────────────────────────────────────

        private void Update()
        {
            if (!_onCooldown) return;

            _cooldownRemaining -= Time.deltaTime;
         
            if (_cooldownRemaining <= 0f)
                ClearCooldown();
        }

        // ── Round Lifecycle ───────────────────────────────────────

        public void OnRoundStart(int roundNumber, int totalRounds, int startingJinxedId)
        {
            if (_resultsPanel != null) _resultsPanel.SetActive(false);
            if (_bannerRoot != null) _bannerRoot.SetActive(false);
            
            if (_roundText != null)
                _roundText.text = $"Round {roundNumber} / {totalRounds}";

            foreach (var row in _scoreRows.Values)
                row.ResetForNewRound();

            // Mark starting jinxed player
            if (_scoreRows.TryGetValue(startingJinxedId, out JinxedScoreRow jinxedRow))
                jinxedRow.SetStatus(JinxedPlayerState.Jinxed);
        }

        public void OnRoundEnd()
        {
            ClearCooldown();
        }

        // ── Timer ─────────────────────────────────────────────────

        public void SetTimer(int seconds)
        {
            if (_timerText == null) return;
            int mins = seconds / 60;
            int secs = seconds % 60;
            _timerText.text = $"{mins}:{secs:00}";
            _timerText.color = seconds <= _warningThreshold ? _timerWarningColor : _timerNormalColor;
        }

        // ── Score Rows ────────────────────────────────────────────

        public void InitScoreRows(Dictionary<int, string> nameMap)
        {
            ClearScoreRows();
            if (_scoreRowParent == null || _scoreRowPrefab == null) return;

            foreach (var kvp in nameMap)
            {
                JinxedScoreRow row = Instantiate(_scoreRowPrefab, _scoreRowParent);
                row.Init(kvp.Value);
                _scoreRows[kvp.Key] = row;
            }
        }

        public void SetPlayerStatus(int playerId, JinxedPlayerState state)
        {
            if (_scoreRows.TryGetValue(playerId, out JinxedScoreRow row))
                row.SetStatus(state);
        }

        public void UpdatePlayerScore(int playerId, int score)
        {
            if (_scoreRows.TryGetValue(playerId, out JinxedScoreRow row))
                row.UpdateScore(score);
        }

        public void ClearScoreRows()
        {
            foreach (var row in _scoreRows.Values)
                if (row != null) Destroy(row.gameObject);
            _scoreRows.Clear();
        }

        // ── Cooldown ──────────────────────────────────────────────

        public void StartCooldown(float duration)
        {
            _cooldownDuration = duration;
            _cooldownRemaining = duration;
            _onCooldown = true;
        }

        public void ClearCooldown()
        {
            _onCooldown = false;
            _cooldownRemaining = 0f;

        }

        // ── Banner ────────────────────────────────────────────────

        public void ShowBanner(string message)
        {
            if (_bannerRoot == null) return;
            if (_bannerCoroutine != null) StopCoroutine(_bannerCoroutine);
            _bannerCoroutine = StartCoroutine(BannerCoroutine(message));
        }

        private IEnumerator BannerCoroutine(string message)
        {
            if (_bannerText != null) _bannerText.text = message;
            _bannerRoot.SetActive(true);
            yield return new WaitForSeconds(_bannerDuration);
            _bannerRoot.SetActive(false);
        }

        // ── Results ───────────────────────────────────────────────

        public void ShowResults(List<(string name, int score, string label)> entries)
        {
            if (_resultsPanel != null) _resultsPanel.SetActive(true);
            if (_resultsText == null) return;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("── RESULTS ──");
            foreach (var e in entries)
                sb.AppendLine($"{e.label}  {e.name}  {e.score} pts");

            _resultsText.text = sb.ToString();
        }

        public void SetResultsCountdown(int seconds)
        {
            if (_resultsCountdownText != null)
                _resultsCountdownText.text = $"Returning in {seconds}...";
        }

        public void SetScorePanelVisible(bool visible)
        {
            if (_scoreRowParent != null)
                _scoreRowParent.gameObject.SetActive(visible);
        }
    }
}