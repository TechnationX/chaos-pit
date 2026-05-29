// MiniGameRegistry.cs

using System.Collections.Generic;
using System.Linq;
using UnityEngine;

[CreateAssetMenu(fileName = "MiniGameRegistry", menuName = "MiniGames/Mini Game Registry")]
public class MiniGameRegistry : ScriptableObject
{
    [Header("Registered Mini Games")]
    public List<MiniGameRegistryEntry> Entries = new List<MiniGameRegistryEntry>();

    // --- Accessors ---

    /// <summary>
    /// Returns all active mini games available for selection.
    /// </summary>
    public List<MiniGameRegistryEntry> GetActiveEntries()
    {
        return Entries.Where(e => e != null && e.IsActive).ToList();
    }

    /// <summary>
    /// Returns a specific entry by its MiniGameId.
    /// </summary>
    public MiniGameRegistryEntry GetById(string miniGameId)
    {
        return Entries.FirstOrDefault(e => e != null && e.MiniGameId == miniGameId);
    }

    /// <summary>
    /// Returns true if a mini game with this id exists and is active.
    /// </summary>
    public bool IsAvailable(string miniGameId)
    {
        var entry = GetById(miniGameId);
        return entry != null && entry.IsActive;
    }
}