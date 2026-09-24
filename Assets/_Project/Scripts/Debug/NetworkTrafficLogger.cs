// NetworkTrafficLogger.cs
using System.Reflection;
using FishNet;
using FishNet.Editing.NetworkProfiler;
using FishNet.Managing;
using FishNet.Managing.Statistic;
using UnityEngine;

// Diagnostic-only, for the current join-hang investigation. Not part of normal gameplay code —
// safe to delete once we've found the actual bug.
//
// Question this answers: after a client connects, does the SERVER actually attempt to send it
// any bytes at all?
//   - Server-outbound stays near zero after the client connects -> the failure is upstream of
//     the transport (something never queues the spawn/data to send in the first place).
//   - Server-outbound is clearly non-zero but the client's own inbound never climbs and it still
//     sees zero spawns -> the failure is in the transport/network layer itself (packets are being
//     sent but not arriving/being processed for that connection).
//
// Why this needs reflection: FishNet's own traffic totals (NetworkTraffic.Bytes) live on an
// `internal` class, and its container (BidirectionalNetworkTraffic.InboundTraffic/OutboundTraffic)
// exposes those internal-only fields. FishNet.Runtime is its own assembly (see
// FishNet.Runtime.asmdef) with no InternalsVisibleTo pointing at our assembly, so normal code
// can't read them. This reflects past that restriction purely to read the byte counters — it
// doesn't modify FishNet or touch any of its internal state.
//
// Setup: drop this on any persistent object that exists before a client connects — the
// NetworkManager object itself is the obvious spot. Requires StatisticsManager's Enable Mode to
// be Development or higher and Update Server checked (already set, per the last screenshot).
public class NetworkTrafficLogger : MonoBehaviour
{
    private NetworkTrafficStatistics _stats;
    private FieldInfo _outboundField;
    private FieldInfo _inboundField;
    private FieldInfo _bytesField;

    private void Start()
    {
        NetworkManager manager = InstanceFinder.NetworkManager;
        if (manager == null || manager.StatisticsManager == null)
        {
            Debug.LogWarning("[NetworkTrafficLogger] No NetworkManager/StatisticsManager found — cannot log traffic.");
            return;
        }

        if (!manager.StatisticsManager.TryGetNetworkTrafficStatistics(out _stats))
        {
            Debug.LogWarning("[NetworkTrafficLogger] TryGetNetworkTrafficStatistics returned false — check StatisticsManager's Enable Mode in the Inspector.");
            return;
        }

        System.Type biTrafficType = typeof(BidirectionalNetworkTraffic);
        _outboundField = biTrafficType.GetField("OutboundTraffic", BindingFlags.NonPublic | BindingFlags.Instance);
        _inboundField = biTrafficType.GetField("InboundTraffic", BindingFlags.NonPublic | BindingFlags.Instance);

        if (_outboundField == null || _inboundField == null)
        {
            Debug.LogWarning("[NetworkTrafficLogger] Reflection couldn't find OutboundTraffic/InboundTraffic on BidirectionalNetworkTraffic — this FishNet version may have renamed/restructured them.");
            return;
        }

        _stats.OnNetworkTraffic += OnNetworkTraffic;
        Debug.Log("[NetworkTrafficLogger] Subscribed to FishNet's OnNetworkTraffic — will log any tick with nonzero traffic.");
    }

    private void OnDestroy()
    {
        if (_stats != null)
            _stats.OnNetworkTraffic -= OnNetworkTraffic;
    }

    private void OnNetworkTraffic(uint tick, BidirectionalNetworkTraffic serverTraffic, BidirectionalNetworkTraffic clientTraffic)
    {
        ulong serverOut = GetBytes(serverTraffic, _outboundField);
        ulong serverIn = GetBytes(serverTraffic, _inboundField);
        ulong clientOut = GetBytes(clientTraffic, _outboundField);
        ulong clientIn = GetBytes(clientTraffic, _inboundField);

        // This event fires every network tick (~30/sec) whether or not anything moved — only
        // log the ticks that actually carried bytes, or this would spam harder than the bug
        // we're chasing.
        if (serverOut == 0 && serverIn == 0 && clientOut == 0 && clientIn == 0)
            return;

        Debug.Log($"[NetworkTrafficLogger] tick {tick} — server out: {serverOut}B in: {serverIn}B | client out: {clientOut}B in: {clientIn}B");
    }

    private ulong GetBytes(BidirectionalNetworkTraffic traffic, FieldInfo directionField)
    {
        if (traffic == null || directionField == null) return 0;

        object networkTrafficObj = directionField.GetValue(traffic);
        if (networkTrafficObj == null) return 0;

        if (_bytesField == null)
            _bytesField = networkTrafficObj.GetType().GetField("Bytes", BindingFlags.Public | BindingFlags.Instance);

        if (_bytesField == null) return 0;

        return (ulong)_bytesField.GetValue(networkTrafficObj);
    }
}
