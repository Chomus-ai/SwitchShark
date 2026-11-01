using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

public class BulkAddTrees : EditorWindow
{
    private List<GameObject> treePrefabs = new List<GameObject>();
    private Terrain targetTerrain;
    private Vector2 scrollPos;

    [MenuItem("Tools/Bulk Add Trees to Terrain")]
    public static void ShowWindow()
    {
        GetWindow<BulkAddTrees>("Bulk Add Trees");
    }

    private void OnGUI()
    {
        GUILayout.Label("Bulk Add Trees to Terrain", EditorStyles.boldLabel);

        targetTerrain = (Terrain)EditorGUILayout.ObjectField("Target Terrain", targetTerrain, typeof(Terrain), true);

        // Track list size manually to allow expanding/colliding in GUI
        int listSize = EditorGUILayout.IntField("Number of Prefabs", treePrefabs.Count);
        while (listSize > treePrefabs.Count) treePrefabs.Add(null);
        while (listSize < treePrefabs.Count) treePrefabs.RemoveAt(treePrefabs.Count - 1);

        scrollPos = EditorGUILayout.BeginScrollView(scrollPos, GUILayout.Height(200));
        for (int i = 0; i < treePrefabs.Count; i++)
        {
            treePrefabs[i] = (GameObject)EditorGUILayout.ObjectField($"Tree {i + 1}", treePrefabs[i], typeof(GameObject), false);
        }
        EditorGUILayout.EndScrollView();

        if (GUILayout.Button("Add All to Terrain Painter") && targetTerrain != null)
        {
            AddTreesToTerrain();
        }
    }

    private void AddTreesToTerrain()
    {
        TerrainData terrainData = targetTerrain.terrainData;
        List<TreePrototype> prototypes = new List<TreePrototype>(terrainData.treePrototypes);

        foreach (GameObject prefab in treePrefabs)
        {
            if (prefab == null) continue;

            // Prevent duplicating if it's already added
            if (prototypes.Exists(p => p.prefab == prefab)) continue;

            TreePrototype newProto = new TreePrototype();
            newProto.prefab = prefab;
            newProto.bendFactor = 0.5f; // Default bend settings
            prototypes.Add(newProto);
        }

        terrainData.treePrototypes = prototypes.ToArray();
        targetTerrain.Flush();
        Debug.Log($"Successfully updated terrain with {treePrefabs.Count} tree types!");
    }
}
