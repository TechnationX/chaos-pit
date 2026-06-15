// PaintTile.cs
using UnityEngine;

namespace ChaosPit.Minigames.PaintTheTown
{
    /// <summary>
    /// Attached to each tile in the grid.
    /// Owns visual state only — no networking.
    /// TileGrid owns all state; this just renders it.
    /// </summary>
    public class PaintTile : MonoBehaviour
    {
        // ── Inspector ────────────────────────────────────────────
        [Header("References")]
        [SerializeField] private Renderer _renderer;

        [Header("Colors")]
        [SerializeField] private Color _neutralColor = new Color(0.25f, 0.25f, 0.25f);

        // ── State ────────────────────────────────────────────────
        public int GridX    { get; private set; }
        public int GridZ    { get; private set; }
        public int OwnerId  { get; private set; } = -1;  // -1 = unclaimed

        private static readonly int _colorPropId = Shader.PropertyToID("_BaseColor");
        private MaterialPropertyBlock _mpb;

        // ── Init ─────────────────────────────────────────────────
        public void Init(int x, int z)
        {
            GridX = x;
            GridZ = z;
            _mpb = new MaterialPropertyBlock();
            SetNeutral();
        }

        // ── Public API ───────────────────────────────────────────

        /// <summary>Apply ownership color. Called by TileGrid on client after server batch arrives.</summary>
        public void SetOwner(int playerId, Color color)
        {
            OwnerId = playerId;
            ApplyColor(color);
        }

        /// <summary>Reset to neutral (unclaimed).</summary>
        public void SetNeutral()
        {
            OwnerId = -1;
            ApplyColor(_neutralColor);
        }

        // ── Internal ─────────────────────────────────────────────
        private void ApplyColor(Color color)
        {
            if (_renderer == null) return;
            _renderer.GetPropertyBlock(_mpb);
            _mpb.SetColor(_colorPropId, color);
            _renderer.SetPropertyBlock(_mpb);
        }

        private void Reset()
        {
            _renderer = GetComponentInChildren<Renderer>();
        }
    }
}
