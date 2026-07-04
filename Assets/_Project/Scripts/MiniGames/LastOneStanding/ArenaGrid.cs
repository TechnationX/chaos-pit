// ArenaGrid.cs
// Generates and manages the flat tile grid for Last One Standing.
// Instantiates tiles at runtime so grid shape can vary between rounds.
// No network code — purely structural. Server and clients both build the same
// grid independently using the same seed/dimensions sent via los_grid_init.

using System.Collections.Generic;
using UnityEngine;

namespace ChaosPit.Minigames.LastOneStanding
{
    public class ArenaGrid : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────
        [Header("Grid Dimensions")]
        [SerializeField] private int   _gridWidth  = 20;
        [SerializeField] private int   _gridHeight = 20;
        [SerializeField] private float _tileSize   = 1f;
        [SerializeField] private float _tileGap    = 0.05f;

        [Header("References")]
        [SerializeField] private GameObject _tilePrefab;

        // ── Runtime ───────────────────────────────────────────────
        private ArenaTile[]         _tiles;      // flat array, index = row * width + col
        private List<int>           _activeTileIndices = new();

        public int TileCount  => _tiles?.Length ?? 0;
        public int GridWidth  => _gridWidth;
        public int GridHeight => _gridHeight;

        // ── Grid Generation ───────────────────────────────────────

        /// <summary>
        /// Build the grid. Call this on both server and clients after receiving los_grid_init.
        /// Optional: pass width/height to override Inspector values (for per-round shape changes).
        /// </summary>
        public void BuildGrid(int width = -1, int height = -1)
        {
            if (width  > 0) _gridWidth  = width;
            if (height > 0) _gridHeight = height;

            DestroyGrid();

            _tiles = new ArenaTile[_gridWidth * _gridHeight];
            _activeTileIndices.Clear();

            float stepX = _tileSize + _tileGap;
            float stepZ = _tileSize + _tileGap;

            // Center grid on this transform's position
            float originX = transform.position.x - (_gridWidth  - 1) * stepX * 0.5f;
            float originZ = transform.position.z - (_gridHeight - 1) * stepZ * 0.5f;
            float originY = transform.position.y;

            for (int row = 0; row < _gridHeight; row++)
            {
                for (int col = 0; col < _gridWidth; col++)
                {
                    int index = row * _gridWidth + col;

                    Vector3 pos = new Vector3(
                        originX + col * stepX,
                        originY,
                        originZ + row * stepZ
                    );

                    GameObject go = Instantiate(_tilePrefab, pos, Quaternion.identity, transform);
                    go.name = $"Tile_{index}";
                    go.transform.localScale = new Vector3(_tileSize, go.transform.localScale.y, _tileSize);

                    ArenaTile tile = go.GetComponent<ArenaTile>();
                    if (tile == null) tile = go.AddComponent<ArenaTile>();

                    tile.Init(index);
                    _tiles[index] = tile;
                    _activeTileIndices.Add(index);
                }
            }

            Debug.Log($"[ArenaGrid] Built {_gridWidth}x{_gridHeight} grid ({_tiles.Length} tiles).");
        }

        // ── Tile Access ───────────────────────────────────────────

        public ArenaTile GetTile(int index)
        {
            if (_tiles == null || index < 0 || index >= _tiles.Length) return null;
            return _tiles[index];
        }

        /// <summary>Returns all indices of tiles that have not yet been dropped.</summary>
        public List<int> GetActiveTileIndices()
        {
            _activeTileIndices.RemoveAll(i => _tiles[i] == null || _tiles[i].State == TileState.Dropped);
            return new List<int>(_activeTileIndices);
        }

        /// <summary>
        /// Pick N random active tile indices, excluding any already in warning/danger/dropped state.
        /// Used by server to select tiles for each wave.
        /// </summary>
        public List<int> PickRandomSafeTiles(int count)
        {
            var safe = new List<int>();
            foreach (int i in _activeTileIndices)
            {
                if (_tiles[i] != null && _tiles[i].State == TileState.Safe)
                    safe.Add(i);
            }

            // Fisher-Yates shuffle then take count
            for (int i = safe.Count - 1; i > 0; i--)
            {
                int j   = Random.Range(0, i + 1);
                int tmp = safe[i];
                safe[i] = safe[j];
                safe[j] = tmp;
            }

            return safe.GetRange(0, Mathf.Min(count, safe.Count));
        }

        // ── Tile State Control ────────────────────────────────────

        /// <summary>Trigger the drop sequence on a specific tile (called on clients from RPC data).</summary>
        public void BeginTileDrop(int index, float warningDuration, float dangerDuration)
        {
            ArenaTile tile = GetTile(index);
            if (tile == null) return;
            tile.BeginDropSequence(warningDuration, dangerDuration);
        }

        /// <summary>Reset all tiles to Safe state (between rounds).</summary>
        public void ResetAllTiles()
        {
            if (_tiles == null) return;
            _activeTileIndices.Clear();

            for (int i = 0; i < _tiles.Length; i++)
            {
                if (_tiles[i] != null)
                {
                    _tiles[i].ResetToSafe();
                    _activeTileIndices.Add(i);
                }
            }
        }

        // ── Cleanup ───────────────────────────────────────────────

        public void DestroyGrid()
        {
            if (_tiles != null)
            {
                foreach (ArenaTile tile in _tiles)
                    if (tile != null) Destroy(tile.gameObject);
            }
            _tiles = null;
            _activeTileIndices.Clear();
        }

        private void OnDestroy() => DestroyGrid();

        /// <summary>
        /// Returns tile indices in outer-ring-first spiral order.
        /// Used by Jinxed to determine sequential tile fall sequence.
        /// </summary>
        public List<int> GetFallOrder()
        {
            var order = new List<int>();
            bool[,] visited = new bool[_gridHeight, _gridWidth];

            int top = 0, bottom = _gridHeight - 1;
            int left = 0, right = _gridWidth - 1;

            while (top <= bottom && left <= right)
            {
                // Top row
                for (int col = left; col <= right; col++)
                {
                    int idx = top * _gridWidth + col;
                    if (!visited[top, col]) { order.Add(idx); visited[top, col] = true; }
                }
                // Right column
                for (int row = top + 1; row <= bottom; row++)
                {
                    int idx = row * _gridWidth + right;
                    if (!visited[row, right]) { order.Add(idx); visited[row, right] = true; }
                }
                // Bottom row
                if (bottom > top)
                {
                    for (int col = right - 1; col >= left; col--)
                    {
                        int idx = bottom * _gridWidth + col;
                        if (!visited[bottom, col]) { order.Add(idx); visited[bottom, col] = true; }
                    }
                }
                // Left column
                if (right > left)
                {
                    for (int row = bottom - 1; row >= top + 1; row--)
                    {
                        int idx = row * _gridWidth + left;
                        if (!visited[row, left]) { order.Add(idx); visited[row, left] = true; }
                    }
                }

                top++; bottom--; left++; right--;
            }

            return order;
        }
    }
}
