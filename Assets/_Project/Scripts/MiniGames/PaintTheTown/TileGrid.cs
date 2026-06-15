// TileGrid.cs
using System.Collections.Generic;
using UnityEngine;

namespace ChaosPit.Minigames.PaintTheTown
{
    /// <summary>
    /// Generates the tile grid procedurally at runtime.
    /// Tracks authoritative tile ownership state (server).
    /// Tracks a dirty set between batches so only changed tiles are synced.
    /// Does NOT network — PaintTheTownController owns all RPCs.
    /// </summary>
    public class TileGrid : MonoBehaviour
    {
        // ── Inspector ────────────────────────────────────────────
        [Header("Grid Config")]
        [SerializeField] private int _gridWidth = 20;
        [SerializeField] private int _gridHeight = 20;
        [SerializeField] private float _tileSize = 1.0f;
        [SerializeField] private float _tileThickness = 0.15f;
        [SerializeField] private GameObject _tilePrefab;

        // ── State ────────────────────────────────────────────────

        // Key: encoded tile index (z * width + x)
        private Dictionary<int, PaintTile> _tiles = new();

        // Server-side ownership: tileIndex → playerId (-1 = unclaimed)
        private Dictionary<int, int> _ownerMap = new();

        // Tiles changed since last batch flush
        private HashSet<int> _dirtyTiles = new();

        public int GridWidth => _gridWidth;
        public int GridHeight => _gridHeight;

        // ── Generation ───────────────────────────────────────────

        public void GenerateGrid()
        {
            if (_tilePrefab == null)
            {
                Debug.LogError("[TileGrid] Tile prefab not assigned.");
                return;
            }

            float offsetX = (_gridWidth * _tileSize) * 0.5f - _tileSize * 0.5f;
            float offsetZ = (_gridHeight * _tileSize) * 0.5f - _tileSize * 0.5f;

            for (int z = 0; z < _gridHeight; z++)
            {
                for (int x = 0; x < _gridWidth; x++)
                {
                    Vector3 pos = transform.position + new Vector3(
                        x * _tileSize - offsetX,
                        0f,
                        z * _tileSize - offsetZ
                    );

                    GameObject go = Instantiate(_tilePrefab, pos, Quaternion.identity, transform);
                    go.name = $"Tile_{x}_{z}";
                    go.transform.localScale = new Vector3(_tileSize, _tileThickness, _tileSize);

                    PaintTile tile = go.GetComponent<PaintTile>();
                    if (tile == null)
                    {
                        Debug.LogError($"[TileGrid] Tile prefab missing PaintTile component on {go.name}");
                        continue;
                    }

                    tile.Init(x, z);
                    _tiles[Encode(x, z)] = tile;
                    _ownerMap[Encode(x, z)] = -1;  // -1 = unclaimed
                }
            }

            Debug.Log($"[TileGrid] Generated {_gridWidth}x{_gridHeight} grid ({_tiles.Count} tiles).");
        }

        // ── Tile Lookup ──────────────────────────────────────────

        /// <summary>Returns the tile index under a world position, or -1 if outside grid.</summary>
        public int GetTileIndexAtWorld(Vector3 worldPos)
        {
            float offsetX = (_gridWidth * _tileSize) * 0.5f - _tileSize * 0.5f;
            float offsetZ = (_gridHeight * _tileSize) * 0.5f - _tileSize * 0.5f;

            int x = Mathf.RoundToInt((worldPos.x - transform.position.x + offsetX) / _tileSize);
            int z = Mathf.RoundToInt((worldPos.z - transform.position.z + offsetZ) / _tileSize);

            if (x < 0 || x >= _gridWidth || z < 0 || z >= _gridHeight)
                return -1;

            return Encode(x, z);
        }

        // ── Server-Side State ─────────────────────────────────────

        /// <summary>
        /// Server only. Assign tile ownership and mark dirty.
        /// Returns true if ownership actually changed.
        /// </summary>
        public bool ClaimTile(int tileIndex, int playerId)
        {
            if (!_ownerMap.ContainsKey(tileIndex)) return false;
            if (_ownerMap[tileIndex] == playerId) return false;

            _ownerMap[tileIndex] = playerId;
            _dirtyTiles.Add(tileIndex);
            return true;
        }

        /// <summary>
        /// Returns dirty tile data and clears the dirty set.
        /// Called by PaintTheTownController on batch interval.
        /// </summary>
        public List<TileDelta> FlushDirtyTiles()
        {
            var deltas = new List<TileDelta>(_dirtyTiles.Count);
            foreach (int idx in _dirtyTiles)
            {
                deltas.Add(new TileDelta { TileIndex = idx, OwnerId = _ownerMap[idx] });
            }
            _dirtyTiles.Clear();
            return deltas;
        }

        /// <summary>Count tiles owned by a specific player.</summary>
        public int CountTilesForPlayer(int playerId)
        {
            int count = 0;
            foreach (var kvp in _ownerMap)
            {
                if (kvp.Value == playerId) count++;
            }
            return count;
        }

        /// <summary>Reset all tile ownership (start of round).</summary>
        public void ResetAllTiles()
        {
            var keys = new List<int>(_ownerMap.Keys);
            foreach (int key in keys)
            {
                _ownerMap[key] = -1;
                _dirtyTiles.Add(key);
            }
        }

        // ── Client-Side Apply ─────────────────────────────────────

        /// <summary>
        /// Client only. Apply a batch of tile deltas received from server.
        /// colorMap: playerId → Color (built from assigned player colors).
        /// </summary>
        public void ApplyDeltas(List<TileDelta> deltas, Dictionary<int, Color> colorMap)
        {
            foreach (TileDelta delta in deltas)
            {
                if (!_tiles.TryGetValue(delta.TileIndex, out PaintTile tile)) continue;

                if (delta.OwnerId < 0)
                {
                    tile.SetNeutral();
                }
                else if (colorMap.TryGetValue(delta.OwnerId, out Color color))
                {
                    tile.SetOwner(delta.OwnerId, color);
                }
            }
        }

        // ── Helpers ───────────────────────────────────────────────

        private int Encode(int x, int z) => z * _gridWidth + x;

        /// Client-side: reset all tiles to neutral visually and in local state, without dirty tracking.</summary>
        public void ResetAllTilesVisual()
        {
            var keys = new List<int>(_ownerMap.Keys);
            foreach (int key in keys)
                _ownerMap[key] = -1;

            foreach (var tile in _tiles.Values)
                tile.SetNeutral();
        }
    }

    // ── Data Structs ──────────────────────────────────────────────

    [System.Serializable]
    public struct TileDelta
    {
        public int TileIndex;
        public int OwnerId;   // -1 = neutral/unclaimed
    }
}
