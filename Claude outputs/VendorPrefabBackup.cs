// VendorPrefabBackup.cs
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Keeps a git-tracked backup of any prefab you've modified that lives OUTSIDE
/// Assets/_Project - i.e. a prefab belonging to a purchased/imported asset package
/// (Chess MEGA-pack, MegaSportPack-By3DGO, FoundryStudios, etc).
///
/// WHY THIS EXISTS: .gitignore only tracks Assets/_Project/** - every other Assets
/// subfolder (every third-party package) is intentionally excluded so we don't
/// commit paid asset content to the repo. Fine for prefabs we never touch, but a
/// bunch of vendor prefabs in this project HAVE been hand-modified (chess pieces
/// got Rigidbody/MeshCollider/NetworkObject/Grabbable added, the bowling pin was
/// swapped and rewired, the pool table/balls/cues got colliders and physics
/// materials tuned...) and none of that was ever backed up anywhere. A Library
/// wipe, a bad reimport, or this PC dying would silently lose all of it - this
/// already happened once for real (see game-overview.md's Known Quirks: an
/// unsaved-editor-state Unity restart wiped the bowling pin prefabs' Rigidbody
/// and NetworkObject entirely).
///
/// FIX: mirror any modified vendor prefab into VendorPrefabBackups/ at the
/// PROJECT ROOT - a sibling of Assets/, not inside it. That placement matters:
///   - Unity's asset pipeline never sees it, so there's no risk of a duplicate-GUID
///     collision with the real prefab (which is exactly what would happen if a
///     copy of the .prefab AND its .meta both lived inside Assets/).
///   - No .gitignore changes needed - its rules only scope to Assets/*, so a brand
///     new root-level folder is tracked by git the moment you `git add` it.
/// The mirrored file is a plain copy of the prefab's YAML, kept purely for git
/// history/diffing/manual-restore - Unity never reads it back.
/// </summary>
public class VendorPrefabBackup : AssetModificationProcessor
{
    // Sits next to Assets/, Packages/, ProjectSettings/ - NOT inside Assets/.
    private const string BackupRootFolderName = "VendorPrefabBackups";

    // Anything under these Assets/ paths is ours already (tracked normally via the
    // Assets/_Project allowlist in .gitignore) - never mirrored.
    private static readonly string[] ExcludedPrefixes = { "Assets/_Project" };

    // ---- Automatic: fires on every real, user-driven save ----
    // (Ctrl+S, exiting Prefab Mode after Apply, File > Save Project.) This does NOT
    // fire when a package is freshly imported/reimported - only prefabs you
    // actually edit and save trigger a backup, so reinstalling or updating an
    // asset pack will never flood this folder with untouched prefabs.
    private static string[] OnWillSaveAssets(string[] paths)
    {
        foreach (var path in paths)
        {
            if (!IsVendorPrefab(path)) continue;

            // Copy AFTER Unity actually finishes writing the file to disk -
            // OnWillSaveAssets fires just BEFORE the write happens.
            string captured = path;
            EditorApplication.delayCall += () => BackupPrefab(captured);
        }
        return paths; // never block the save
    }

    private static bool IsVendorPrefab(string assetPath)
    {
        if (!assetPath.EndsWith(".prefab")) return false;
        if (!assetPath.StartsWith("Assets/")) return false;
        return !ExcludedPrefixes.Any(prefix => assetPath.StartsWith(prefix));
    }

    private static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;

    private static void BackupPrefab(string assetPath)
    {
        string projectRoot = ProjectRoot;
        string sourceFile = Path.Combine(projectRoot, assetPath);
        if (!File.Exists(sourceFile))
        {
            Debug.LogWarning($"[VendorPrefabBackup] Expected to back up {assetPath} but the file wasn't on disk.");
            return;
        }

        string relativeUnderAssets = assetPath.Substring("Assets/".Length);
        string destFile = Path.Combine(projectRoot, BackupRootFolderName, relativeUnderAssets);

        Directory.CreateDirectory(Path.GetDirectoryName(destFile));
        File.Copy(sourceFile, destFile, overwrite: true);

        Debug.Log($"[VendorPrefabBackup] Modified vendor prefab backed up -> {BackupRootFolderName}/{relativeUnderAssets}");
    }

    // ---- Manual catch-up: select any prefab(s) in the Project window, then run this ----
    [MenuItem("ChaosPit/Vendor Prefab Backup/Backup Selected Prefab(s)")]
    private static void BackupSelected()
    {
        var paths = Selection.assetGUIDs
            .Select(AssetDatabase.GUIDToAssetPath)
            .Where(IsVendorPrefab)
            .ToArray();

        if (paths.Length == 0)
        {
            Debug.LogWarning("[VendorPrefabBackup] No vendor prefabs selected (prefabs under Assets/_Project are skipped - they're already tracked in git normally).");
            return;
        }

        foreach (var path in paths) BackupPrefab(path);
        Debug.Log($"[VendorPrefabBackup] Backed up {paths.Length} prefab(s).");
    }

    // ---- Refresh: re-syncs backups that already exist, without creating new ones ----
    // Replaces "Backup ALL Vendor Prefabs", which unconditionally backed up every
    // vendor prefab Unity knows about (~1100+ files) the moment it was run -
    // technically fixing backup staleness, but by quietly reintroducing the exact
    // "redistribute full purchased asset content into VendorPrefabBackups/" problem
    // this whole tool exists to avoid. That command bloated the folder with a sweep
    // of everything the project has ever imported and had to be deleted and
    // manually rebuilt from the known-good list.
    //
    // This does the safe version of the same job: it only re-copies prefabs that
    // ALREADY have an entry under VendorPrefabBackups/ (i.e. something the automatic
    // OnWillSaveAssets hook - or a manual Backup Selected - has backed up before),
    // picking up any edits made outside Unity's own save pipeline (direct file
    // edits via the device bridge, for instance, never trigger OnWillSaveAssets).
    // It never creates a backup entry for a prefab that's never had one.
    [MenuItem("ChaosPit/Vendor Prefab Backup/Refresh Existing Backups")]
    private static void RefreshAllBackups()
    {
        string projectRoot = ProjectRoot;
        string backupRoot = Path.Combine(projectRoot, BackupRootFolderName);
        if (!Directory.Exists(backupRoot))
        {
            Debug.LogWarning($"[VendorPrefabBackup] No {BackupRootFolderName} folder found - nothing to refresh.");
            return;
        }

        var existingBackupAssetPaths = Directory.GetFiles(backupRoot, "*.prefab", SearchOption.AllDirectories)
            .Select(f => "Assets/" + f.Substring(backupRoot.Length + 1).Replace('\\', '/'))
            .ToArray();

        int refreshed = 0, missingSource = 0;
        foreach (var assetPath in existingBackupAssetPaths)
        {
            string sourceFile = Path.Combine(projectRoot, assetPath);
            if (!File.Exists(sourceFile))
            {
                Debug.LogWarning($"[VendorPrefabBackup] Backup exists for {assetPath} but the source prefab is gone - left the backup untouched.");
                missingSource++;
                continue;
            }
            BackupPrefab(assetPath);
            refreshed++;
        }

        Debug.Log($"[VendorPrefabBackup] Refresh done: {refreshed} existing backup(s) re-synced, {missingSource} source prefab(s) missing.");
    }

    // ---- Scan-only: flags vendor prefabs that look modified after package import ----
    // Direct answer to "how do I check for a modified file that was never backed up
    // to begin with" - there's no reliable "modified since import" flag Unity
    // exposes, so this is a heuristic: group every vendor prefab by its top-level
    // package folder (e.g. "Chess MEGA-pack", "FoundryStudios"), assume the most
    // common on-disk last-write DATE within a group is that package's bulk-import
    // date, and flag any prefab whose last-write date differs as worth a manual look.
    //
    // This is not proof either way - it can miss a prefab that was edited and saved
    // on the very same day it was imported, and it can flag one that's untouched but
    // got a fresh timestamp for an unrelated reason (a partial package re-import,
    // git checkout, etc). NEVER backs anything up itself - it only logs candidates
    // to the Console, specifically so running it can't repeat the ~1100-file bloat
    // incident. Review the flagged list and use "Backup Selected Prefab(s)" for any
    // that are genuinely modified and not already covered.
    [MenuItem("ChaosPit/Vendor Prefab Backup/Scan For Likely-Modified Prefabs (log only)")]
    private static void ScanForLikelyModified()
    {
        string projectRoot = ProjectRoot;
        string backupRoot = Path.Combine(projectRoot, BackupRootFolderName);

        var vendorPrefabs = AssetDatabase.FindAssets("t:Prefab")
            .Select(AssetDatabase.GUIDToAssetPath)
            .Where(IsVendorPrefab)
            .Distinct()
            .ToArray();

        var existingBackups = Directory.Exists(backupRoot)
            ? new HashSet<string>(Directory.GetFiles(backupRoot, "*.prefab", SearchOption.AllDirectories)
                .Select(f => "Assets/" + f.Substring(backupRoot.Length + 1).Replace('\\', '/')))
            : new HashSet<string>();

        // Group by the first path segment after "Assets/" - that's the package folder
        // (e.g. "Assets/Chess MEGA-pack/..." -> "Chess MEGA-pack").
        var groups = vendorPrefabs.GroupBy(p => p.Split('/')[1]);

        int candidateCount = 0;
        int candidateMissingBackupCount = 0;

        foreach (var group in groups.OrderBy(g => g.Key))
        {
            var withDates = group
                .Select(p => (path: p, writeDate: File.GetLastWriteTimeUtc(Path.Combine(projectRoot, p)).Date))
                .ToArray();

            // The most common date in this group = presumed bulk-import date.
            var modeDate = withDates
                .GroupBy(x => x.writeDate)
                .OrderByDescending(g => g.Count())
                .First().Key;

            var outliers = withDates.Where(x => x.writeDate != modeDate).ToArray();
            if (outliers.Length == 0) continue;

            Debug.Log($"[VendorPrefabBackup] Scan — {group.Key}: {group.Count()} prefab(s), presumed import date {modeDate:yyyy-MM-dd}, {outliers.Length} outlier(s):");
            foreach (var (path, writeDate) in outliers)
            {
                bool hasBackup = existingBackups.Contains(path);
                candidateCount++;
                if (!hasBackup) candidateMissingBackupCount++;
                Debug.Log($"    {(hasBackup ? "[has backup]" : "[NO BACKUP]")} {path} — last written {writeDate:yyyy-MM-dd}");
            }
        }

        Debug.Log($"[VendorPrefabBackup] Scan complete: {candidateCount} candidate(s) look modified after import, {candidateMissingBackupCount} of those have no backup entry yet. Nothing was backed up automatically — review the list above and use \"Backup Selected Prefab(s)\" for any that are genuinely modified.");
    }
}
