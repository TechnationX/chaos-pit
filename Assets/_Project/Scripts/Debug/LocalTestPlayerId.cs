// LocalTestPlayerId.cs
using System.IO;
using UnityEngine;

public static class LocalTestPlayerId
{
    private static string _cachedId;
    private static FileStream _lockHandle;

    public static string GetOrClaim()
    {
        if (_cachedId != null) return _cachedId;

        string folder = Bozo.ModularCharacters.BMAC_SaveSystem.filePath;
        if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);

        int suffix = 0;
        while (true)
        {
            string candidate = suffix == 0 ? "LocalPlayer" : $"LocalPlayer{suffix}";
            string lockPath = Path.Combine(folder, candidate + ".lock");

            try
            {
                // FileShare.None = exclusive; throws if another process already holds it
                _lockHandle = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                _cachedId = candidate;
                Debug.Log($"[LocalTestPlayerId] Claimed local save slot: {candidate}");
                return _cachedId;
            }
            catch (IOException)
            {
                // Slot already claimed by another running instance, try the next one
                suffix++;
                if (suffix > 20)
                {
                    Debug.LogError("[LocalTestPlayerId] Could not claim a local save slot after 20 attempts.");
                    _cachedId = "LocalPlayer"; // fallback, avoids crash
                    return _cachedId;
                }
            }
        }
    }
}