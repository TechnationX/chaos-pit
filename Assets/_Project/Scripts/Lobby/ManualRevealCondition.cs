// ManualRevealCondition
using System.Collections.Generic;
using FishNet.Connection;
using FishNet.Observing;
using UnityEngine;

// A FishNet ObserverCondition that starts an object invisible to every
// connection and stays that way until something explicitly calls Reveal()
// for a given connection. Used by LobbySpawner to stop chess pieces from
// being included in the automatic burst FishNet sends a newly-joined client
// (ServerManager.Objects.RebuildObservers) when it catches up on every
// already-spawned NetworkObject at once — that burst is what was found to
// permanently stall a joining client's reliable channel (see the history in
// LobbySpawner.SwitchPoolPatternRoutine's comment for the earlier, related
// case). Instead, LobbySpawner reveals chess pieces to a new connection one
// at a time across multiple frames, the same way SwitchPoolPatternRoutine
// already spreads pool ball spawns.
//
// NetworkObserver.Initialize() instantiates a private copy of whichever
// condition asset is assigned in the Inspector for every NetworkObject that
// uses it (see NetworkObserver.cs), so even though every chess prefab points
// at the SAME source .asset file, each spawned piece gets its own independent
// _revealedTo set at runtime — nothing here is actually shared between pieces.
[CreateAssetMenu(menuName = "Chaos Pit/Manual Reveal Condition", fileName = "ManualRevealCondition")]
public class ManualRevealCondition : ObserverCondition
{
    private HashSet<NetworkConnection> _revealedTo = new HashSet<NetworkConnection>();

    public override bool ConditionMet(NetworkConnection connection, bool currentlyAdded, out bool notProcessed)
    {
        notProcessed = false;
        return _revealedTo.Contains(connection);
    }

    public override ObserverConditionType GetConditionType() => ObserverConditionType.Normal;

    // Grants a connection visibility of this object. Safe to call more than
    // once for the same connection — a repeat call is a no-op. Uses the
    // targeted single-connection RebuildObservers overload so revealing one
    // object to one connection doesn't re-check every other connection this
    // object already has (or doesn't have) visibility for.
    public void Reveal(NetworkConnection connection)
    {
        if (connection == null) return;
        if (!_revealedTo.Add(connection)) return;

        NetworkObject?.ServerManager?.Objects.RebuildObservers(NetworkObject, connection);
    }

    public override void Deinitialize(bool destroyed)
    {
        _revealedTo.Clear();
        base.Deinitialize(destroyed);
    }
}
