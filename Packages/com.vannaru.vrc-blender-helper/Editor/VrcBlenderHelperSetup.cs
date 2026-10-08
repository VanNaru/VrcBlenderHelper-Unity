// Companion to the "VRC Avatar & World Helper" Blender add-on.
//
// Reads the <name>.vrchelper.json sidecar that the add-on's "Export for
// VRChat" button writes next to the FBX, and applies it to an avatar in the
// scene: VRCPhysBone components on marked chain roots, VRCContactSender /
// VRCContactReceiver components on Contact_* objects, and the Avatar
// Descriptor's View Position.
//
// Install: add the VPM listing in ALCOM/VCC and install "VRC Blender Helper"
//          (see README), or copy this file into any "Editor" folder under Assets/.
// Use:     select the avatar root in the Hierarchy, then
//          Tools > VRC Blender Helper > Apply Setup to Selected Avatar.
//
// Re-running is safe: existing components on the same objects are updated
// in place, not duplicated, and everything is undoable as one step.
//
// SDK types and fields are resolved by name (reflection + SerializedObject)
// rather than referenced directly, so this file compiles in projects without
// the VRChat SDK and degrades to warnings, not compile errors, if a future
// SDK renames a field. Names verified against VRChat SDK 3.10.4.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace VrcBlenderHelper
{
    public static class VrcBlenderHelperSetup
    {
        const string MenuPath = "Tools/VRC Blender Helper/Apply Setup to Selected Avatar";
        const string SidecarSuffix = ".vrchelper.json";
        const string SetupFormat = "vrc_blender_helper_setup";
        const int SupportedFormatVersion = 1;

        const string PhysBoneType = "VRC.SDK3.Dynamics.PhysBone.Components.VRCPhysBone";
        const string ContactSenderType = "VRC.SDK3.Dynamics.Contact.Components.VRCContactSender";
        const string ContactReceiverType = "VRC.SDK3.Dynamics.Contact.Components.VRCContactReceiver";
        const string AvatarDescriptorType = "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor";

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

        public class Report
        {
            public readonly List<string> Applied = new List<string>();
            public readonly List<string> Warnings = new List<string>();
            public readonly List<string> Errors = new List<string>();
        }

        // -- Menu entry --

        [MenuItem(MenuPath, true)]
        static bool ValidateMenu() => Selection.activeGameObject != null;

        [MenuItem(MenuPath)]
        static void RunMenu()
        {
            var root = Selection.activeGameObject;
            var jsonPath = FindSidecar(root);
            if (string.IsNullOrEmpty(jsonPath))
            {
                jsonPath = EditorUtility.OpenFilePanel("Select VRC Blender Helper setup", Application.dataPath, "json");
                if (string.IsNullOrEmpty(jsonPath)) return;
            }

            var report = Apply(root, jsonPath);
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
            if (string.IsNullOrEmpty(assetPath) || !assetPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                return null;
            var candidate = assetPath.Substring(0, assetPath.Length - 4) + SidecarSuffix;
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
