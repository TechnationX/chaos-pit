// JinxedPlayerEffect.cs
// Applies and removes the jinx visual effect on a player's character model.
// Attach to PlayerObject prefab. Called by JinxedController on all clients
// when a player's state changes.

using UnityEngine;

namespace ChaosPit.Minigames.Jinxed
{
    public class JinxedPlayerEffect : MonoBehaviour
    {
        [Header("Jinx Effect")]
        [SerializeField] private SkinnedMeshRenderer _renderer;
        [SerializeField] private Color _jinxedColor = new Color(0.5f, 0.1f, 1f, 1f);
        [SerializeField] private float _jinxedEmission = 0.4f;

        private Material _materialInstance;
        private Color _originalColor;
        private bool _initialized = false;

        // ── Init ──────────────────────────────────────────────────

        private void Awake()
        {
            if (_renderer == null)
                _renderer = GetComponentInChildren<SkinnedMeshRenderer>();

            if (_renderer != null)
            {
                // Create a per-instance material so we don't affect other players
                _materialInstance = _renderer.material;
                _originalColor = _materialInstance.GetColor("_BaseColor");
                _initialized = true;
            }
        }

        // ── Public API ────────────────────────────────────────────

        public void ApplyJinxEffect()
        {
            if (!_initialized) return;
            _materialInstance.SetColor("_BaseColor", _jinxedColor);

            // Enable emission if the material supports it
            if (_materialInstance.IsKeywordEnabled("_EMISSION") ||
                _materialInstance.HasProperty("_EmissionColor"))
            {
                _materialInstance.EnableKeyword("_EMISSION");
                _materialInstance.SetColor("_EmissionColor",
                    _jinxedColor * _jinxedEmission);
            }
        }

        public void RemoveJinxEffect()
        {
            if (!_initialized) return;
            _materialInstance.SetColor("_BaseColor", _originalColor);

            if (_materialInstance.HasProperty("_EmissionColor"))
            {
                _materialInstance.SetColor("_EmissionColor", Color.black);
                _materialInstance.DisableKeyword("_EMISSION");
            }
        }

        public void ApplyEliminatedEffect()
        {
            if (!_initialized) return;
            // Greyed out to visually confirm elimination
            _materialInstance.SetColor("_BaseColor",
                Color.Lerp(_originalColor, Color.grey, 0.7f));

            if (_materialInstance.HasProperty("_EmissionColor"))
            {
                _materialInstance.SetColor("_EmissionColor", Color.black);
                _materialInstance.DisableKeyword("_EMISSION");
            }
        }

        private void OnDestroy()
        {
            // Clean up material instance to avoid memory leak
            if (_materialInstance != null)
                Destroy(_materialInstance);
        }
    }
}