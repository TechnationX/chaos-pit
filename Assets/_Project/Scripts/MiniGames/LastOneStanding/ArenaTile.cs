// ArenaTile.cs
// Manages per-tile visual state: Safe → Warning → Danger → Dropped
// Color transitions are driven by the client after receiving wave data from the server.
// No network code lives here — purely visual state machine.

using System.Collections;
using UnityEngine;

namespace ChaosPit.Minigames.LastOneStanding
{
    public enum TileState { Safe, Warning, Danger, Dropped }

    public class ArenaTile : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────
        [Header("Colors")]
        [SerializeField] private Color _colorSafe    = new Color(0.3f, 0.6f, 1f);   // blue-white
        [SerializeField] private Color _colorWarning = new Color(1f, 0.85f, 0f);    // yellow
        [SerializeField] private Color _colorDanger  = new Color(1f, 0.15f, 0.1f);  // red
        [SerializeField] private float _colorLerpSpeed = 4f;

        // ── Runtime ───────────────────────────────────────────────
        public TileState State   { get; private set; } = TileState.Safe;
        public int       TileIndex { get; private set; }

        private Renderer   _renderer;
        private Color      _targetColor;
        private Coroutine  _dropCoroutine;

        // ── Init ──────────────────────────────────────────────────

        public void Init(int index)
        {
            TileIndex = index;
            _renderer = GetComponentInChildren<Renderer>();
            SetColorImmediate(_colorSafe);
            State = TileState.Safe;
        }

        // ── Public API ────────────────────────────────────────────

        /// <summary>
        /// Begin the warning → danger → drop sequence with given durations.
        /// Called on all clients when a wave message arrives.
        /// </summary>
        public void BeginDropSequence(float warningDuration, float dangerDuration)
        {
            if (State == TileState.Dropped) return;
            if (_dropCoroutine != null) StopCoroutine(_dropCoroutine);
            _dropCoroutine = StartCoroutine(DropSequenceCoroutine(warningDuration, dangerDuration));
        }

        /// <summary>Force-drop immediately (e.g. round reset or cleanup).</summary>
        public void ForceDropImmediate()
        {
            if (_dropCoroutine != null) StopCoroutine(_dropCoroutine);
            State = TileState.Dropped;
            gameObject.SetActive(false);
        }

        /// <summary>Reset tile to safe state (between rounds).</summary>
        public void ResetToSafe()
        {
            if (_dropCoroutine != null) StopCoroutine(_dropCoroutine);
            State = TileState.Safe;
            gameObject.SetActive(true);
            SetColorImmediate(_colorSafe);
        }

        // ── Drop Sequence ─────────────────────────────────────────

        private IEnumerator DropSequenceCoroutine(float warningDuration, float dangerDuration)
        {
            // Warning phase — lerp to yellow
            State = TileState.Warning;
            yield return LerpColorCoroutine(_colorWarning, warningDuration * 0.3f);
            yield return new WaitForSeconds(warningDuration * 0.7f);

            // Danger phase — lerp to red
            State = TileState.Danger;
            yield return LerpColorCoroutine(_colorDanger, dangerDuration * 0.3f);

            // Flash rapidly in final second before drop
            float flashTime = Mathf.Min(1f, dangerDuration * 0.5f);
            yield return FlashCoroutine(flashTime);

            // Drop
            State = TileState.Dropped;
            gameObject.SetActive(false);
        }

        private IEnumerator LerpColorCoroutine(Color target, float duration)
        {
            if (_renderer == null) yield break;

            Color start   = _renderer.material.color;
            float elapsed = 0f;

            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                _renderer.material.color = Color.Lerp(start, target, elapsed / duration);
                yield return null;
            }

            _renderer.material.color = target;
        }

        private IEnumerator FlashCoroutine(float duration)
        {
            if (_renderer == null) yield break;

            float elapsed    = 0f;
            float flashSpeed = 8f;

            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.PingPong(elapsed * flashSpeed, 1f);
                _renderer.material.color = Color.Lerp(_colorDanger, Color.white, t);
                yield return null;
            }
        }

        private void SetColorImmediate(Color color)
        {
            if (_renderer != null)
                _renderer.material.color = color;
        }
    }
}
