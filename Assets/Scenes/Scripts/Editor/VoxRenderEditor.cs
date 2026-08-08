#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// [CustomEditor(typeof(VoxDataProvider))]
public class VoxDataProviderEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var provider = (VoxDataProvider)target;
        var assets = provider.Assets;

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("运行时资产 (只读)", EditorStyles.boldLabel);

        EditorGUI.BeginDisabledGroup(true);

        EditorGUILayout.IntField("资产总数", provider.Count);

        for (int i = 0; i < assets.Count; i++)
        {
            var a = assets[i];
            string label = a.isActive
                ? $"Element {i}: {a.filePath}"
                : $"Element {i}: [已卸载]";
            EditorGUILayout.LabelField(label);

            EditorGUI.indentLevel++;
            EditorGUILayout.IntField("Chunk Cubes", a.chunkCubes.Count);
            EditorGUILayout.IntField("Textures", a.textures.Count);
            EditorGUILayout.IntField("Materials", a.materials.Count);
            EditorGUILayout.IntField("Chunk Dims", a.chunkDims.Count);
            EditorGUI.indentLevel--;
        }

        EditorGUI.EndDisabledGroup();
    }
}

// [CustomEditor(typeof(VoxModel))]
public class VoxModelEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var model = (VoxModel)target;

        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("运行时状态 (只读)", EditorStyles.boldLabel);

        EditorGUI.BeginDisabledGroup(true);
        EditorGUILayout.IntField("句柄 (Handle)", model.Handle);
        EditorGUI.EndDisabledGroup();
    }
}
#endif
