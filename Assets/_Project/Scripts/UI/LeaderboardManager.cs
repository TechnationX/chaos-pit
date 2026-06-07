// LeaderboardManager.cs

using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using TMPro;
using UnityEngine.UI;

public class LeaderboardManager : MonoBehaviour
{
    public static LeaderboardManager Instance { get; private set; }

    [Header("Pages")]
    [SerializeField] private LeaderboardPage[] _pages;
    [SerializeField] private float _autoPageInterval = 8f;

    [Header("Navigation")]
    [SerializeField] private Button _prevButton;
    [SerializeField] private Button _nextButton;
    [SerializeField] private TextMeshProUGUI _pageIndicator;

    [Header("Settings")]
    [SerializeField] private int _maxEntries = 10;

    private int _currentPage = 0;
    private Coroutine _autoCycleCoroutine;

    private List<SessionLeaderboardEntry> _cachedCareerEntries = new List<SessionLeaderboardEntry>();
    private List<SessionLeaderboardEntry> _cachedSessionEntries = new List<SessionLeaderboardEntry>();

    public void SetCachedData(List<SessionLeaderboardEntry> careerEntries,
                          List<SessionLeaderboardEntry> sessionEntries)
    {
        _cachedCareerEntries = careerEntries;
        _cachedSessionEntries = sessionEntries;
        ShowPage(_currentPage);
    }

    public List<SessionLeaderboardEntry> GetCachedCareerEntries() => _cachedCareerEntries;
    public List<SessionLeaderboardEntry> GetCachedSessionEntries() => _cachedSessionEntries;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    private void Start()
    {
        _prevButton?.onClick.AddListener(PrevPage);
        _nextButton?.onClick.AddListener(NextPage);

        ShowPage(_currentPage);
        StartAutoCycle();
    }

    private void OnDestroy()
    {
        _prevButton?.onClick.RemoveAllListeners();
        _nextButton?.onClick.RemoveAllListeners();
    }

    // ─── Public ───────────────────────────────────────────────────────────────

    /// Called when players connect or return from a minigame.
    public void Refresh()
    {
        //Debug.Log($"[LeaderboardManager] Refresh called — pages: {_pages?.Length}");
        foreach (var page in _pages)
        {
            //Debug.Log($"[LeaderboardManager] Populating page: {page?.name}");
            page.Populate(_maxEntries);
        }

        ShowPage(_currentPage);
    }

    // ─── Navigation ───────────────────────────────────────────────────────────

    public void NextPage()
    {
        _currentPage = (_currentPage + 1) % _pages.Length;
        ShowPage(_currentPage);
        RestartAutoCycle();
    }

    public void PrevPage()
    {
        _currentPage = (_currentPage - 1 + _pages.Length) % _pages.Length;
        ShowPage(_currentPage);
        RestartAutoCycle();
    }

    private void ShowPage(int index)
    {
        for (int i = 0; i < _pages.Length; i++)
        {
            _pages[i].gameObject.SetActive(i == index);
            Debug.Log($"[LeaderboardManager] Page {i} position: {_pages[i].GetComponent<RectTransform>().anchoredPosition}");
        }

        if (_pageIndicator != null)
            _pageIndicator.text = $"{index + 1} / {_pages.Length}";
    }

    private void StartAutoCycle()
    {
        if (_autoCycleCoroutine != null)
            StopCoroutine(_autoCycleCoroutine);
        _autoCycleCoroutine = StartCoroutine(AutoCycleCoroutine());
    }

    private void RestartAutoCycle()
    {
        StartAutoCycle();
    }

    private IEnumerator AutoCycleCoroutine()
    {
        while (true)
        {
            yield return new WaitForSeconds(_autoPageInterval);
            _currentPage = (_currentPage + 1) % _pages.Length;
            ShowPage(_currentPage);
        }
    }
}