using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using UnityEngine.Rendering;

namespace XRStudy.Task1.Editor
{
    /// <summary>
    /// Prepares the virtual humans copied from the team's virtual-humans project for this Quest study:
    /// humanoid import, ≤1024 px textures, lightweight URP Simple Lit materials built from the original
    /// diffuse/normal textures, a seated-idle animator with IK (for gaze), and one prefab per avatar.
    /// Menu: XR Study > Task 1 > Set Up Virtual Humans (also run automatically by Build Study Scene).
    /// </summary>
    public static class VirtualHumanSetup
    {
        public const string Root = "Assets/ThirdParty/VirtualHumans";
        public const string ControllerPath = Root + "/Animations/AgentSeated.controller";
        public const string MalePrefab = "Assets/Prefabs/Task1/Agent_VirtualHuman_Male.prefab";
        public const string FemalePrefab = "Assets/Prefabs/Task1/Agent_VirtualHuman_Female.prefab";
        const string IdlePath = Root + "/Animations/SittingIdle.fbx";

        [MenuItem("XR Study/Task 1/Set Up Virtual Humans")]
        public static void SetupFromMenu() => Debug.Log(Setup());

        public static string Setup()
        {
            if (!AssetDatabase.IsValidFolder(Root)) return "Virtual human assets not found at " + Root;
            AssetDatabase.Refresh();
            var controller = BuildController();
            string m = BuildAvatar("Young_kev", "Virtual Human (Male)", MalePrefab, controller);
            string f = BuildAvatar("Young_cami", "Virtual Human (Female)", FemalePrefab, controller);
            AssetDatabase.SaveAssets();
            return $"Virtual humans ready: {m}; {f}";
        }

        // ------------------------------------------------------------------ animation

        static AnimatorController BuildController()
        {
            var mi = (ModelImporter)AssetImporter.GetAtPath(IdlePath);
            bool dirty = false;
            if (mi.animationType != ModelImporterAnimationType.Human) { mi.animationType = ModelImporterAnimationType.Human; mi.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel; dirty = true; }
            if (mi.materialImportMode != ModelImporterMaterialImportMode.None) { mi.materialImportMode = ModelImporterMaterialImportMode.None; dirty = true; }
            var clips = mi.clipAnimations.Length > 0 ? mi.clipAnimations : mi.defaultClipAnimations;
            foreach (var c in clips)
            {
                // Seated loop in place: no root motion, height and orientation baked into the pose.
                if (!c.loopTime || !c.lockRootRotation || !c.lockRootHeightY || !c.lockRootPositionXZ) dirty = true;
                c.loopTime = true;
                c.lockRootRotation = true; c.keepOriginalOrientation = true;
                c.lockRootHeightY = true; c.keepOriginalPositionY = true;
                c.lockRootPositionXZ = true; c.keepOriginalPositionXZ = true;
            }
            if (dirty) { mi.clipAnimations = clips; mi.SaveAndReimport(); }
            var clip = AssetDatabase.LoadAllAssetsAtPath(IdlePath).OfType<AnimationClip>().First(c => !c.name.StartsWith("__preview__"));

            var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            if (!ctrl)
            {
                ctrl = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
                var state = ctrl.layers[0].stateMachine.AddState("Seated Idle");
                state.motion = clip;
                ctrl.layers[0].stateMachine.defaultState = state;
            }
            var layers = ctrl.layers;
            layers[0].iKPass = true;   // needed for AgentLookAt (head/eye gaze)
            ctrl.layers = layers;
            EditorUtility.SetDirty(ctrl);
            return ctrl;
        }

        // ------------------------------------------------------------------ avatar

        static string BuildAvatar(string name, string displayName, string prefabPath, AnimatorController controller)
        {
            string dir = $"{Root}/{name}";
            string fbx = $"{dir}/{name}.fbx";
            var mi = (ModelImporter)AssetImporter.GetAtPath(fbx);
            if (!mi) return name + " missing";
            bool dirty = false;
            if (mi.animationType != ModelImporterAnimationType.Human) { mi.animationType = ModelImporterAnimationType.Human; mi.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel; dirty = true; }
            if (mi.importAnimation) { mi.importAnimation = false; dirty = true; }
            if (mi.importCameras || mi.importLights) { mi.importCameras = false; mi.importLights = false; dirty = true; }
            // No speech or facial animation in this study: drop facial blend shapes and compress the mesh
            // (keeps the Quest APK and memory small).
            if (mi.importBlendShapes) { mi.importBlendShapes = false; dirty = true; }
            if (mi.meshCompression != ModelImporterMeshCompression.Medium) { mi.meshCompression = ModelImporterMeshCompression.Medium; dirty = true; }
            if (mi.materialImportMode != ModelImporterMaterialImportMode.ImportViaMaterialDescription) { mi.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription; dirty = true; }
            if (dirty) mi.SaveAndReimport();

            // Reallusion rigs + Unity humanoid retargeting roll the eyes back and drop the jaw (their "neutral"
            // muscle pose differs from the CC bind pose). Leaving eyes and jaw unmapped keeps the modelled pose:
            // eyes forward, mouth closed. Head gaze (AgentLookAt) still works through the neck/head.
            var hd = mi.humanDescription;
            var keep = hd.human.Where(h => h.humanName != "LeftEye" && h.humanName != "RightEye" && h.humanName != "Jaw").ToArray();
            if (keep.Length != hd.human.Length)
            {
                hd.human = keep;
                mi.humanDescription = hd;
                mi.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                mi.SaveAndReimport();
            }

            ConfigureTextures($"{dir}/Textures");
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(fbx);
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(model);
            var root = new GameObject(displayName);
            inst.transform.SetParent(root.transform, false);
            // Avatar faces +z, like the agent anchor (toward the participant).
            inst.transform.localRotation = Quaternion.identity;

            string matDir = $"{dir}/Materials";
            if (!AssetDatabase.IsValidFolder(matDir)) AssetDatabase.CreateFolder(dir, "Materials");
            var made = new Dictionary<string, Material>();
            foreach (var r in inst.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    string matName = mats[i] ? mats[i].name : "Unnamed";
                    if (!made.TryGetValue(matName, out var m)) made[matName] = m = MakeMaterial(dir, matName);
                    mats[i] = m;
                }
                r.sharedMaterials = mats;
                // Renderers made only of hidden overlays (eye occlusion, tear line) are switched off entirely.
                r.enabled = mats.Any(x => x && x.shader && x.shader.name != "XRStudy/Task1 Invisible");
                r.shadowCastingMode = ShadowCastingMode.Off;   // no real-time shadows (frame budget)
                r.receiveShadows = false;
                if (r is SkinnedMeshRenderer smr) { smr.updateWhenOffscreen = false; smr.skinnedMotionVectors = false; }
            }

            var animator = inst.GetComponent<Animator>() ?? inst.AddComponent<Animator>();
            animator.runtimeAnimatorController = controller;
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            inst.AddComponent<AgentLookAt>();
            inst.AddComponent<SeatedAvatarAligner>();
            foreach (var c in inst.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(c);

            var prefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            Object.DestroyImmediate(root);
            return $"{name}: {made.Count} materials -> {prefabPath}";
        }

        static void ConfigureTextures(string folder)
        {
            foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { folder }))
            {
                string p = AssetDatabase.GUIDToAssetPath(guid);
                var ti = (TextureImporter)AssetImporter.GetAtPath(p);
                string n = Path.GetFileNameWithoutExtension(p);
                bool normal = n.EndsWith("_Normal");
                bool changed = false;
                // The eyeball is tiny on screen: without mipmaps the dark iris stays crisp instead of blurring to grey.
                bool eye = n.StartsWith("Std_Eye_L") || n.StartsWith("Std_Eye_R");
                if (eye && ti.mipmapEnabled) { ti.mipmapEnabled = false; changed = true; }
                if (normal && ti.textureType != TextureImporterType.NormalMap) { ti.textureType = TextureImporterType.NormalMap; changed = true; }
                if (ti.maxTextureSize != 1024) { ti.maxTextureSize = 1024; changed = true; }
                var android = ti.GetPlatformTextureSettings("Android");
                if (!android.overridden || android.maxTextureSize != 1024)
                {
                    android.overridden = true; android.maxTextureSize = 1024; android.format = TextureImporterFormat.ASTC_6x6;
                    ti.SetPlatformTextureSettings(android); changed = true;
                }
                if (changed) ti.SaveAndReimport();
            }
        }

        /// <summary>
        /// Simple Lit material per original material name. Hair/brows/lashes use alpha clipping (no sorting
        /// issues, cheap on Quest); eye-occlusion/tear-line/cornea overlays and "Invisible" are hidden.
        /// </summary>
        static Material MakeMaterial(string dir, string matName)
        {
            string safe = string.Concat(matName.Split(Path.GetInvalidFileNameChars()));
            string path = $"{dir}/Materials/{safe}.mat";
            var shader = Shader.Find("Universal Render Pipeline/Simple Lit");
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (!m) { m = new Material(shader); AssetDatabase.CreateAsset(m, path); }
            m.shader = shader;
            string key = matName.Replace(" (Instance)", "");
            // CC eyes are an eyeball (iris + sclera texture) under a glossy cornea overlay made for Reallusion's
            // eye shader. With a simple shader the cornea washes the iris out, so it is hidden together with the
            // eye-occlusion / tear-line overlays and "Invisible"; the eyeball itself is drawn matte.
            bool hidden = key.Contains("Occlusion") || key.Contains("Tearline") || key.Contains("TearLine") || key.Contains("Cornea") || key.Contains("Invisib");
            bool eyeball = key.StartsWith("Std_Eye_L") || key.StartsWith("Std_Eye_R");
            bool cutout = key.Contains("Transparency") || key.Contains("Eyelash") || key.Contains("Brow");

            var tex = FindTexture(dir, key, "_Diffuse") ?? FindTexture(dir, key, "_Opacity");
            var nrm = FindTexture(dir, key, "_Normal");
            m.SetTexture("_BaseMap", tex);
            m.SetColor("_BaseColor", tex ? Color.white : GuessColor(key));
            if (nrm) { m.SetTexture("_BumpMap", nrm); m.EnableKeyword("_NORMALMAP"); } else m.DisableKeyword("_NORMALMAP");
            m.SetFloat("_Smoothness", eyeball ? 0.1f : key.Contains("Teeth") ? 0.5f : 0.25f);
            if (eyeball)
            {
                m.SetColor("_BaseColor", new Color(0.82f, 0.8f, 0.78f));   // sclera not glowing white
                m.SetFloat("_SpecularHighlights", 0f); m.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");
            }
            else { m.SetFloat("_SpecularHighlights", 1f); m.DisableKeyword("_SPECULARHIGHLIGHTS_OFF"); }
            m.SetFloat("_Cull", cutout ? (float)CullMode.Off : (float)CullMode.Back);
            if (hidden)
            {
                // Truly invisible (no colour, no depth): a zero-alpha lit material still adds specular light.
                var invisible = Shader.Find("XRStudy/Task1 Invisible");
                if (invisible) { m.shader = invisible; m.renderQueue = (int)RenderQueue.Transparent; }
                EditorUtility.SetDirty(m);
                return m;
            }
            else
            {
                m.SetFloat("_Surface", 0f); m.SetFloat("_ZWrite", 1f); m.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
                m.SetFloat("_SrcBlend", (float)BlendMode.One); m.SetFloat("_DstBlend", (float)BlendMode.Zero);
                m.SetFloat("_AlphaClip", 0f); m.DisableKeyword("_ALPHATEST_ON");
                m.renderQueue = -1;
            }
            if (!hidden && cutout)
            {
                m.SetFloat("_AlphaClip", 1f); m.SetFloat("_Cutoff", 0.35f);
                m.EnableKeyword("_ALPHATEST_ON");
                m.renderQueue = (int)RenderQueue.AlphaTest;
            }
            m.enableInstancing = true;
            EditorUtility.SetDirty(m);
            return m;
        }

        static Texture2D FindTexture(string dir, string matName, string suffix)
        {
            foreach (var ext in new[] { ".jpg", ".png" })
            {
                var t = AssetDatabase.LoadAssetAtPath<Texture2D>($"{dir}/Textures/{matName}{suffix}{ext}");
                if (t) return t;
            }
            return null;
        }

        static Color GuessColor(string key)
        {
            if (key.Contains("Skin") || key.Contains("Nails")) return new Color(0.85f, 0.68f, 0.58f);
            if (key.Contains("Teeth")) return new Color(0.92f, 0.9f, 0.85f);
            if (key.Contains("Tongue")) return new Color(0.75f, 0.4f, 0.4f);
            return new Color(0.5f, 0.5f, 0.5f);
        }
    }
}
