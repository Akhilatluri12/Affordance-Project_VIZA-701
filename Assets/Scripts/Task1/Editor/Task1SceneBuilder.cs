using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.Interaction.Toolkit.UI;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace XRStudy.Task1.Editor
{
    /// <summary>
    /// Generates the Task 1 mixed-reality scene (passthrough rig, table-relative study objects, world-space UI),
    /// the matched mug prefab, materials and – if missing – the protocol and layout assets.
    /// Menu: XR Study > Task 1 > Build Study Scene. Protocol/layout assets are never overwritten.
    /// </summary>
    public static class Task1SceneBuilder
    {
        public const string ScenePath = "Assets/Scenes/Task1_MugStudy_MR.unity";
        public const string ConfigPath = "Assets/Settings/Task1/Task1ProtocolConfig.asset";
        public const string LayoutDir = "Assets/Settings/Task1/Layouts";
        public const string MugPrefabPath = "Assets/Prefabs/Task1/StudyMug.prefab";
        const string MaterialDir = "Assets/Materials/Task1";
        const string HandsRig = "Assets/Samples/XR Interaction Toolkit/3.5.1/Hands Interaction Demo/Prefabs/XR Origin Hands (XR Rig).prefab";
        const string InputActionsPath = "Assets/Samples/XR Interaction Toolkit/3.5.1/Starter Assets/XRI Default Input Actions.inputactions";

        static readonly Dictionary<string, Material> s_Mats = new Dictionary<string, Material>();

        [MenuItem("XR Study/Task 1/Build Study Scene")]
        public static void BuildFromMenu()
        {
            if (File.Exists(ScenePath) && !EditorUtility.DisplayDialog("Rebuild Task 1 scene?",
                    ScenePath + " will be regenerated. Protocol and layout assets are kept.", "Rebuild", "Cancel"))
                return;
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            Debug.Log(Build());
        }

        public static string Build()
        {
            s_Mats.Clear();
            // Open the new scene first: switching scenes unloads unreferenced assets, which would invalidate
            // layout/config objects loaded before the switch.
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EnsureFolder(LayoutDir); EnsureFolder("Assets/Prefabs/Task1"); EnsureFolder(MaterialDir);
            var layouts = LoadOrCreateLayouts();
            var cfg = LoadOrCreateConfig(layouts[0], layouts[1]);
            CreateMaterials();
            var mugPrefab = BuildMugPrefab();
            // Virtual humans from the team's virtual-humans project (materials, seated animator, prefabs).
            if (!AssetDatabase.LoadAssetAtPath<GameObject>(VirtualHumanSetup.MalePrefab) || !AssetDatabase.LoadAssetAtPath<GameObject>(VirtualHumanSetup.FemalePrefab))
                VirtualHumanSetup.Setup();
            ConfigureLighting();

            // ------------------------------------------------ Rig: XRI hands + controllers, no locomotion
            var actions = AssetDatabase.LoadAssetAtPath<InputActionAsset>(InputActionsPath);
            var refs = AssetDatabase.LoadAllAssetsAtPath(InputActionsPath).OfType<InputActionReference>()
                .GroupBy(r => r.name).ToDictionary(g => g.Key, g => g.First());
            InputActionReference R(string n) => refs.TryGetValue(n, out var r) ? r : throw new System.Exception("Missing action " + n);

            var managerGO = new GameObject("XR Interaction Manager");
            var interactionManager = managerGO.AddComponent<UnityEngine.XR.Interaction.Toolkit.XRInteractionManager>();

            var rig = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(HandsRig));
            PrefabUtility.UnpackPrefabInstance(rig, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            rig.name = "XR Origin (MR Study Rig)";
            StripRig(rig);
            var origin = rig.GetComponent<XROrigin>();
            origin.RequestedTrackingOriginMode = XROrigin.TrackingOriginMode.Floor;
            origin.CameraYOffset = 0f;
            var cam = origin.Camera;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 0f, 0f);   // passthrough shows through alpha 0
            cam.nearClipPlane = 0.01f;
            var urpCam = cam.GetComponent<UniversalAdditionalCameraData>() ?? cam.gameObject.AddComponent<UniversalAdditionalCameraData>();
            urpCam.renderPostProcessing = false;
            urpCam.renderShadows = false;
            cam.gameObject.AddComponent<ARCameraManager>();     // enables Meta passthrough
            var offset = origin.CameraFloorOffsetObject.transform;
            var planes = rig.AddComponent<ARPlaneManager>();
            planes.enabled = false;                              // optional table detection, enabled on demand

            var session = new GameObject("AR Session");
            session.AddComponent<ARSession>();
            session.AddComponent<ARInputManager>();

            var eventSystem = new GameObject("EventSystem");
            eventSystem.AddComponent<EventSystem>();
            eventSystem.AddComponent<XRUIInputModule>();

            Transform left = offset.Find("Left Controller"), right = offset.Find("Right Controller");

            // ------------------------------------------------ Study objects (children of the table origin)
            var tableGO = new GameObject("Table Origin (calibrated)");
            var table = tableGO.AddComponent<TableCalibration>();
            var layoutCtl = tableGO.AddComponent<LayoutController>();
            tableGO.transform.position = new Vector3(0f, 0.74f, 0.3f);

            var mugs = new StudyMug[3];
            var owners = new[] { Owner.Owned, Owner.Neutral, Owner.AgentOwned };
            for (int i = 0; i < 3; i++)
            {
                var inst = (GameObject)PrefabUtility.InstantiatePrefab(mugPrefab, tableGO.transform);
                var m = inst.GetComponent<StudyMug>();
                m.ApplyIdentity(cfg.Visual(owners[i]));
                EditorUtility.SetDirty(m.Tag);
                mugs[i] = m;
            }

            var rest = BuildHandRest(tableGO.transform);
            var occluderGO = new GameObject("Real Table Occluder (depth only)");
            occluderGO.transform.SetParent(tableGO.transform, false);
            var occluderBox = Prim(PrimitiveType.Cube, "Box", occluderGO.transform, Vector3.zero, Vector3.one, "Occluder");
            var occluder = occluderGO.AddComponent<TableOccluder>();
            var surfaceGO = new GameObject("Table Surface Collider (pick & place)");
            surfaceGO.transform.SetParent(tableGO.transform, false);
            var surface = surfaceGO.AddComponent<BoxCollider>();
            surface.sharedMaterial = MugPhysics();
            occluder.Configure(occluderBox.transform, surface);
            var pad = BuildPad(tableGO.transform);
            var feedbackGO = new GameObject("Feedback Audio");
            feedbackGO.AddComponent<AudioSource>();
            var feedback = feedbackGO.AddComponent<FeedbackPlayer>();
            feedback.Configure(AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/MRTemplateAssets/Audio/ButtonClick.wav"),
                AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/MRTemplateAssets/Audio/Goal.wav"),
                AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/MRTemplateAssets/Audio/ButtonHover.wav"));

            var instruction = BuildPanel("Instruction Panel", tableGO.transform, 600f, cam, feedback, 40, 30);
            var experimenter = BuildPanel("Experimenter Panel (hidden)", tableGO.transform, 520f, cam, feedback, 30, 22);

            var agentRoot = new GameObject("Agent (Layout 2 only)");
            agentRoot.transform.SetParent(tableGO.transform, false);
            BuildChair(agentRoot.transform);
            var body = new GameObject("Agent Body (Placeholder)");
            body.transform.SetParent(agentRoot.transform, false);
            BuildAgent(body.transform, out var torso, out var head);
            GameObject AddHuman(string path)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (!prefab) return null;
                var h = (GameObject)PrefabUtility.InstantiatePrefab(prefab, agentRoot.transform);
                h.transform.localPosition = Vector3.zero;
                h.transform.localRotation = Quaternion.identity;
                foreach (var look in h.GetComponentsInChildren<AgentLookAt>(true)) look.target = null;   // uses the participant camera
                h.SetActive(false);
                return h;
            }
            var male = AddHuman(VirtualHumanSetup.MalePrefab);
            var female = AddHuman(VirtualHumanSetup.FemalePrefab);

            // ------------------------------------------------ Managers
            var studyGO = new GameObject("Task 1 Study");
            var logger = studyGO.AddComponent<DataLogger>();
            var effectors = studyGO.AddComponent<EffectorTracker>();
            effectors.Configure(offset, left, right, R("XRI Left/Is Tracked"), R("XRI Right/Is Tracked"), R("XRI Head/Is Tracked"));
            var contacts = studyGO.AddComponent<ContactDetector>();
            contacts.Configure(effectors, mugs);
            contacts.Apply(cfg);
            var agent = studyGO.AddComponent<AgentPresenceController>();
            agent.Configure(agentRoot, torso, head, cam.transform);
            agent.ConfigureBodies(body, male, female);
            agent.SetAvatar(cfg.agentAvatar);
            var detector = studyGO.AddComponent<ARTableDetector>();
            detector.Configure(planes, cam.transform);
            layoutCtl.Configure(table, mugs, rest, agent, instruction.transform, experimenter.transform, layouts[0]);
            layoutCtl.SetOccluder(occluder, cfg.realTableWidth_m, cfg.realTableDepth_m);
            layoutCtl.SetPad(pad);
            var trials = studyGO.AddComponent<TrialManager>();
            trials.Configure(layoutCtl, effectors, contacts, feedback, instruction, logger);
            trials.ConfigureInteraction(interactionManager);
            var flow = studyGO.AddComponent<StudyFlowController>();
            flow.Configure(cfg, trials, logger, layoutCtl, table, detector, effectors, agent, instruction, cam.transform, left, right);
            flow.ConfigureFeedback(contacts, feedback);
            var controls = studyGO.AddComponent<ExperimenterControls>();
            controls.Configure(experimenter, flow);
            var sim = studyGO.AddComponent<TrialSimulator>();
            sim.Configure(flow, trials, layoutCtl, effectors, instruction, cam.transform);

            WireHandMenuGesture(offset.Find("Left Hand"), controls);

            // Preview: Layout 1 at a nominal table in front of the origin (recalibrated on the headset).
            table.Preview(new TableCalibrationData { position = new Vector3(0f, 0.74f, 0.3f), yaw_deg = 0f });
            layoutCtl.Apply(layouts[0], 0);
            agentRoot.SetActive(false);
            experimenter.Hide();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AddSceneToBuild();
            AssetDatabase.SaveAssets();
            return $"Task 1 MR scene built: {ScenePath}. Protocol: {ConfigPath}. Layouts: {LayoutDir}.";
        }

        // =================================================================== Rig

        static void StripRig(GameObject rig)
        {
            // Seated study: no locomotion, turning, teleport, climbing or gaze selection.
            foreach (var name in new[] { "Locomotion", "Gaze Interactor", "Gaze Stabilized", "Teleport Interactor",
                         "Left Controller Teleport Stabilized Origin", "Right Controller Teleport Stabilized Origin" })
                foreach (var t in rig.GetComponentsInChildren<Transform>(true).Where(t => t && t.name == name).ToList())
                    Object.DestroyImmediate(t.gameObject);
            foreach (var c in rig.GetComponents<Component>())
            {
                string n = c.GetType().Name;
                if (n == "XRGazeAssistance" || n == "CharacterController") Object.DestroyImmediate(c);
            }
            // The starter-assets manager toggles teleport/move/turn input; nothing left for it to manage.
            foreach (var mb in rig.GetComponentsInChildren<MonoBehaviour>(true))
                if (mb && mb.GetType().Name == "ControllerInputActionManager") mb.enabled = false;
        }

        static void WireHandMenuGesture(Transform leftHand, ExperimenterControls controls)
        {
            if (!leftHand) return;
            foreach (var mb in leftHand.GetComponents<MonoBehaviour>())
            {
                if (mb.GetType().Name != "MetaSystemGestureDetector") continue;
                var prop = mb.GetType().GetProperty("menuPressed");
                if (prop?.GetValue(mb) is UnityEvent evt)
                {
                    UnityEventTools.AddVoidPersistentListener(evt, controls.Toggle);
                    EditorUtility.SetDirty(mb);
                }
            }
        }

        // =================================================================== Mug

        static GameObject BuildMugPrefab()
        {
            var root = new GameObject("StudyMug");
            var body = Prim(PrimitiveType.Cylinder, "Body", root.transform, new Vector3(0, 0.05f, 0), new Vector3(0.08f, 0.05f, 0.08f), "MugBody");
            // Handle along local +x (participant's right at yaw 0).
            var h1 = Prim(PrimitiveType.Cube, "Handle Top", root.transform, new Vector3(0.052f, 0.072f, 0), new Vector3(0.03f, 0.01f, 0.012f), "MugBody");
            var h2 = Prim(PrimitiveType.Cube, "Handle Bottom", root.transform, new Vector3(0.052f, 0.028f, 0), new Vector3(0.03f, 0.01f, 0.012f), "MugBody");
            var h3 = Prim(PrimitiveType.Cube, "Handle Grip", root.transform, new Vector3(0.063f, 0.05f, 0), new Vector3(0.01f, 0.054f, 0.012f), "MugBody");
            var ring = Prim(PrimitiveType.Cylinder, "Cue Ring", root.transform, new Vector3(0, 0.002f, 0), new Vector3(0.14f, 0.0015f, 0.14f), "CueRing");
            var halo = Prim(PrimitiveType.Cylinder, "Cue Halo", root.transform, new Vector3(0, 0.06f, 0), new Vector3(0.115f, 0.062f, 0.115f), "CueHalo");
            ring.GetComponent<Renderer>().enabled = false;
            halo.GetComponent<Renderer>().enabled = false;

            // Identity sticker on the participant-facing side (local −z): shape above, label below.
            var sticker = new GameObject("Identity Sticker").transform;
            sticker.SetParent(root.transform, false);
            sticker.localPosition = new Vector3(0, 0.05f, -0.0405f);
            Prim(PrimitiveType.Quad, "Sticker", sticker, Vector3.zero, new Vector3(0.05f, 0.07f, 1f), "Sticker");
            var diamond = Prim(PrimitiveType.Quad, "Shape Diamond", sticker, new Vector3(0, 0.014f, -0.0006f), new Vector3(0.022f, 0.022f, 1f), "Shape");
            diamond.transform.localRotation = Quaternion.Euler(0, 0, 45f);
            var square = Prim(PrimitiveType.Quad, "Shape Square", sticker, new Vector3(0, 0.014f, -0.0006f), new Vector3(0.026f, 0.026f, 1f), "Shape");
            var triangle = Prim(PrimitiveType.Quad, "Shape Triangle", sticker, new Vector3(0, 0.014f, -0.0006f), new Vector3(0.03f, 0.028f, 1f), "Shape");
            triangle.GetComponent<MeshFilter>().sharedMesh = TriangleMesh();
            var labelGO = new GameObject("Label");
            labelGO.transform.SetParent(sticker, false);
            labelGO.transform.localPosition = new Vector3(0, -0.018f, -0.0008f);
            var label = labelGO.AddComponent<TextMeshPro>();
            label.text = "P1";
            label.fontSize = 0.2f;
            label.fontStyle = FontStyles.Bold;
            label.alignment = TextAlignmentOptions.Center;
            label.color = new Color(0.1f, 0.1f, 0.12f);
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.rectTransform.sizeDelta = new Vector2(0.05f, 0.025f);

            // Pick & place physics: one standardized collider/physics/attach configuration for every mug (FR-04).
            // Kinematic and not grabbable until a pick & place session switches them on.
            var phys = MugPhysics();
            var bodyCol = body.AddComponent<MeshCollider>();
            bodyCol.convex = true; bodyCol.sharedMesh = body.GetComponent<MeshFilter>().sharedMesh; bodyCol.sharedMaterial = phys;
            foreach (var h in new[] { h1, h2, h3 }) h.AddComponent<BoxCollider>().sharedMaterial = phys;
            var rb = root.AddComponent<Rigidbody>();
            rb.mass = 0.3f;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            rb.isKinematic = true;
            rb.useGravity = false;
            var grab = root.AddComponent<XRGrabInteractable>();
            grab.movementType = XRBaseInteractable.MovementType.VelocityTracking;
            grab.throwOnDetach = false;          // natural release, no flinging
            grab.useDynamicAttach = true;        // keep the grasp pose where the hand grabbed
            grab.smoothPosition = false;
            grab.smoothRotation = false;
            grab.trackScale = false;
            grab.enabled = false;
            var filter = root.AddComponent<DesignatedHandSelectFilter>();
            grab.startingSelectFilters.Add(filter);
            grab.startingHoverFilters.Add(filter);
            root.AddComponent<OwnershipTag>();
            var mug = root.AddComponent<StudyMug>();
            mug.ConfigureVisuals(new[] { body.GetComponent<Renderer>(), h1.GetComponent<Renderer>(), h2.GetComponent<Renderer>(), h3.GetComponent<Renderer>() },
                new[] { ring.GetComponent<Renderer>(), halo.GetComponent<Renderer>() }, label,
                diamond.GetComponent<Renderer>(), triangle.GetComponent<Renderer>(), square.GetComponent<Renderer>());
            root.AddComponent<GrabEventBridge>();
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, MugPrefabPath);
            Object.DestroyImmediate(root);
            return prefab;
        }

        static Mesh TriangleMesh()
        {
            const string path = MaterialDir + "/Task1_Triangle.asset";
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (mesh) return mesh;
            mesh = new Mesh { name = "Task1_Triangle" };
            mesh.vertices = new[] { new Vector3(-0.5f, -0.45f, 0), new Vector3(0f, 0.5f, 0), new Vector3(0.5f, -0.45f, 0) };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.normals = new[] { Vector3.back, Vector3.back, Vector3.back };
            mesh.uv = new[] { Vector2.zero, new Vector2(0.5f, 1f), Vector2.right };
            mesh.RecalculateBounds();
            AssetDatabase.CreateAsset(mesh, path);
            return mesh;
        }

        static PhysicsMaterial MugPhysics()
        {
            string path = MaterialDir + "/Task1_MugPhysics.physicMaterial";
            var m = AssetDatabase.LoadAssetAtPath<PhysicsMaterial>(path);
            if (m) return m;
            m = new PhysicsMaterial("Task1_MugPhysics") { dynamicFriction = 0.6f, staticFriction = 0.7f, bounciness = 0f,
                frictionCombine = PhysicsMaterialCombine.Average, bounceCombine = PhysicsMaterialCombine.Minimum };
            AssetDatabase.CreateAsset(m, path);
            return m;
        }

        /// <summary>Flat pad marker (translucent fill + white border), no collider, no snapping.</summary>
        static PlacementPad BuildPad(Transform parent)
        {
            var go = new GameObject("Placement Pad (pick & place)");
            go.transform.SetParent(parent, false);
            var visual = new GameObject("Pad Visual").transform;
            visual.SetParent(go.transform, false);
            var fill = Prim(PrimitiveType.Cube, "Fill", visual, Vector3.zero, new Vector3(1f, 0.001f, 1f), "PadFill");
            const float b = 0.06f; // border thickness as a fraction of the pad size
            Prim(PrimitiveType.Cube, "Border N", visual, new Vector3(0, 0.0004f, 0.5f - b / 2), new Vector3(1f, 0.0012f, b), "PadBorder");
            Prim(PrimitiveType.Cube, "Border S", visual, new Vector3(0, 0.0004f, -0.5f + b / 2), new Vector3(1f, 0.0012f, b), "PadBorder");
            Prim(PrimitiveType.Cube, "Border E", visual, new Vector3(0.5f - b / 2, 0.0004f, 0), new Vector3(b, 0.0012f, 1f), "PadBorder");
            Prim(PrimitiveType.Cube, "Border W", visual, new Vector3(-0.5f + b / 2, 0.0004f, 0), new Vector3(b, 0.0012f, 1f), "PadBorder");
            var pad = go.AddComponent<PlacementPad>();
            pad.Configure(visual, fill.GetComponent<Renderer>());
            return pad;
        }

        static HandRestMarker BuildHandRest(Transform parent)
        {
            var go = new GameObject("Hand Rest Marker");
            go.transform.SetParent(parent, false);
            var marker = go.AddComponent<HandRestMarker>();
            var oval = Prim(PrimitiveType.Cylinder, "Oval", go.transform, new Vector3(0, 0.001f, 0), new Vector3(0.13f, 0.001f, 0.09f), "RestOval");
            var ring = Prim(PrimitiveType.Cylinder, "Ready Ring", go.transform, new Vector3(0, 0.0005f, 0), new Vector3(0.16f, 0.0008f, 0.12f), "RestRing");
            marker.Configure(oval.transform, oval.GetComponent<Renderer>(), ring.GetComponent<Renderer>());
            return marker;
        }

        // =================================================================== UI

        static StudyPanel BuildPanel(string name, Transform parent, float widthMm, Camera cam, FeedbackPlayer feedback, int titleSize, int bodySize)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.worldCamera = cam;
            go.AddComponent<TrackedDeviceGraphicRaycaster>();
            var rt = (RectTransform)go.transform;
            rt.sizeDelta = new Vector2(widthMm, 400f);
            rt.localScale = Vector3.one * 0.001f;   // 1 UI unit = 1 mm

            var bg = new GameObject("Background", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            bg.transform.SetParent(go.transform, false);
            var bgRt = (RectTransform)bg.transform;
            bgRt.anchorMin = bgRt.anchorMax = new Vector2(0.5f, 0.5f);
            bgRt.pivot = new Vector2(0.5f, 0.5f);
            bgRt.sizeDelta = new Vector2(widthMm, 300f);
            bg.GetComponent<Image>().color = new Color(0.07f, 0.08f, 0.10f, 0.92f);
            var vlg = bg.GetComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(24, 24, 18, 18);
            vlg.spacing = 12;
            vlg.childAlignment = TextAnchor.UpperCenter;
            vlg.childControlWidth = true; vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
            bg.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            TextMeshProUGUI Text(string n, int size, FontStyles style, Color color)
            {
                var t = new GameObject(n, typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
                t.transform.SetParent(bg.transform, false);
                t.fontSize = size; t.fontStyle = style; t.color = color;
                t.alignment = TextAlignmentOptions.Center;
                t.textWrappingMode = TextWrappingModes.Normal;
                t.richText = true;
                return t;
            }
            var title = Text("Title", titleSize, FontStyles.Bold, Color.white);
            var bodyText = Text("Body", bodySize, FontStyles.Normal, new Color(0.92f, 0.93f, 0.95f));

            var area = new GameObject("Buttons", typeof(RectTransform), typeof(GridLayoutGroup), typeof(LayoutElement));
            area.transform.SetParent(bg.transform, false);
            var grid = area.GetComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(130f, 62f);            // ≥ 4 cm targets for poke and ray
            grid.spacing = new Vector2(12f, 12f);
            grid.childAlignment = TextAnchor.MiddleCenter;
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = Mathf.Max(1, Mathf.FloorToInt((widthMm - 48f + 12f) / 142f));
            var progress = Text("Progress", Mathf.RoundToInt(bodySize * 0.75f), FontStyles.Italic, new Color(0.65f, 0.70f, 0.78f));

            var template = new GameObject("Button Template", typeof(RectTransform), typeof(Image), typeof(Button));
            template.transform.SetParent(go.transform, false);
            ((RectTransform)template.transform).sizeDelta = new Vector2(130f, 62f);
            var button = template.GetComponent<Button>();
            button.targetGraphic = template.GetComponent<Image>();
            var colors = button.colors;
            colors.highlightedColor = new Color(1.25f, 1.25f, 1.25f); colors.pressedColor = new Color(0.7f, 0.7f, 0.7f);
            colors.colorMultiplier = 1.4f; colors.fadeDuration = 0.05f;
            button.colors = colors;
            button.navigation = new Navigation { mode = Navigation.Mode.None };
            var label = new GameObject("Label", typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
            label.transform.SetParent(template.transform, false);
            var lrt = (RectTransform)label.transform;
            lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one; lrt.offsetMin = new Vector2(6, 4); lrt.offsetMax = new Vector2(-6, -4);
            label.fontSize = 24; label.alignment = TextAlignmentOptions.Center; label.color = Color.white;
            label.textWrappingMode = TextWrappingModes.Normal;
            label.enableAutoSizing = true; label.fontSizeMin = 14; label.fontSizeMax = 24;
            template.SetActive(false);

            var panel = go.AddComponent<StudyPanel>();
            panel.Configure(bg, title, bodyText, progress, (RectTransform)area.transform, button, feedback);
            return panel;
        }

        // =================================================================== Agent

        static void BuildChair(Transform anchor)
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/ThirdParty/VirtualHumans/Chair/black_chair.fbx");
            if (model)
            {
                // Chair from the virtual-humans project: seat ≈ 0.45 m, backrest toward −z (away from the participant).
                var chairGO = (GameObject)PrefabUtility.InstantiatePrefab(model, anchor);
                chairGO.name = "Agent Chair";
                chairGO.transform.localPosition = new Vector3(0f, 0f, -0.04f);
                foreach (var r in chairGO.GetComponentsInChildren<Renderer>())
                {
                    r.sharedMaterials = Enumerable.Repeat(s_Mats["ChairBlack"], r.sharedMaterials.Length).ToArray();
                    r.shadowCastingMode = ShadowCastingMode.Off; r.receiveShadows = false;
                }
                foreach (var c in chairGO.GetComponentsInChildren<Collider>()) Object.DestroyImmediate(c);
                return;
            }
            var chair = new GameObject("Agent Chair").transform;
            chair.SetParent(anchor, false);
            Prim(PrimitiveType.Cube, "Seat", chair, new Vector3(0, 0.44f, 0), new Vector3(0.46f, 0.04f, 0.44f), "Chair");
            Prim(PrimitiveType.Cube, "Back", chair, new Vector3(0, 0.72f, -0.21f), new Vector3(0.46f, 0.52f, 0.04f), "Chair");
            for (int i = 0; i < 4; i++)
                Prim(PrimitiveType.Cube, "Leg", chair, new Vector3(i % 2 == 0 ? -0.2f : 0.2f, 0.21f, i < 2 ? -0.19f : 0.19f), new Vector3(0.035f, 0.42f, 0.035f), "Chair");
        }

        /// <summary>Realistically sized seated mannequin facing local +z (toward the participant).</summary>
        static void BuildAgent(Transform body, out Transform torso, out Transform head)
        {
            torso = Prim(PrimitiveType.Capsule, "Torso", body, new Vector3(0, 0.78f, -0.04f), new Vector3(0.36f, 0.3f, 0.24f), "AgentCloth").transform;
            Prim(PrimitiveType.Capsule, "Thigh L", body, new Vector3(-0.1f, 0.5f, 0.14f), new Vector3(0.13f, 0.2f, 0.13f), "AgentCloth").transform.localRotation = Quaternion.Euler(90, 0, 0);
            Prim(PrimitiveType.Capsule, "Thigh R", body, new Vector3(0.1f, 0.5f, 0.14f), new Vector3(0.13f, 0.2f, 0.13f), "AgentCloth").transform.localRotation = Quaternion.Euler(90, 0, 0);
            Prim(PrimitiveType.Capsule, "Shin L", body, new Vector3(-0.1f, 0.24f, 0.33f), new Vector3(0.11f, 0.22f, 0.11f), "AgentCloth");
            Prim(PrimitiveType.Capsule, "Shin R", body, new Vector3(0.1f, 0.24f, 0.33f), new Vector3(0.11f, 0.22f, 0.11f), "AgentCloth");
            Prim(PrimitiveType.Capsule, "Upper Arm L", body, new Vector3(-0.22f, 0.8f, -0.02f), new Vector3(0.09f, 0.15f, 0.09f), "AgentCloth");
            Prim(PrimitiveType.Capsule, "Upper Arm R", body, new Vector3(0.22f, 0.8f, -0.02f), new Vector3(0.09f, 0.15f, 0.09f), "AgentCloth");
            Prim(PrimitiveType.Capsule, "Forearm L", body, new Vector3(-0.2f, 0.63f, 0.1f), new Vector3(0.08f, 0.13f, 0.08f), "AgentSkin").transform.localRotation = Quaternion.Euler(80, 0, 0);
            Prim(PrimitiveType.Capsule, "Forearm R", body, new Vector3(0.2f, 0.63f, 0.1f), new Vector3(0.08f, 0.13f, 0.08f), "AgentSkin").transform.localRotation = Quaternion.Euler(80, 0, 0);
            Prim(PrimitiveType.Cylinder, "Neck", body, new Vector3(0, 1.08f, -0.03f), new Vector3(0.09f, 0.05f, 0.09f), "AgentSkin");
            head = new GameObject("Head").transform;
            head.SetParent(body, false);
            head.localPosition = new Vector3(0, 1.22f, -0.02f);
            Prim(PrimitiveType.Sphere, "Skull", head, Vector3.zero, new Vector3(0.19f, 0.23f, 0.21f), "AgentSkin");
            Prim(PrimitiveType.Sphere, "Hair", head, new Vector3(0, 0.035f, -0.015f), new Vector3(0.2f, 0.2f, 0.21f), "AgentHair");
            Prim(PrimitiveType.Sphere, "Eye L", head, new Vector3(-0.035f, 0.015f, 0.093f), new Vector3(0.025f, 0.025f, 0.015f), "AgentEye");
            Prim(PrimitiveType.Sphere, "Eye R", head, new Vector3(0.035f, 0.015f, 0.093f), new Vector3(0.025f, 0.025f, 0.015f), "AgentEye");
        }

        static GameObject Prim(PrimitiveType type, string name, Transform parent, Vector3 localPos, Vector3 localScale, string material)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = localScale;
            Object.DestroyImmediate(go.GetComponent<Collider>());   // contacts are geometric; nothing physical
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = s_Mats[material];
            r.shadowCastingMode = ShadowCastingMode.Off;               // no real-time shadows (frame budget)
            r.receiveShadows = false;
            return go;
        }

        // =================================================================== Assets

        static void CreateMaterials()
        {
            var lit = Shader.Find("Universal Render Pipeline/Simple Lit");
            var unlit = Shader.Find("Universal Render Pipeline/Unlit");
            Mat("MugBody", lit, Color.white);
            Mat("Sticker", unlit, new Color(0.97f, 0.97f, 0.95f));
            Mat("Shape", unlit, Color.gray);
            var ring = Mat("CueRing", unlit, new Color(1f, 0.85f, 0.15f));
            var halo = Mat("CueHalo", unlit, new Color(1f, 0.85f, 0.15f, 0.35f));
            MakeTransparent(halo);
            Mat("RestOval", unlit, new Color(0.12f, 0.30f, 0.45f));
            Mat("RestRing", unlit, new Color(0.30f, 0.85f, 0.45f));
            Mat("Chair", lit, new Color(0.4f, 0.36f, 0.33f));
            Mat("ChairBlack", lit, new Color(0.08f, 0.08f, 0.09f));
            Mat("AgentCloth", lit, new Color(0.36f, 0.42f, 0.5f));
            Mat("AgentSkin", lit, new Color(0.78f, 0.64f, 0.55f));
            Mat("AgentHair", lit, new Color(0.22f, 0.18f, 0.15f));
            Mat("AgentEye", lit, new Color(0.08f, 0.08f, 0.09f));
            AssetDatabase.ImportAsset(MaterialDir + "/Task1_DepthOccluder.shader");
            Mat("Occluder", Shader.Find("XRStudy/Task1 Depth Occluder") ?? unlit, Color.black);
            MakeTransparent(Mat("PadFill", unlit, new Color(1f, 1f, 1f, 0.25f)));
            Mat("PadBorder", unlit, new Color(0.95f, 0.95f, 0.95f));
            AssetDatabase.SaveAssets();
        }

        static Material Mat(string name, Shader shader, Color color)
        {
            string path = $"{MaterialDir}/Task1_{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (!m) { m = new Material(shader); AssetDatabase.CreateAsset(m, path); }
            m.shader = shader;
            m.SetColor("_BaseColor", color);
            m.enableInstancing = true;
            EditorUtility.SetDirty(m);
            s_Mats[name] = m;
            return m;
        }

        static void MakeTransparent(Material m)
        {
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", 0f);
            m.SetFloat("_ZWrite", 0f);
            m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.renderQueue = (int)RenderQueue.Transparent;
            EditorUtility.SetDirty(m);
        }

        static void ConfigureLighting()
        {
            var lightGO = new GameObject("Directional Light");
            var light = lightGO.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.0f;
            light.shadows = LightShadows.None;
            lightGO.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            RenderSettings.skybox = null;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.55f, 0.55f, 0.58f);
        }

        static StudyLayout[] LoadOrCreateLayouts()
        {
            StudyLayout Make(string file, string name, bool agent, bool counterbalance, string desc, params (string slot, Owner owner, float x, float z)[] slots)
            {
                string path = $"{LayoutDir}/{file}.asset";
                var l = AssetDatabase.LoadAssetAtPath<StudyLayout>(path);
                if (l) return l;
                l = ScriptableObject.CreateInstance<StudyLayout>();
                l.layoutName = name; l.showAgent = agent; l.allowOrderCounterbalancing = counterbalance; l.description = desc;
                l.slots = slots.Select(s => new MugSlot { slotName = s.slot, canonicalOwner = s.owner, position_m = new Vector2(s.x, s.z), yaw_deg = 0f }).ToList();
                AssetDatabase.CreateAsset(l, path);
                return l;
            }
            const float depth = 0.35f, spacing = 0.25f;
            var l1 = Make("Layout1_AgentAbsent", "Layout1", false, true,
                "Agent absent. Mugs in one straight line parallel to the near edge, 0.35 m in; P1, N, NPC left to right (rotated per participant when counterbalanced).",
                ("Left", Owner.Owned, -spacing, depth), ("Center", Owner.Neutral, 0f, depth), ("Right", Owner.AgentOwned, spacing, depth));
            var l2 = Make("Layout2_AgentPresent", "Layout2", true, true,
                "Layout 1 plus the seated agent across the table. Nothing else differs.",
                ("Left", Owner.Owned, -spacing, depth), ("Center", Owner.Neutral, 0f, depth), ("Right", Owner.AgentOwned, spacing, depth));
            var future = Make("FutureLayout_DepthZones", "FutureLayout", true, false,
                "Not used yet. Ownership tied to depth zones: P1 near-left, N middle-centre, NPC far-right (near the agent).",
                ("NearLeft", Owner.Owned, -0.28f, 0.18f), ("MiddleCenter", Owner.Neutral, 0f, 0.40f), ("FarRight", Owner.AgentOwned, 0.28f, 0.62f));
            AssetDatabase.SaveAssets();
            return new[] { l1, l2, future };
        }

        static ProtocolConfig LoadOrCreateConfig(StudyLayout absent, StudyLayout present)
        {
            var cfg = AssetDatabase.LoadAssetAtPath<ProtocolConfig>(ConfigPath);
            if (cfg && cfg.protocolId != "Task1-MugTouch-MR")
            {
                // Pre-MR (reach-grab-place) protocol: keep a copy, then start from the new defaults.
                AssetDatabase.CopyAsset(ConfigPath, ConfigPath.Replace(".asset", "_ReachGrabPlace_v0.1.asset"));
                AssetDatabase.DeleteAsset(ConfigPath);
                cfg = null;
            }
            if (!cfg)
            {
                cfg = ScriptableObject.CreateInstance<ProtocolConfig>();
                AssetDatabase.CreateAsset(cfg, ConfigPath);
            }
            if (!cfg.agentAbsentLayout) cfg.agentAbsentLayout = absent;
            if (!cfg.agentPresentLayout) cfg.agentPresentLayout = present;
            EditorUtility.SetDirty(cfg);
            AssetDatabase.SaveAssets();
            return cfg;
        }

        static void AddSceneToBuild()
        {
            var list = EditorBuildSettings.scenes.Where(s => s.path != ScenePath)
                .Select(s => new EditorBuildSettingsScene(s.path, false)).ToList();
            list.Insert(0, new EditorBuildSettingsScene(ScenePath, true));
            EditorBuildSettings.scenes = list.ToArray();
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }
}
