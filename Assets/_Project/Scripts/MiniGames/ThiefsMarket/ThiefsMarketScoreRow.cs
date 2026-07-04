// ThiefsMarketScoreRow.cs

using UnityEngine;
using TMPro;

namespace ChaosPit.Minigames.ThiefsMarket
{
    public class ThiefsMarketScoreRow : MonoBehaviour
    {
        [SerializeField] private TextMeshProUGUI _nameText;
        [SerializeField] private TextMeshProUGUI _countText;
        [SerializeField] private GameObject _winnerIcon;

        public void Init(int playerId, string displayName)
        {
            if (_nameText != null) _nameText.text = displayName;
            UpdateCount(0);
            SetRoundWinner(false);
        }

        public void UpdateCount(int count)
        {
            if (_countText != null) _countText.text = count.ToString();
        }

        public void SetRoundWinner(bool isWinner)
        {
            if (_winnerIcon != null) _winnerIcon.SetActive(isWinner);
        }

        public void SetFinalStanding(int standing, int roundWins)
        {
            if (_countText != null) _countText.text = $"#{standing} ({roundWins} wins)";
        }
    }
}