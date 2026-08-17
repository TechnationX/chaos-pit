using UnityEngine;
using UnityEngine.UI;
namespace Bozo.ModularCharacters
{
    public class OutfitSelector : MonoBehaviour
    {
        public Image icon;
        [Header("Lock State (optional — leave unassigned if not using unlocks)")]
        public GameObject lockOverlay;
        public TMPro.TMP_Text lockLevelText;

        public Outfit outfit;
        private CharacterCreator characterCreator;
        private Button button;
        private OutfitRegistry unlockRegistry;
        private bool _isLocked;

        public void Init(Outfit outfit, CharacterCreator characterCreator, OutfitRegistry unlockRegistry = null)
        {
            button = GetComponentInChildren<Button>();
            button.onClick.AddListener(OnSelect);
            this.outfit = outfit;
            this.characterCreator = characterCreator;
            this.unlockRegistry = unlockRegistry;
            icon.overrideSprite = this.outfit.OutfitIcon;

            RefreshLockState();
        }

        private void RefreshLockState()
        {
            // Remove-button tiles use a sentinel Outfit with no Type — never lock these.
            if (outfit == null || outfit.Type == null || unlockRegistry == null)
            {
                _isLocked = false;
                if (lockOverlay != null) lockOverlay.SetActive(false);
                if (lockLevelText != null) lockLevelText.gameObject.SetActive(false);
                return;
            }

            int requiredLevel = unlockRegistry.GetRequiredLevelForOutfit(outfit);
            int currentLevel = LocalPlayerProfile.Instance != null
                ? CareerLevelSystem.CalculateLevel(LocalPlayerProfile.Instance.CareerScore)
                : 1;

            _isLocked = requiredLevel > currentLevel;

            if (lockOverlay != null) lockOverlay.SetActive(_isLocked);

            if (lockLevelText != null)
            {
                lockLevelText.gameObject.SetActive(_isLocked);
                lockLevelText.text = _isLocked ? $"Lv. {requiredLevel}" : "";
            }

            if (icon != null)
                icon.color = _isLocked ? new Color(1f, 1f, 1f, 0.4f) : Color.white;
        }

        private void OnSelect()
        {
            if (_isLocked) return; // ignore clicks on locked items
            characterCreator.SetOutfit(outfit);
        }

        public void SetVisable(string type)
        {
            if (outfit.Type == null)
            {
                gameObject.SetActive(false);
                Debug.LogWarning(outfit.name + " is missing an outfitType and will not show in the character creator");
                return;
            }
            if (outfit.Type.name == type)
            {
                gameObject.SetActive(true);
            }
            else
            {
                gameObject.SetActive(false);
            }
        }
    }
}