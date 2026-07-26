// FindBrokenScripts.cs
using UnityEditor;
using UnityEngine;

public static class FindBrokenScripts
{
    [MenuItem("ChaosPit/Find Broken Scripts In Project")]
    public static void Find()
    {
        var guids = AssetDatabase.FindAssets("t:Prefab");
        int brokenCount = 0;

        foreach (var guid in guids)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) continue;

            var components = prefab.GetComponentsInChildren<Component>(true);
            foreach (var comp in components)
            {
                if (comp == null)
                {
                    Debug.LogWarning($"Broken script reference in prefab: {path}", prefab);
                    brokenCount++;
                    break;
                }
            }
        }

        Debug.Log($"Scan complete. {brokenCount} prefabs with broken script references found.");
    }
}