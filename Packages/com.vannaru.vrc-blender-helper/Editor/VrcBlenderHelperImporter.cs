// Automatic companion to VrcBlenderHelperSetup.cs: whenever a model with a
// matching <name>.vrchelper.json sidecar (re)imports, this creates/updates a
// same-named Prefab Variant right next to it, already carrying everything
// Apply()/ApplyWorld() would otherwise need a manual menu click for --
// PhysBones, Contacts, View Position, Stations, Reflection Probes, and
// Teleporter colliders.
//
// Deliberately does NOT add components to the raw imported model asset
// itself (e.g. via OnPostprocessModel): Unity's documented, supported way
// to customize an imported model is to instantiate it as a Prefab instance
// and SaveAsPrefabAsset it -- that automatically produces a Prefab Variant,
// which safely layers overrides on top of the base model and survives the
// base FBX changing on a later re-export. Editing the raw model asset's own
// components directly is not a pattern Unity's reimport pipeline makes any
// guarantees about preserving.
//
// World sidecars' Spawns still can't be applied here -- ApplySpawns needs a
// VRCSceneDescriptor that only exists once the world is actually placed in
// a scene -- so that one step stays manual via the existing "Apply Setup to
// Selected World Root" menu item, run once against the Prefab Variant this
// file already prepared.

using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace VrcBlenderHelper
{
    public class VrcBlenderHelperImporter : AssetPostprocessor
    {
        [Serializable]
        class FormatProbe
        {
            public string format;
        }

        static void OnPostprocessAllAssets(
            string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
        {
            foreach (var fbxPath in importedAssets)
            {
                var sidecarPath = VrcBlenderHelperSetup.SidecarPathForAsset(fbxPath);
                if (sidecarPath == null) continue;
                ProcessOne(fbxPath, sidecarPath);
            }
        }

        static void ProcessOne(string fbxPath, string sidecarPath)
        {
            string format;
            try
            {
                format = JsonUtility.FromJson<FormatProbe>(File.ReadAllText(sidecarPath))?.format;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[VRC Blender Helper] Could not read {sidecarPath}: {e.Message}");
                return;
            }
            if (format != VrcBlenderHelperSetup.SetupFormat && format != VrcBlenderHelperSetup.WorldSetupFormat)
                return; // not one of ours (or an unreadable/unexpected file) -- leave it alone

            var prefabPath = fbxPath.Substring(0, fbxPath.Length - Path.GetExtension(fbxPath).Length) + ".prefab";

            // Instantiate the EXISTING Prefab Variant if re-running (so a
            // VRCAvatarDescriptor, repositioned children, or any other
            // manual addition on top of it survives), otherwise the raw
            // model asset itself the first time -- SaveAsPrefabAsset below
            // turns a Prefab-instance-of-either into a Prefab Variant either
            // way, per Unity's own PrefabUtility reference.
            var existingVariant = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            var sourceAsset = existingVariant != null
                ? existingVariant
                : AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (sourceAsset == null)
            {
                Debug.LogWarning($"[VRC Blender Helper] Could not load '{fbxPath}' as a GameObject to prepare a Prefab from.");
                return;
            }

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(sourceAsset);
            try
            {
                var report = format == VrcBlenderHelperSetup.WorldSetupFormat
                    ? VrcBlenderHelperSetup.ApplyWorld(instance, sidecarPath)
                    : VrcBlenderHelperSetup.Apply(instance, sidecarPath);

                PrefabUtility.SaveAsPrefabAsset(instance, prefabPath, out bool success);
                if (!success)
                {
                    Debug.LogError($"[VRC Blender Helper] Failed to save Prefab at '{prefabPath}'.");
                    return;
                }

                foreach (var line in report.Applied) Debug.Log("[VRC Blender Helper] " + line);
                foreach (var line in report.Warnings) Debug.LogWarning("[VRC Blender Helper] " + line);
                foreach (var line in report.Errors) Debug.LogError("[VRC Blender Helper] " + line);
                Debug.Log(
                    $"[VRC Blender Helper] Prepared '{prefabPath}' from '{fbxPath}' "
                    + $"({report.Applied.Count} applied, {report.Warnings.Count} warning(s), {report.Errors.Count} error(s)). "
                    + "Drag this Prefab into your scene.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }
    }
}
