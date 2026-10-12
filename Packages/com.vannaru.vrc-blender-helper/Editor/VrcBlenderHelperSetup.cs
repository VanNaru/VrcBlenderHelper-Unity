// Companion to the "VRC Avatar & World Helper" Blender add-on.
//
// Reads the <name>.vrchelper.json sidecar that the add-on's "Export for
// VRChat" button writes next to the FBX, and applies it to a GameObject in
// the scene. Two sidecar shapes exist (same suffix, distinguished by the
// "format" field):
//   - Avatar ("vrc_blender_helper_setup"): VRCPhysBone components on marked
//     chain roots, VRCContactSender/VRCContactReceiver components on
//     Contact_* objects, the Avatar Descriptor's View Position, and -- for
//     any Contact Receiver linked to a shape key in Blender -- a generated
//     AnimationClip wired to that Receiver's parameter via Motion Time on
//     the FX Animator layer (see ApplyBlendShapeLinks below).
//   - World ("vrc_blender_helper_world_setup"): the scene's VRC Scene
//     Descriptor's Spawns array, a Light Probe Group built from LightProbe_*
//     marker positions, VRCStation components on Station_* objects,
//     UnityEngine.ReflectionProbe components on ReflectionProbe_* objects,
//     and a BoxCollider on every TeleportTarget_*/TeleportTrigger_* object
//     (sized/trigger-configured, but NOT wired to any teleport behavior --
//     see ApplyTeleportTriggers below for what's still a manual Udon step).
//
// Install: add the VPM listing in ALCOM/VCC and install "VRC Blender Helper"
//          (see README), or copy this file into any "Editor" folder under Assets/.
// Use:     VrcBlenderHelperImporter.cs (same folder) already runs most of this
//          automatically into a generated Prefab Variant on import -- see its
//          own header comment. The menu items below are the manual fallback,
//          and still the only way to apply a world's Spawns (needs a
//          VRCSceneDescriptor placed in a scene, which can't exist at import
//          time): select the avatar (or imported world) root in the
//          Hierarchy, then Tools > VRC Blender Helper > Apply Setup to
//          Selected Avatar, or ...Apply Setup to Selected World Root.
//
// Re-running is safe: existing components on the same objects are updated
// in place, not duplicated, and everything is undoable as one step.
//
// SDK types and fields are resolved by name (reflection + SerializedObject)
// rather than referenced directly, so this file compiles in projects without
// the VRChat SDK and degrades to warnings, not compile errors, if a future
// SDK renames a field. Avatar-side field names (PhysBone, Contacts, Avatar
// Descriptor) were verified against VRChat SDK 3.10.4. The world-side field
// names below (VRCSceneDescriptor.spawns, VRCStation's settings) were NOT
// verified against an installed Worlds SDK in the same way -- no SDK was
// available to inspect while writing this. VRCSceneDescriptor.spawns is
// fairly well-attested in community Udon references; VRCStation's field
// names are a best-effort guess. If Apply World Setup logs "SDK field 'x'
// not found" warnings, open the real VRCStation/VRCSceneDescriptor component
// in the Inspector's Debug view to find the correct serialized name and fix
// the SetXxx(...) calls in ApplyStations/ApplySpawns below.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace VrcBlenderHelper
{
    public static class VrcBlenderHelperSetup
    {
        const string AvatarMenuPath = "Tools/VRC Blender Helper/Apply Setup to Selected Avatar";
        const string WorldMenuPath = "Tools/VRC Blender Helper/Apply Setup to Selected World Root";
        const string SidecarSuffix = ".vrchelper.json";
        // internal, not private: VrcBlenderHelperImporter.cs (same assembly)
        // needs these two to route a sidecar to Apply vs ApplyWorld.
        internal const string SetupFormat = "vrc_blender_helper_setup";
        internal const string WorldSetupFormat = "vrc_blender_helper_world_setup";
        const int SupportedFormatVersion = 2;
        const int SupportedWorldFormatVersion = 2;

        // Where ApplyBlendShapeLinks puts generated clips/controllers,
        // relative to the sidecar's own folder -- keeps auto-produced
        // sub-assets visually separate from the model/prefab/sidecar that
        // already live there.
        const string GeneratedSubfolder = "Generated";
        const string FxLayerName = "FX";
        const string BlendShapeLayerName = "VRC Blender Helper";

        const string PhysBoneType = "VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone";
        const string ContactSenderType = "VRC.SDK3.Dynamics.Contact.Components.VRCContactSender";
        const string ContactReceiverType = "VRC.SDK3.Dynamics.Contact.Components.VRCContactReceiver";
        const string AvatarDescriptorType = "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor";
        const string SceneDescriptorType = "VRC.SDK3.Components.VRCSceneDescriptor";
        const string StationType = "VRC.SDK3.Components.VRCStation";

        // -- JSON shape (field names must match core/unity_bridge.py) --

        [Serializable]
        class PhysBoneSettings
        {
            public string integration_type;
            public float pull, spring, stiffness, gravity, gravity_falloff, immobile;
            public string limit_type;
            public float max_angle, radius;
            public string allow_grabbing, allow_posing, allow_collision;
            public float grab_movement;
            public string parameter;
        }

        [Serializable]
        class PhysBoneEntry
        {
            public string root_bone;
            public int transform_count;
            public float endpoint_length;
            public PhysBoneSettings settings;
        }

        [Serializable]
        class ContactEntry
        {
            public string @object;
            public string type;
            public string parent_bone;
            public string shape;
            public float radius, height;
            public string[] collision_tags;
            public bool local_only;
            public string receiver_type;
            public string parameter;
            public bool allow_self, allow_others;
            public string blendshape_mesh;
            public string blendshape_key;
        }

        [Serializable]
        class ViewPositionEntry
        {
            public string method;
            public string eye_l, eye_r, head;
            public float head_up_offset;
        }

        [Serializable]
        class Setup
        {
            public string format;
            public int format_version;
            public string blender_version;
            public string armature;
            public PhysBoneEntry[] physbones;
            public ContactEntry[] contacts;
            public ViewPositionEntry view_position;
        }

        [Serializable]
        class StationEntry
        {
            public string @object;
            public bool seated;
            public string player_mobility;
            public bool disable_exit_collider;
            public bool can_use_from_station;
        }

        [Serializable]
        class ReflectionProbeEntry
        {
            public string @object;
            public string probe_type;
            public float[] box_size;
            public float[] box_offset;
            public bool box_projection;
            public int importance;
            public float intensity;
            public int resolution;
            public bool hdr;
        }

        [Serializable]
        class TeleportTriggerEntry
        {
            public string @object;
            public string target;
            public string trigger_mode;
            public string orientation;
            public float[] box_size;
        }

        [Serializable]
        class WorldSetup
        {
            public string format;
            public int format_version;
            public string blender_version;
            public string[] spawns;
            public string[] light_probes;
            public StationEntry[] stations;
            public ReflectionProbeEntry[] reflection_probes;
            public string[] teleport_targets;
            public TeleportTriggerEntry[] teleport_triggers;
        }

        public class Report
        {
            public readonly List<string> Applied = new List<string>();
            public readonly List<string> Warnings = new List<string>();
            public readonly List<string> Errors = new List<string>();
        }

        // -- Menu entry --

        [MenuItem(AvatarMenuPath, true)]
        static bool ValidateAvatarMenu() => Selection.activeGameObject != null;

        [MenuItem(AvatarMenuPath)]
        static void RunAvatarMenu() => RunMenu(Apply);

        [MenuItem(WorldMenuPath, true)]
        static bool ValidateWorldMenu() => Selection.activeGameObject != null;

        [MenuItem(WorldMenuPath)]
        static void RunWorldMenu() => RunMenu(ApplyWorld);

        static void RunMenu(Func<GameObject, string, Report> applyFn)
        {
            var root = Selection.activeGameObject;
            var jsonPath = FindSidecar(root);
            if (string.IsNullOrEmpty(jsonPath))
            {
                jsonPath = EditorUtility.OpenFilePanel("Select VRC Blender Helper setup", Application.dataPath, "json");
                if (string.IsNullOrEmpty(jsonPath)) return;
            }

            var report = applyFn(root, jsonPath);
            foreach (var line in report.Applied) Debug.Log("[VRC Blender Helper] " + line, root);
            foreach (var line in report.Warnings) Debug.LogWarning("[VRC Blender Helper] " + line, root);
            foreach (var line in report.Errors) Debug.LogError("[VRC Blender Helper] " + line, root);

            EditorUtility.DisplayDialog(
                "VRC Blender Helper",
                $"Applied {report.Applied.Count} item(s) from {Path.GetFileName(jsonPath)}.\n" +
                $"{report.Warnings.Count} warning(s), {report.Errors.Count} error(s) -- see Console for details.",
                "OK");
        }

        /// <summary>The sidecar next to the FBX this avatar instance came from, or null.</summary>
        public static string FindSidecar(GameObject avatarRoot)
        {
            var source = PrefabUtility.GetCorrespondingObjectFromOriginalSource(avatarRoot);
            var assetPath = source != null ? AssetDatabase.GetAssetPath(source) : null;
            return assetPath != null ? SidecarPathForAsset(assetPath) : null;
        }

        /// <summary>
        /// '.../Avatar.fbx' -> '.../Avatar.vrchelper.json' if that file
        /// actually exists, else null -- same rule FindSidecar above uses,
        /// exposed standalone (keyed off a plain asset path instead of a
        /// scene instance) for VrcBlenderHelperImporter.cs's import-time
        /// hook, which has no scene instance to work backward from yet.
        /// </summary>
        public static string SidecarPathForAsset(string fbxAssetPath)
        {
            if (string.IsNullOrEmpty(fbxAssetPath) || !fbxAssetPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                return null;
            var candidate = fbxAssetPath.Substring(0, fbxAssetPath.Length - 4) + SidecarSuffix;
            return File.Exists(candidate) ? candidate : null;
        }

        // -- Apply --

        public static Report Apply(GameObject avatarRoot, string jsonPath)
        {
            var report = new Report();
            Setup setup;
            try
            {
                setup = JsonUtility.FromJson<Setup>(File.ReadAllText(jsonPath));
            }
            catch (Exception e)
            {
                report.Errors.Add($"Could not read {jsonPath}: {e.Message}");
                return report;
            }
            if (setup == null || setup.format != SetupFormat)
            {
                report.Errors.Add($"{Path.GetFileName(jsonPath)} is not a VRC Blender Helper setup file.");
                return report;
            }
            if (setup.format_version > SupportedFormatVersion)
                report.Warnings.Add($"Setup format v{setup.format_version} is newer than this script (v{SupportedFormatVersion}) -- update the Unity script.");

            Undo.SetCurrentGroupName("Apply VRC Blender Helper Setup");
            int undoGroup = Undo.GetCurrentGroup();

            var armatureNode = FindUnique(avatarRoot.transform, setup.armature, null, report, warnIfMissing: false);
            ApplyPhysBones(avatarRoot.transform, armatureNode, setup.physbones, report);
            ApplyContacts(avatarRoot.transform, setup.contacts, report);
            ApplyViewPosition(avatarRoot, armatureNode, setup.view_position, report);
            ApplyBlendShapeLinks(avatarRoot, Path.GetDirectoryName(jsonPath), setup.contacts, report);

            Undo.CollapseUndoOperations(undoGroup);
            return report;
        }

        static void ApplyPhysBones(Transform root, Transform armatureNode, PhysBoneEntry[] entries, Report report)
        {
            if (entries == null || entries.Length == 0) return;
            var type = FindType(PhysBoneType);
            if (type == null)
            {
                report.Errors.Add("VRCPhysBone type not found -- is the VRChat Avatars SDK installed?");
                return;
            }

            foreach (var e in entries)
            {
                var bone = FindUnique(root, e.root_bone, armatureNode, report);
                if (bone == null) continue;
                var s = e.settings;
                var component = GetOrAdd(bone.gameObject, type, out bool added);
                var so = new SerializedObject(component);
                var ctx = $"PhysBone '{e.root_bone}'";

                SetObject(so, "rootTransform", bone, ctx, report);
                SetEnum(so, "integrationType", s.integration_type, ctx, report);
                SetFloat(so, "pull", s.pull, ctx, report);
                SetFloat(so, "spring", s.spring, ctx, report);
                SetFloat(so, "stiffness", s.stiffness, ctx, report);
                SetFloat(so, "gravity", s.gravity, ctx, report);
                SetFloat(so, "gravityFalloff", s.gravity_falloff, ctx, report);
                SetFloat(so, "immobile", s.immobile, ctx, report);
                SetEnum(so, "limitType", s.limit_type, ctx, report);
                SetFloat(so, "maxAngleX", s.max_angle, ctx, report);
                if (Normalize(s.limit_type) == "polar")
                {
                    // Blender authors one Max Angle; Polar has separate pitch (X) / yaw (Z).
                    SetFloat(so, "maxAngleZ", s.max_angle, ctx, report);
                    report.Warnings.Add($"{ctx}: Polar limit -- Max Pitch and Max Yaw both set to {s.max_angle}; tune separately if needed.");
                }
                SetFloat(so, "radius", s.radius * LocalScaleFactor(root, bone), ctx, report);
                // Blender bones have tails, Unity transforms don't: without an
                // endpoint VRChat can't rotate the chain's last bones. Blender's
                // FBX export keeps each bone's +Y axis along the bone.
                SetVector3(so, "endpointPosition",
                    new Vector3(0f, e.endpoint_length * LocalScaleFactor(root, bone), 0f), ctx, report);
                SetEnum(so, "allowCollision", s.allow_collision, ctx, report);
                SetEnum(so, "allowGrabbing", s.allow_grabbing, ctx, report);
                SetEnum(so, "allowPosing", s.allow_posing, ctx, report);
                SetFloat(so, "grabMovement", s.grab_movement, ctx, report);
                SetString(so, "parameter", s.parameter ?? "", ctx, report);
                so.ApplyModifiedProperties();

                report.Applied.Add($"{ctx}: {(added ? "added" : "updated")} ({e.transform_count} transform(s))");
            }
        }

        static void ApplyContacts(Transform root, ContactEntry[] entries, Report report)
        {
            if (entries == null || entries.Length == 0) return;
            var senderType = FindType(ContactSenderType);
            var receiverType = FindType(ContactReceiverType);
            if (senderType == null || receiverType == null)
            {
                report.Errors.Add("VRC Contact types not found -- is the VRChat SDK installed?");
                return;
            }

            foreach (var e in entries)
            {
                var target = FindUnique(root, e.@object, null, report);
                if (target == null) continue;
                bool isReceiver = e.type == "RECEIVER";
                var component = GetOrAdd(target.gameObject, isReceiver ? receiverType : senderType, out bool added);
                var so = new SerializedObject(component);
                var ctx = $"Contact '{e.@object}'";
                float scale = LocalScaleFactor(root, target);

                SetEnum(so, "shapeType", e.shape, ctx, report);
                SetFloat(so, "radius", e.radius * scale, ctx, report);
                SetFloat(so, "height", e.height * scale, ctx, report);
                SetVector3(so, "position", Vector3.zero, ctx, report);
                SetStringArray(so, "collisionTags", e.collision_tags ?? new string[0], ctx, report);
                SetBool(so, "localOnly", e.local_only, ctx, report);
                if (isReceiver)
                {
                    SetEnum(so, "receiverType", e.receiver_type, ctx, report);
                    SetString(so, "parameter", e.parameter ?? "", ctx, report);
                    SetBool(so, "allowSelf", e.allow_self, ctx, report);
                    SetBool(so, "allowOthers", e.allow_others, ctx, report);
                }
                so.ApplyModifiedProperties();

                if (string.IsNullOrEmpty(e.parent_bone))
                    report.Warnings.Add($"{ctx}: not bone-parented in Blender, so it won't follow the rig.");
                report.Applied.Add($"{ctx}: {(added ? "added" : "updated")} {(isReceiver ? "Receiver" : "Sender")}");
            }
        }

        static void ApplyViewPosition(GameObject avatarRoot, Transform armatureNode, ViewPositionEntry vp, Report report)
        {
            if (vp == null || string.IsNullOrEmpty(vp.method)) return;
            var descriptorType = FindType(AvatarDescriptorType);
            var descriptor = descriptorType != null ? avatarRoot.GetComponent(descriptorType) : null;
            if (descriptor == null)
            {
                report.Warnings.Add("No VRC Avatar Descriptor on the selected object -- add one and re-run to set View Position.");
                return;
            }

            var root = avatarRoot.transform;
            Vector3 world;
            if (vp.method == "EYES")
            {
                var l = FindUnique(root, vp.eye_l, armatureNode, report);
                var r = FindUnique(root, vp.eye_r, armatureNode, report);
                if (l == null || r == null) return;
                world = (l.position + r.position) * 0.5f;
            }
            else
            {
                var head = FindUnique(root, vp.head, armatureNode, report);
                if (head == null) return;
                world = head.position + root.up * vp.head_up_offset * MaxComponent(root.lossyScale);
                report.Warnings.Add("View Position estimated from the Head bone (no eye bones found) -- check it in the Scene view.");
            }

            // Offset from the avatar root in the root's orientation, unscaled.
            var viewPosition = Quaternion.Inverse(root.rotation) * (world - root.position);
            var so = new SerializedObject(descriptor);
            SetVector3(so, "ViewPosition", viewPosition, "Avatar Descriptor", report);
            so.ApplyModifiedProperties();
            report.Applied.Add($"View Position set to {viewPosition.ToString("F4")} ({vp.method})");
        }

        /// <summary>
        /// For every Contact Receiver linked to a shape key in Blender
        /// (ContactEntry.blendshape_mesh/blendshape_key both set), generates
        /// a constant-100 AnimationClip for that shape key and wires it into
        /// a <b>Direct Blend Tree</b> on the avatar's FX layer, with that
        /// child's Direct Blend Parameter set to the Contact's own
        /// `parameter` -- VRChat's standard way to let several independent
        /// Contact-driven effects coexist on one shared layer (confirmed
        /// against real avatar-tooling usage, not assumed): each child's
        /// contribution to the final blend shape weight is simply
        /// 100 * thatParameter, with no interaction between children, since
        /// Direct blend weights are independent rather than normalized
        /// against each other the way a normal 1D/2D blend tree's are.
        ///
        /// (`AnimatorState.timeParameterActive`/`.timeParameter` -- "Motion
        /// Time" -- is a real Unity feature but the wrong tool here: it
        /// scrubs ONE state's clip by a parameter, which only works for a
        /// single always-active effect per layer. Multiple independent
        /// Contact-driven effects need Direct Blend Tree's per-child
        /// parameter instead.)
        ///
        /// `AnimatorController`/`AnimationClip`/`BlendTree` are plain built-in
        /// Unity Editor types (no reflection needed); only finding the FX
        /// slot on the VRCAvatarDescriptor needs the SDK-reflection
        /// treatment the rest of this file already uses, resolving the FX
        /// layer by matching baseAnimationLayers[i].type's enum NAME
        /// ("FX") rather than ever referencing the actual AnimLayerType
        /// enum -- so this file keeps compiling without the SDK installed.
        /// </summary>
        static void ApplyBlendShapeLinks(GameObject avatarRoot, string fbxFolderPath, ContactEntry[] contacts, Report report)
        {
            var links = contacts?.Where(e => !string.IsNullOrEmpty(e.blendshape_mesh) && !string.IsNullOrEmpty(e.blendshape_key)).ToArray();
            if (links == null || links.Length == 0) return;

            var descriptorType = FindType(AvatarDescriptorType);
            var descriptor = descriptorType != null ? avatarRoot.GetComponent(descriptorType) : null;
            if (descriptor == null)
            {
                report.Warnings.Add("No VRC Avatar Descriptor on the selected object -- add one and re-run to wire up blend shape links.");
                return;
            }

            var so = new SerializedObject(descriptor);
            var baseLayers = so.FindProperty("baseAnimationLayers");
            if (baseLayers == null)
            {
                report.Warnings.Add("Avatar Descriptor: SDK field 'baseAnimationLayers' not found (SDK changed?) -- skipped blend shape links.");
                return;
            }

            SerializedProperty fxElement = null;
            for (int i = 0; i < baseLayers.arraySize; i++)
            {
                var element = baseLayers.GetArrayElementAtIndex(i);
                var typeProp = element.FindPropertyRelative("type");
                var names = typeProp?.enumNames;
                if (names != null && typeProp.enumValueIndex >= 0 && typeProp.enumValueIndex < names.Length
                    && names[typeProp.enumValueIndex] == FxLayerName)
                {
                    fxElement = element;
                    break;
                }
            }
            if (fxElement == null)
            {
                report.Warnings.Add("Avatar Descriptor: couldn't find the FX playable layer slot -- skipped blend shape links.");
                return;
            }

            var animatorProp = fxElement.FindPropertyRelative("animatorController");
            var controller = animatorProp.objectReferenceValue as AnimatorController;
            if (controller == null)
            {
                var controllerPath = GeneratedAssetPath(fbxFolderPath, avatarRoot.name + "_FX.controller");
                controller = AnimatorController.CreateAnimatorControllerAtPath(controllerPath);
                animatorProp.objectReferenceValue = controller;
                so.ApplyModifiedProperties();
            }

            var layer = controller.layers.FirstOrDefault(l => l.name == BlendShapeLayerName);
            if (layer.stateMachine == null)
            {
                controller.AddLayer(BlendShapeLayerName);
                layer = controller.layers.First(l => l.name == BlendShapeLayerName);
            }

            const string stateName = "Blend Shape Links";
            var state = layer.stateMachine.states.Select(s => s.state).FirstOrDefault(s => s.name == stateName);
            if (state == null) state = layer.stateMachine.AddState(stateName);
            var tree = state.motion as BlendTree;
            if (tree == null)
            {
                tree = new BlendTree { name = stateName, blendType = BlendTreeType.Direct };
                AssetDatabase.AddObjectToAsset(tree, controller);
                state.motion = tree;
            }

            bool changedAnyAsset = false;
            foreach (var e in links)
            {
                var target = FindUnique(avatarRoot.transform, e.blendshape_mesh, null, report);
                if (target == null) continue;
                var smr = target.GetComponent<SkinnedMeshRenderer>();
                if (smr == null || smr.sharedMesh == null || smr.sharedMesh.GetBlendShapeIndex(e.blendshape_key) < 0)
                {
                    report.Warnings.Add(
                        $"Contact '{e.@object}': shape key '{e.blendshape_key}' not found on '{e.blendshape_mesh}' -- "
                        + "skipped (name mismatch between Blender and the imported mesh?).");
                    continue;
                }

                if (!controller.parameters.Any(p => p.name == e.parameter))
                    controller.AddParameter(e.parameter, AnimatorControllerParameterType.Float);

                var clipPath = Path.Combine(fbxFolderPath, GeneratedSubfolder, $"{e.parameter}_{e.blendshape_key}.anim").Replace('\\', '/');
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
                if (clip == null)
                {
                    clip = new AnimationClip { name = Path.GetFileNameWithoutExtension(clipPath) };
                    Directory.CreateDirectory(Path.GetDirectoryName(clipPath));
                    AssetDatabase.CreateAsset(clip, clipPath);
                }
                var relativePath = AnimationUtility.CalculateTransformPath(target, avatarRoot.transform);
                clip.SetCurve(relativePath, typeof(SkinnedMeshRenderer), "blendShape." + e.blendshape_key, new AnimationCurve(new Keyframe(0f, 100f)));
                EditorUtility.SetDirty(clip);

                var children = tree.children;
                int existingIndex = Array.FindIndex(children, c => c.directBlendParameter == e.parameter);
                var childMotion = new ChildMotion { motion = clip, directBlendParameter = e.parameter, timeScale = 1f };
                tree.children = existingIndex >= 0
                    ? children.Select((c, i) => i == existingIndex ? childMotion : c).ToArray()
                    : children.Append(childMotion).ToArray();

                changedAnyAsset = true;
                report.Applied.Add(
                    $"Contact '{e.@object}': shape key '{e.blendshape_key}' on '{e.blendshape_mesh}' wired to FX "
                    + $"parameter '{e.parameter}' via a Direct Blend Tree.");
                if (!e.local_only)
                    report.Warnings.Add(
                        $"Contact '{e.@object}': parameter '{e.parameter}' is not Local Only -- add it to your "
                        + "VRC Expression Parameters by hand so it syncs to other players.");
            }

            if (changedAnyAsset)
            {
                EditorUtility.SetDirty(controller);
                AssetDatabase.SaveAssets();
            }
        }

        /// <summary>folderPath/Generated/fileName, creating the Generated
        /// subfolder if needed.</summary>
        static string GeneratedAssetPath(string folderPath, string fileName)
        {
            var dir = Path.Combine(folderPath, GeneratedSubfolder).Replace('\\', '/');
            Directory.CreateDirectory(dir);
            return $"{dir}/{fileName}";
        }

        // -- Apply (world) --

        /// <summary>
        /// Applies a world sidecar: Spawns onto the scene's VRC Scene
        /// Descriptor, a Light Probe Group from LightProbe_* marker
        /// positions, and VRCStation settings on Station_* objects.
        /// `worldRoot` is the GameObject the imported world FBX lives
        /// under (where Spawn_NN/Station_NN/LightProbe_NN are found by
        /// name) -- it does not need a Scene Descriptor on it itself,
        /// since that component is typically placed separately in the
        /// scene.
        /// </summary>
        public static Report ApplyWorld(GameObject worldRoot, string jsonPath)
        {
            var report = new Report();
            WorldSetup setup;
            try
            {
                setup = JsonUtility.FromJson<WorldSetup>(File.ReadAllText(jsonPath));
            }
            catch (Exception e)
            {
                report.Errors.Add($"Could not read {jsonPath}: {e.Message}");
                return report;
            }
            if (setup == null || setup.format != WorldSetupFormat)
            {
                report.Errors.Add($"{Path.GetFileName(jsonPath)} is not a VRC Blender Helper world setup file.");
                return report;
            }
            if (setup.format_version > SupportedWorldFormatVersion)
                report.Warnings.Add($"World setup format v{setup.format_version} is newer than this script (v{SupportedWorldFormatVersion}) -- update the Unity script.");

            Undo.SetCurrentGroupName("Apply VRC Blender Helper World Setup");
            int undoGroup = Undo.GetCurrentGroup();

            var root = worldRoot.transform;
            ApplySpawns(root, setup.spawns, report);
            ApplyLightProbes(root, setup.light_probes, report);
            ApplyStations(root, setup.stations, report);
            ApplyReflectionProbes(root, setup.reflection_probes, report);
            ApplyTeleportTargets(root, setup.teleport_targets, report);
            ApplyTeleportTriggers(root, setup.teleport_triggers, report);

            Undo.CollapseUndoOperations(undoGroup);
            return report;
        }

        static void ApplySpawns(Transform importRoot, string[] spawnNames, Report report)
        {
            if (spawnNames == null || spawnNames.Length == 0) return;
            var descriptorType = FindType(SceneDescriptorType);
            var descriptor = descriptorType != null
                ? UnityEngine.Object.FindObjectOfType(descriptorType) as Component
                : null;
            if (descriptor == null)
            {
                report.Warnings.Add("No VRC Scene Descriptor found in the scene -- add one and re-run to set Spawns.");
                return;
            }

            var transforms = new List<Transform>();
            foreach (var name in spawnNames)
            {
                var t = FindUnique(importRoot, name, null, report);
                if (t != null) transforms.Add(t);
            }
            if (transforms.Count == 0) return;

            var so = new SerializedObject(descriptor);
            var p = Prop(so, "spawns", "Scene Descriptor", report);
            if (p == null) return;
            p.arraySize = transforms.Count;
            for (int i = 0; i < transforms.Count; i++)
                p.GetArrayElementAtIndex(i).objectReferenceValue = transforms[i];
            so.ApplyModifiedProperties();
            report.Applied.Add($"Scene Descriptor: {transforms.Count} spawn(s) set");
        }

        /// <summary>
        /// LightProbeGroup is a core UnityEngine type (always present,
        /// unlike the VRC SDK types above), so it's referenced directly
        /// rather than through FindType/SerializedObject reflection.
        /// Positions are stored on a dedicated "Light Probes" child of
        /// `importRoot` (created if missing) so re-running updates the
        /// same group instead of creating a new one each time.
        /// </summary>
        static void ApplyLightProbes(Transform importRoot, string[] probeNames, Report report)
        {
            if (probeNames == null || probeNames.Length == 0) return;

            const string holderName = "Light Probes";
            var holderTransform = importRoot.Find(holderName);
            GameObject holderGo;
            if (holderTransform == null)
            {
                holderGo = new GameObject(holderName);
                Undo.RegisterCreatedObjectUndo(holderGo, "Create Light Probes holder");
                Undo.SetTransformParent(holderGo.transform, importRoot, "Parent Light Probes holder");
                holderGo.transform.localPosition = Vector3.zero;
                holderGo.transform.localRotation = Quaternion.identity;
                holderGo.transform.localScale = Vector3.one;
            }
            else
            {
                holderGo = holderTransform.gameObject;
            }

            var positions = new List<Vector3>();
            foreach (var name in probeNames)
            {
                var t = FindUnique(importRoot, name, null, report);
                if (t != null) positions.Add(holderGo.transform.InverseTransformPoint(t.position));
            }
            if (positions.Count == 0) return;

            var group = holderGo.GetComponent<LightProbeGroup>();
            bool added = group == null;
            if (added) group = Undo.AddComponent<LightProbeGroup>(holderGo);
            Undo.RecordObject(group, "Set Light Probe positions");
            group.probePositions = positions.ToArray();

            report.Applied.Add(
                $"Light Probe Group: {(added ? "added" : "updated")} with {positions.Count} probe position(s) on '{holderName}'");
        }

        static void ApplyStations(Transform importRoot, StationEntry[] entries, Report report)
        {
            if (entries == null || entries.Length == 0) return;
            var stationType = FindType(StationType);
            if (stationType == null)
            {
                report.Errors.Add("VRCStation type not found -- is the VRChat Worlds SDK installed?");
                return;
            }

            foreach (var e in entries)
            {
                var target = FindUnique(importRoot, e.@object, null, report);
                if (target == null) continue;
                var component = GetOrAdd(target.gameObject, stationType, out bool added);
                var so = new SerializedObject(component);
                var ctx = $"Station '{e.@object}'";

                SetBool(so, "seated", e.seated, ctx, report);
                SetEnum(so, "playerMobility", e.player_mobility, ctx, report);
                SetBool(so, "disableStationExitCollider", e.disable_exit_collider, ctx, report);
                SetBool(so, "canUseStationFromStation", e.can_use_from_station, ctx, report);
                so.ApplyModifiedProperties();

                report.Applied.Add($"{ctx}: {(added ? "added" : "updated")} VRCStation");
            }
        }

        /// <summary>
        /// UnityEngine.ReflectionProbe is a core engine type (always present,
        /// unlike the VRC SDK types above), so it's referenced directly
        /// rather than through FindType/SerializedObject reflection -- same
        /// treatment ApplyLightProbes gives LightProbeGroup.
        ///
        /// Unlike most components, ReflectionProbe.size/center are
        /// documented as world-space values, NOT scaled by the GameObject's
        /// own Transform -- and box_size/box_offset arrive from Blender
        /// already in world meters, deliberately NOT multiplied by the
        /// marker's own object scale (core/unity_bridge.py's
        /// _reflection_probes_payload ignores it on purpose, since that
        /// scale is only ever a Blender-viewport preview aid -- see
        /// core/reflection_probes.py's size_marker_cube). So these two facts
        /// cancel out perfectly: set size/center directly, with no scale
        /// division at all.
        /// </summary>
        static void ApplyReflectionProbes(Transform importRoot, ReflectionProbeEntry[] entries, Report report)
        {
            if (entries == null || entries.Length == 0) return;

            foreach (var e in entries)
            {
                var target = FindUnique(importRoot, e.@object, null, report);
                if (target == null) continue;

                var probe = target.gameObject.GetComponent<ReflectionProbe>();
                bool added = probe == null;
                if (added) probe = Undo.AddComponent<ReflectionProbe>(target.gameObject);
                Undo.RecordObject(probe, "Set ReflectionProbe settings");

                probe.size = ToVector3(e.box_size);
                probe.center = ToVector3(e.box_offset);
                probe.boxProjection = e.box_projection;
                probe.importance = e.importance;
                probe.intensity = e.intensity;
                probe.resolution = e.resolution;
                probe.hdr = e.hdr;
                probe.mode = ParseReflectionProbeMode(e.probe_type);

                report.Applied.Add($"ReflectionProbe '{e.@object}': {(added ? "added" : "updated")} ({e.probe_type}, {probe.size})");
            }
        }

        /// <summary>
        /// Gives each TeleportTarget_* object a BoxCollider, matching
        /// creators.vrchat.com's own Udon Basics example, which explicitly
        /// requires a Collider on TeleportTo's target GameObject. Default
        /// BoxCollider size (1,1,1 local) -- Blender has no size data for a
        /// bare target marker to carry over, so this is a starting point to
        /// resize by hand if the default footprint doesn't fit.
        /// </summary>
        static void ApplyTeleportTargets(Transform importRoot, string[] targetNames, Report report)
        {
            if (targetNames == null || targetNames.Length == 0) return;

            foreach (var name in targetNames)
            {
                var target = FindUnique(importRoot, name, null, report);
                if (target == null) continue;

                var collider = target.gameObject.GetComponent<BoxCollider>();
                bool added = collider == null;
                if (added)
                {
                    collider = Undo.AddComponent<BoxCollider>(target.gameObject);
                    collider.isTrigger = false;
                }
                report.Applied.Add($"TeleportTarget '{name}': BoxCollider {(added ? "added" : "left as-is")}");
            }
        }

        /// <summary>
        /// Gives each TeleportTrigger_* object a BoxCollider sized/trigger-
        /// configured for its mode, but does NOT add any Udon/UdonSharp
        /// script -- this bridge prepares scene geometry and stock SDK/
        /// engine components, the same division of labor Stations and
        /// Contacts already use; it does not author gameplay logic. The
        /// Applied log line spells out the exact call to wire up by hand.
        ///
        /// BoxCollider.size IS scaled by the object's own Transform
        /// (unlike ReflectionProbe.size above), so box_size -- world
        /// meters, independent of the marker's own preview scale, same
        /// reasoning as ApplyReflectionProbes -- is divided by this
        /// object's lossyScale per axis to cancel that back out and land
        /// on the correct real-world collision volume.
        /// </summary>
        static void ApplyTeleportTriggers(Transform importRoot, TeleportTriggerEntry[] entries, Report report)
        {
            if (entries == null || entries.Length == 0) return;

            foreach (var e in entries)
            {
                var trigger = FindUnique(importRoot, e.@object, null, report);
                if (trigger == null) continue;

                var collider = trigger.gameObject.GetComponent<BoxCollider>();
                bool added = collider == null;
                if (added) collider = Undo.AddComponent<BoxCollider>(trigger.gameObject);
                Undo.RecordObject(collider, "Set TeleportTrigger BoxCollider");

                bool walkIn = Normalize(e.trigger_mode) == "walkin";
                collider.size = DivideSafe(ToVector3(e.box_size), trigger.lossyScale);
                collider.center = Vector3.zero;
                collider.isTrigger = walkIn;

                if (string.IsNullOrEmpty(e.target))
                    report.Warnings.Add($"TeleportTrigger '{e.@object}': no target set in Blender -- nothing to teleport to yet.");
                else if (FindUnique(importRoot, e.target, null, report, warnIfMissing: false) == null)
                    report.Warnings.Add($"TeleportTrigger '{e.@object}': target '{e.target}' not found under {importRoot.name}.");

                string eventName = walkIn ? "OnPlayerTriggerEnter" : "Interact()";
                report.Applied.Add(
                    $"TeleportTrigger '{e.@object}': BoxCollider {(added ? "added" : "updated")} " +
                    $"({(walkIn ? "Walk In, trigger" : "Interact, solid")}) -- add an UdonBehaviour that calls " +
                    $"Networking.LocalPlayer.TeleportTo(target.position, target.rotation, " +
                    $"VRC_SceneDescriptor.SpawnOrientation.{SpawnOrientationName(e.orientation)}) targeting " +
                    $"'{e.target}' from {eventName}. Not wired automatically.");
            }
        }

        // -- Helpers --

        static Type FindType(string fullName) =>
            AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType(fullName, false))
                .FirstOrDefault(t => t != null);

        static Component GetOrAdd(GameObject go, Type type, out bool added)
        {
            var existing = go.GetComponent(type);
            added = existing == null;
            return added ? Undo.AddComponent(go, type) : existing;
        }

        /// <summary>
        /// Find a descendant by exact name. Prefers matches under `preferUnder`
        /// (the armature node, so a mesh named like a bone can't win), and warns
        /// on ambiguity instead of guessing silently.
        /// </summary>
        static Transform FindUnique(Transform root, string name, Transform preferUnder, Report report, bool warnIfMissing = true)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var matches = root.GetComponentsInChildren<Transform>(true).Where(t => t.name == name).ToList();
            if (preferUnder != null)
            {
                var under = matches.Where(t => t.IsChildOf(preferUnder)).ToList();
                if (under.Count > 0) matches = under;
            }
            if (matches.Count == 0)
            {
                if (warnIfMissing) report.Errors.Add($"'{name}' not found under {root.name}.");
                return null;
            }
            if (matches.Count > 1)
                report.Warnings.Add($"{matches.Count} objects named '{name}' -- used the first ({AnimationUtility.CalculateTransformPath(matches[0], root)}).");
            return matches[0];
        }

        /// <summary>
        /// Sidecar lengths are world meters for an unscaled avatar; VRChat radii
        /// are in the target transform's local scale. Converts between the two,
        /// keeping lengths proportional if the avatar root itself is scaled.
        /// </summary>
        static float LocalScaleFactor(Transform root, Transform target)
        {
            float targetScale = MaxComponent(target.lossyScale);
            return targetScale > 1e-6f ? MaxComponent(root.lossyScale) / targetScale : 1f;
        }

        static float MaxComponent(Vector3 v) => Mathf.Max(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        static string Normalize(string s) => (s ?? "").Replace("_", "").Replace(" ", "").ToLowerInvariant();

        static Vector3 ToVector3(float[] arr) =>
            arr != null && arr.Length >= 3 ? new Vector3(arr[0], arr[1], arr[2]) : Vector3.zero;

        /// <summary>Component-wise a/b, falling back to `a` on a near-zero
        /// denominator component (mirrors LocalScaleFactor's epsilon guard)
        /// rather than dividing by zero.</summary>
        static Vector3 DivideSafe(Vector3 a, Vector3 b) => new Vector3(
            Mathf.Abs(b.x) > 1e-6f ? a.x / b.x : a.x,
            Mathf.Abs(b.y) > 1e-6f ? a.y / b.y : a.y,
            Mathf.Abs(b.z) > 1e-6f ? a.z / b.z : a.z);

        static UnityEngine.Rendering.ReflectionProbeMode ParseReflectionProbeMode(string probeType)
        {
            switch (Normalize(probeType))
            {
                case "realtime": return UnityEngine.Rendering.ReflectionProbeMode.Realtime;
                case "custom": return UnityEngine.Rendering.ReflectionProbeMode.Custom;
                default: return UnityEngine.Rendering.ReflectionProbeMode.Baked;
            }
        }

        /// <summary>Blender's DEFAULT/ALIGN_PLAYER/ALIGN_ROOM onto VRC_SceneDescriptor.SpawnOrientation's
        /// real enum member names, for the log message in ApplyTeleportTriggers only.</summary>
        static string SpawnOrientationName(string orientation)
        {
            switch (Normalize(orientation))
            {
                case "alignplayer": return "AlignPlayerWithSpawnPoint";
                case "alignroom": return "AlignRoomWithSpawnPoint";
                default: return "Default";
            }
        }

        static SerializedProperty Prop(SerializedObject so, string name, string ctx, Report report)
        {
            var p = so.FindProperty(name);
            if (p == null) report.Warnings.Add($"{ctx}: SDK field '{name}' not found (SDK changed?) -- skipped.");
            return p;
        }

        static void SetFloat(SerializedObject so, string name, float v, string ctx, Report report)
        {
            var p = Prop(so, name, ctx, report);
            if (p != null) p.floatValue = v;
        }

        static void SetBool(SerializedObject so, string name, bool v, string ctx, Report report)
        {
            var p = Prop(so, name, ctx, report);
            if (p != null) p.boolValue = v;
        }

        static void SetString(SerializedObject so, string name, string v, string ctx, Report report)
        {
            var p = Prop(so, name, ctx, report);
            if (p != null) p.stringValue = v;
        }

        static void SetVector3(SerializedObject so, string name, Vector3 v, string ctx, Report report)
        {
            var p = Prop(so, name, ctx, report);
            if (p != null) p.vector3Value = v;
        }

        static void SetObject(SerializedObject so, string name, UnityEngine.Object v, string ctx, Report report)
        {
            var p = Prop(so, name, ctx, report);
            if (p != null) p.objectReferenceValue = v;
        }

        static void SetStringArray(SerializedObject so, string name, string[] values, string ctx, Report report)
        {
            var p = Prop(so, name, ctx, report);
            if (p == null) return;
            p.arraySize = values.Length;
            for (int i = 0; i < values.Length; i++)
                p.GetArrayElementAtIndex(i).stringValue = values[i];
        }

        /// <summary>Blender enum strings ("ON_ENTER") onto SDK enum names ("OnEnter").</summary>
        static void SetEnum(SerializedObject so, string name, string blenderValue, string ctx, Report report)
        {
            var p = Prop(so, name, ctx, report);
            if (p == null || string.IsNullOrEmpty(blenderValue)) return;
            var wanted = Normalize(blenderValue);
            int index = Array.FindIndex(p.enumNames, n => Normalize(n) == wanted);
            if (index < 0)
                report.Warnings.Add($"{ctx}: '{blenderValue}' is not a valid {name} ({string.Join(", ", p.enumNames)}) -- skipped.");
            else
                p.enumValueIndex = index;
        }
    }
}
