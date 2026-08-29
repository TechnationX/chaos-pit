// VendorPrefabBackup.cs
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

    private static void BackupPrefab(string assetPath)
    {
        string projectRoot = Directory.GetParent(Application.dataPath).FullName;
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

    // ---- One-time catch-up for prefabs modified BEFORE this tool existed ----
    // Found by comparing each prefab's file-modified date against its package's
    // original import date, as of 2026-08-29 (documented in game-overview.md).
    // Safe to re-run - it's just an overwrite-if-exists copy. Once you've confirmed
    // VendorPrefabBackups/ has all of these and they're committed, this list and
    // its menu item can be deleted - it has no ongoing purpose once the automatic
    // hook above has been running for a while.
    private static readonly string[] KnownModifiedVendorPrefabs =
    {
        // Chess MEGA-pack - pieces with Rigidbody/MeshCollider/NetworkObject/Grabbable added
        "Assets/Chess MEGA-pack/prefabs/pieces/HighPoly/bishopHighPoly.prefab",
        "Assets/Chess MEGA-pack/prefabs/pieces/HighPoly/bishopHighPoly 1.prefab",
        "Assets/Chess MEGA-pack/prefabs/pieces/HighPoly/KingHighPoly.prefab",
        "Assets/Chess MEGA-pack/prefabs/pieces/HighPoly/KingHighPoly 1.prefab",
        "Assets/Chess MEGA-pack/prefabs/pieces/HighPoly/knightHighPoly.prefab",
        "Assets/Chess MEGA-pack/prefabs/pieces/HighPoly/knightHighPoly 1.prefab",
        "Assets/Chess MEGA-pack/prefabs/pieces/HighPoly/pawnHighPoly.prefab",
        "Assets/Chess MEGA-pack/prefabs/pieces/HighPoly/pawnHighPoly 1.prefab",
        "Assets/Chess MEGA-pack/prefabs/pieces/HighPoly/queenHighPoly.prefab",
        "Assets/Chess MEGA-pack/prefabs/pieces/HighPoly/queenHighPoly 1.prefab",
        "Assets/Chess MEGA-pack/prefabs/pieces/HighPoly/rookHighPoly.prefab",
        "Assets/Chess MEGA-pack/prefabs/pieces/HighPoly/rookHighPoly 1.prefab",
        // Chess board - per-mesh MeshColliders added
        "Assets/Chess MEGA-pack/prefabs/boards/highPolyWood.prefab",

        // FoundryStudios Arcade - bowling ball/pin/dispenser physics + networking
        "Assets/FoundryStudios/Arcade/Prefabs/BowlingBall1.prefab",
        "Assets/FoundryStudios/Arcade/Prefabs/BowlingBall2.prefab",
        "Assets/FoundryStudios/Arcade/Prefabs/BowlingBall3.prefab",
        "Assets/FoundryStudios/Arcade/Prefabs/BowlingBall4.prefab",
        "Assets/FoundryStudios/Arcade/Prefabs/BowlingBallDispenser.prefab",
        "Assets/FoundryStudios/Arcade/Prefabs/BowlingLane_PitDeck_Open.prefab",
        "Assets/FoundryStudios/Arcade/Prefabs/BowlingPin.prefab",
        "Assets/FoundryStudios/Arcade/Prefabs/TV1.prefab",

        // MegaSportPack-By3DGO/Billard_Set - pool table, balls, cues, rack, tray
        "Assets/MegaSportPack-By3DGO/Billard_Set/Prefabs/Billard_Pool_8Ball.prefab",
        "Assets/MegaSportPack-By3DGO/Billard_Set/Prefabs/Billard_Stick_Blue.prefab",
        "Assets/MegaSportPack-By3DGO/Billard_Set/Prefabs/Billard_Stick_Green.prefab",
        "Assets/MegaSportPack-By3DGO/Billard_Set/Prefabs/Billard_Stick_Red.prefab",
        "Assets/MegaSportPack-By3DGO/Billard_Set/Prefabs/BillardBall_Black.prefab",
        "Assets/MegaSportPack-By3DGO/Billard_Set/Prefabs/BillardBall_Blue.prefab",
        "Assets/MegaSportPack-By3DGO/Billard_Set/Prefabs/BillardBall_Brown.prefab",
        "Assets/MegaSportPack-By3DGO/Billard_Set/Prefabs/BillardBall_Green.prefab",
        "Assets/MegaSportPack-By3DGO/Billard_Set/Prefabs/BillardBall_Orange.prefab",
        "Assets/MegaSportPack-By3DGO/Billard_Set/Prefabs/BillardBall_Purple.prefab",
        "Assets/MegaSportPack-By3DGO/Billard_Set/Prefabs/BillardBall_Red.prefab",
        "Assets/MegaSportPack-By3DGO/Billard_Set/Prefabs/BillardBall_SolidBlue.prefab",
        "Assets/MegaSportPack-By3DGO/Billard_Set/Prefabs/BillardBall_SolidBrown.prefab",
        "Assets/MegaSportPack-By3DGO/Billard_Set/Prefabs/BillardBall_SolidGreen.prefab",
        "Assets/MegaSportPack-By3DGO/Billard_Set/Prefabs/BillardBall_SolidOrange.prefab",
        "Assets/MegaSportPack-By3DGO/Billard_Set/Prefabs/BillardBall_SolidPurple.prefab",
        "Assets/MegaSportPack-By3DGO/Billard_Set/Prefabs/BillardBall_SolidRed.prefab",
        "Assets/MegaSportPack-By3DGO/Billard_Set/Prefabs/BillardBall_SolidYellow.prefab",
        "Assets/MegaSportPack-By3DGO/Billard_Set/Prefabs/BillardBall_Tray.prefab",
        "Assets/MegaSportPack-By3DGO/Billard_Set/Prefabs/BillardBall_Triangle.prefab",
        "Assets/MegaSportPack-By3DGO/Billard_Set/Prefabs/BillardBall_White.prefab",
        "Assets/MegaSportPack-By3DGO/Billard_Set/Prefabs/BillardBall_Yellow.prefab",

        // MegaSportPack-By3DGO/Ball_Pack - the swapped-in bowling pin + test balls
        "Assets/MegaSportPack-By3DGO/Ball_Pack/Prefabs/Bowling_Ball 1.prefab",
        "Assets/MegaSportPack-By3DGO/Ball_Pack/Prefabs/Bowling_Ball 2.prefab",
        "Assets/MegaSportPack-By3DGO/Ball_Pack/Prefabs/Bowling_Ball 3.prefab",
        "Assets/MegaSportPack-By3DGO/Ball_Pack/Prefabs/Bowling_Pin.prefab",
    };

    [MenuItem("ChaosPit/Vendor Prefab Backup/One-Time Catch-Up (Known Modified Prefabs)")]
    private static void BackupKnownModified()
    {
        string projectRoot = Directory.GetParent(Application.dataPath).FullName;
        int found = 0, missing = 0;

        foreach (var path in KnownModifiedVendorPrefabs)
        {
            if (!File.Exists(Path.Combine(projectRoot, path)))
            {
                Debug.LogWarning($"[VendorPrefabBackup] Skipped (not found - may have moved/renamed since this list was written): {path}");
                missing++;
                continue;
            }
            BackupPrefab(path);
            found++;
        }

        Debug.Log($"[VendorPrefabBackup] One-time catch-up done: {found} backed up, {missing} not found. Check the Console above for each file, then commit VendorPrefabBackups/ to git.");
    }
}
