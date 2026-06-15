// PaintTheTownNetwork.cs
using System.Collections.Generic;
using UnityEngine;
using FishNet.Object;

namespace ChaosPit.Minigames.PaintTheTown
{
    /// <summary>
    /// Companion NetworkBehaviour for PaintTheTownController.
    ///
    /// Why this exists:
    ///   MiniGameController extends MonoBehaviour (per integration guide — no NetworkObject in scene).
    ///   FishNet RPCs require a NetworkBehaviour on a NetworkObject.
    ///   GameRoomManager IS the scene's NetworkObject authority.
    ///
    ///   Solution: This script lives on a child GameObject of the MiniGameController.
    ///   It carries a NetworkObject + NetworkBehaviour, registered and spawned by GameRoomManager
    ///   before StartGame() is called.
    ///
    /// Responsibilities:
    ///   - Receive RPC calls from PaintTheTownController (server → all clients)
    ///   - Apply results to TileGrid and HUD on each client
    ///
    /// RPC pattern: ObserversRpc — sent from server, received by all connected clients.
    /// </summary>
    public class PaintTheTownNetwork : NetworkBehaviour
    {
        private PaintTheTownController _controller;

        private void Awake()
        {
            _controller = GetComponentInParent<PaintTheTownController>();
            if (_controller == null)
                Debug.LogError("[PaintTheTownNetwork] PaintTheTownController not found in parent.");
        }

        // ── Color Assignment ──────────────────────────────────────

        [ObserversRpc(BufferLast = true)]
        public void RpcSetPlayerColors(int[] playerIds, float[] r, float[] g, float[] b)
        {
            if (_controller == null) return;

            for (int i = 0; i < playerIds.Length; i++)
            {
                //_controller.ColorMap[playerIds[i]] = new Color(r[i], g[i], b[i]);
            }

            Debug.Log($"[PaintTheTownNetwork] Color map received — {playerIds.Length} players.");
        }

        // ── Tile Delta Sync ───────────────────────────────────────

        [ObserversRpc]
        public void RpcApplyTileDeltas(int[] tileIndices, int[] ownerIds)
        {
            if (_controller == null) return;

            var deltas = new List<TileDelta>(tileIndices.Length);
            for (int i = 0; i < tileIndices.Length; i++)
            {
                deltas.Add(new TileDelta { TileIndex = tileIndices[i], OwnerId = ownerIds[i] });
            }

            //_controller.Grid.ApplyDeltas(deltas, _controller.ColorMap);
        }

        // ── Round Lifecycle ───────────────────────────────────────

        [ObserversRpc]
        public void RpcStartRound(float duration)
        {
            //if (_controller?.HUD == null) return;
            //_controller.HUD.OnRoundStart(duration);
        }

        [ObserversRpc]
        public void RpcEndRound()
        {
            //if (_controller?.HUD == null) return;
            //_controller.HUD.OnRoundEnd();
        }

        // ── HUD Tile Count Updates ────────────────────────────────

        [ObserversRpc]
        public void RpcUpdateTileCounts(int[] playerIds, int[] counts)
        {
            //if (_controller?.HUD == null) return;

            var countMap = new Dictionary<int, int>();
            for (int i = 0; i < playerIds.Length; i++)
            {
                countMap[playerIds[i]] = counts[i];
            }

            //_controller.HUD.UpdateTileCounts(countMap);
        }
    }
}
