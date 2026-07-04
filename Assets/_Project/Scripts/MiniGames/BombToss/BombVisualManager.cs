// BombVisualManager.cs
// Client-side only. Manages the bomb GameObject attachment to the current holder's hand socket.

using UnityEngine;
using FindObjectsSortMode = UnityEngine.FindObjectsSortMode;

namespace ChaosPit.Minigames.BombToss
{
    public class BombVisualManager : MonoBehaviour
    {
        [Header("Bomb Visual")]
        [SerializeField] private GameObject _bombPrefab;

        private GameObject _bombInstance;
        private int _currentHolderId = -1;

        private void Awake()
        {
            _bombInstance = Instantiate(_bombPrefab);
            _bombInstance.SetActive(false);
        }

        public void AttachToHolder(int playerId)
        {
            _currentHolderId = playerId;

            PlayerObject holder = FindPlayerById(playerId);
            if (holder == null || holder.HandSocket == null)
            {
                _bombInstance.SetActive(false);
                return;
            }

            _bombInstance.transform.SetParent(holder.HandSocket, false);
            _bombInstance.transform.localPosition = Vector3.zero;
            _bombInstance.transform.localRotation = Quaternion.identity;
            _bombInstance.SetActive(true);
        }

        public void Hide()
        {
            _currentHolderId = -1;
            _bombInstance.SetActive(false);
            _bombInstance.transform.SetParent(null);
        }

        private PlayerObject FindPlayerById(int playerId)
        {
            foreach (var p in FindObjectsByType<PlayerObject>(
                FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (p.PlayerId == playerId) return p;
            return null;
        }
    }
}