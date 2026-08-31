// ResultsScreenUI.cs
// Shared results-screen behavior, living on ResultsCanvas.prefab's
// ResultsPanel (the same object every MiniGameController already toggles
// via _resultsScreenPanel — no extra Inspector wiring needed anywhere).
// MiniGameController.ShowResults / ShowResultsClientOnly call Populate()
// here to spawn per-player rows and run the countdown, replacing each
// minigame's previous hand-built text block plus its own duplicate timer
// coroutine.

using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

public class ResultsScreenUI : MonoBehaviour
{
    [Header("Rows")]
    [SerializeField] private Transform _rowContainer;
    [SerializeField] private ResultsRow _rowPrefab;

    [Header("Countdown")]
    [SerializeField] private TextMeshProUGUI _countdownText;

    private readonly List<ResultsRow> _spawnedRows = new List<ResultsRow>();
    private Coroutine _countdownCoroutine;

    // Spawns one row per entry, runs the countdown, then hides this panel
    // and invokes onComplete. onComplete is null for the client-only
    // display path (see MiniGameController.ShowResultsClientOnly) — that
    // path still hides the panel, it just has nothing left to notify.
    public void Populate(ResultsData data, float duration, Action onComplete)
    {
        ClearRows();

        if (data != null && _rowPrefab != null && _rowContainer != null)
        {
            foreach (PlayerResultEntry entry in data.Entries)
            {
                ResultsRow row = Instantiate(_rowPrefab, _rowContainer);
                row.Init(entry);
                _spawnedRows.Add(row);
            }
        }

        if (_countdownCoroutine != null) StopCoroutine(_countdownCoroutine);
        _countdownCoroutine = StartCoroutine(CountdownCoroutine(duration, onComplete));
    }

    private void ClearRows()
    {
        foreach (ResultsRow row in _spawnedRows)
            if (row != null) Destroy(row.gameObject);
        _spawnedRows.Clear();
    }

    private IEnumerator CountdownCoroutine(float duration, Action onComplete)
    {
        float remaining = duration;
        while (remaining > 0f)
        {
            if (_countdownText != null)
                _countdownText.text = $"Returning in {Mathf.CeilToInt(remaining)}...";
            yield return new WaitForSeconds(1f);
            remaining -= 1f;
        }

        if (_countdownText != null) _countdownText.text = string.Empty;

        gameObject.SetActive(false);
        onComplete?.Invoke();
    }
}
