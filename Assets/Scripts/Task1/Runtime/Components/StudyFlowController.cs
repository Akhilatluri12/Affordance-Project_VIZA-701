using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR;
using UnityEngine.XR.Management;
#if TASK1_META_OPENXR
using UnityEngine.XR.OpenXR.Features.Meta;
#endif

namespace XRStudy.Task1
{
    public enum StudyPhase { Setup, Calibration, Training, BlockIntro, Block, Break, End, Aborted, Faulted }

    [Serializable]
    public class SessionMetadata
    {
        public string schemaVersion, sessionId, participantId, sessionTimestamp, taskId;
        public string protocolId, protocolVersion, protocolHash;
        public long seed;
        public string scheduleGeneratorVersion, conditionOrder, conditionOrderSource, mugOrder, mugOrderSource;
        public int trialsPerBlock, trainingTrials;
        public string agentAbsentLayout, agentPresentLayout, agentAbsentLayoutJson, agentPresentLayoutJson;
        public string responseHand, agentName, task, agentAvatar, taskSelection, conditionSelection;
        public string appVersion, unityVersion, platform, deviceModel, operatingSystem, xrLoader;
        public float requestedRefreshRate_hz, reportedRefreshRate_hz;
        public string clock = "Time.realtimeSinceStartupAsDouble (s, monotonic); one sample per rendered frame";
        public string timingNote = "Response time = first mug contact - cue onset, both at frame resolution (~11 ms at 90 Hz). Cue onset is the application cue-request time, not photon onset.";
        public string buildInfo;
        public List<string> interruptedSessionsFoundAtStartup = new List<string>();
    }

    /// <summary>
    /// Study flow: Setup → Calibration → Training (ownership intro, recall quiz, practice) → Block → Break →
    /// Block → End. Owns the session lifecycle (protocol lock, files) and the experimenter actions.
    /// </summary>
    public class StudyFlowController : MonoBehaviour
    {
        [SerializeField] ProtocolConfig m_Protocol;
        [SerializeField] TrialManager m_Trials;
        [SerializeField] DataLogger m_Logger;
        [SerializeField] LayoutController m_Layout;
        [SerializeField] TableCalibration m_Table;
        [SerializeField] ARTableDetector m_Detector;
        [SerializeField] EffectorTracker m_Effectors;
        [SerializeField] AgentPresenceController m_Agent;
        [SerializeField] StudyPanel m_Panel;
        [SerializeField] ContactDetector m_Contacts;
        [SerializeField] FeedbackPlayer m_Feedback;
        [SerializeField] Transform m_Head;
        [SerializeField] Transform m_RightController;
        [SerializeField] Transform m_LeftController;

        [Header("Editor / desktop testing")]
        [Tooltip("Editor only: ignore Editor window focus changes. No effect in builds.")]
        [SerializeField] bool m_EditorIgnoreFocus;

        ProtocolConfig m_Cfg;
        StudySchedule m_Schedule;
        string m_SessionId = "", m_ParticipantDigits = "", m_SessionTimestamp = "";
        ConditionOrderMode m_OrderMode = ConditionOrderMode.Auto;
        MugOrderMode m_MugOrderMode = MugOrderMode.Auto;
        StudyTask m_Task = StudyTask.Touch;                 // task of the block currently running
        TaskSelection m_Tasks = TaskSelection.Both;         // session plan: which task(s)
        ConditionSelection m_Conditions = ConditionSelection.Both;  // session plan: with / without Alex
        int m_TrialsPerBlock;
        bool m_SessionOpen, m_Recalibrating;
        int m_CalibrationCount, m_RecallAttempts, m_BlockNumber;
        string m_LastOutcome = "";
        Coroutine m_Flow;
        InputAction m_Capture;
        bool m_CaptureWas;
        List<string> m_Interrupted = new List<string>();

        public StudyPhase Phase { get; private set; } = StudyPhase.Setup;
        public string SessionId => m_SessionId;
        public ProtocolConfig ActiveConfig => m_Cfg;
        public StudySchedule Schedule => m_Schedule;
        public bool TrialsPaused => m_Trials && m_Trials.Paused;
        /// <summary>Correct answer of the recall question on screen (debug simulator).</summary>
        public Owner? CurrentQuizAnswer { get; private set; }
        public bool AwaitingCalibrationCapture { get; private set; }

        public bool EditorIgnoreFocus
        {
            get => m_EditorIgnoreFocus;
            set { m_EditorIgnoreFocus = value; if (m_Trials) m_Trials.IgnoreFocus = Application.isEditor && value; }
        }

        public void Configure(ProtocolConfig protocol, TrialManager trials, DataLogger logger, LayoutController layout, TableCalibration table,
            ARTableDetector detector, EffectorTracker effectors, AgentPresenceController agent, StudyPanel panel, Transform head,
            Transform left, Transform right)
        {
            m_Protocol = protocol; m_Trials = trials; m_Logger = logger; m_Layout = layout; m_Table = table; m_Detector = detector;
            m_Effectors = effectors; m_Agent = agent; m_Panel = panel; m_Head = head; m_LeftController = left; m_RightController = right;
        }

        public void ConfigureFeedback(ContactDetector contacts, FeedbackPlayer feedback) { m_Contacts = contacts; m_Feedback = feedback; }

        void Awake()
        {
            m_Capture = new InputAction("CalibrationCapture", InputActionType.Button);
            m_Capture.AddBinding("<XRController>{RightHand}/triggerPressed");
            m_Capture.AddBinding("<XRController>{RightHand}/primaryButton");
            m_Capture.AddBinding("<Keyboard>/c");
            m_Capture.Enable();
        }

        void OnDestroy() => m_Capture?.Dispose();

        void Start()
        {
            m_Trials.IgnoreFocus = Application.isEditor && m_EditorIgnoreFocus;
            m_Logger.LoggingFailed += OnLoggingFailed;
            m_Trials.TrialCommitted += r => m_LastOutcome = $"{r.planned.plannedTrialId} {r.outcome}" +
                (r.ResponseTime.HasValue ? $" RT {r.ResponseTime.Value * 1000:0} ms" : "");
            m_TrialsPerBlock = m_Protocol ? m_Protocol.trialsPerBlock : 12;
            m_OrderMode = m_Protocol ? m_Protocol.conditionOrder : ConditionOrderMode.Auto;
            m_MugOrderMode = m_Protocol ? m_Protocol.mugOrder : MugOrderMode.Auto;
            m_Tasks = m_Protocol ? m_Protocol.defaultTasks : TaskSelection.Both;
            m_Conditions = m_Protocol ? m_Protocol.defaultConditions : ConditionSelection.Both;
            m_Flow = StartCoroutine(Flow());
        }

        // =================================================================== Flow

        IEnumerator Flow()
        {
            yield return null; // let tracking start
            InitialPlacement();
            yield return Setup();
            if (!m_SessionOpen) yield break;
            yield return Calibration(false);
            // Run the session plan: each task's practice, then its blocks (with / without Alex as selected).
            bool firstTraining = true;
            m_BlockNumber = 0;
            foreach (var block in m_Schedule.sequence)
            {
                if (block.training)
                {
                    yield return Training(block, firstTraining);
                    firstTraining = false;
                }
                else
                {
                    m_BlockNumber++;
                    yield return BlockIntro(block);
                    yield return RunBlock(block, (k, n) => $"Block {m_BlockNumber} of {m_Schedule.blocks.Count}  ·  Trial {k} of {n}");
                    if (m_BlockNumber < m_Schedule.blocks.Count && !NextIsTraining(block)) yield return Break();
                }
                if (Phase == StudyPhase.Faulted || Phase == StudyPhase.Aborted) yield break;
            }
            End("Completed");
        }

        bool NextIsTraining(ScheduledBlock block)
        {
            int i = m_Schedule.sequence.IndexOf(block);
            return i >= 0 && i + 1 < m_Schedule.sequence.Count && m_Schedule.sequence[i + 1].training;
        }

        void InitialPlacement()
        {
            var saved = TableCalibration.Load();
            if (saved != null) m_Table.Apply(saved, false);
            else PlaceTableInFrontOfHead();
            var layout = m_Protocol ? m_Protocol.agentAbsentLayout : null;
            m_Layout.Apply(layout, 0);
            SetMugsVisible(false);
            m_Layout.HandRest.Show(HandRestMarker.Visual.Hidden);
        }

        void PlaceTableInFrontOfHead()
        {
            Vector3 fwd = m_Head ? m_Head.forward : Vector3.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            Vector3 head = m_Head ? m_Head.position : new Vector3(0f, 1.2f, 0f);
            m_Table.Preview(new TableCalibrationData { position = head + fwd.normalized * 0.3f - Vector3.up * 0.45f, yaw_deg = Quaternion.LookRotation(fwd).eulerAngles.y });
        }

        void SetMugsVisible(bool v)
        {
            foreach (var m in m_Layout.Mugs) if (m) m.gameObject.SetActive(v);
        }

        // ------------------------------------------------------------------- Setup (experimenter)

        IEnumerator Setup()
        {
            Phase = StudyPhase.Setup;
            string root = SessionWriter.SessionsRoot(Application.persistentDataPath);
            try { m_Interrupted = SessionWriter.MarkInterruptedSessions(root); } catch (Exception e) { Debug.LogWarning(e.Message); }
            var problems = m_Protocol ? m_Protocol.Validate() : new List<string> { "No protocol assigned." };
            if (problems.Count > 0)
            {
                m_Panel.Show("Setup problem", string.Join("\n", problems), "Fix the protocol asset and restart.");
                yield break;
            }

            int step = 0;
            bool start = false;
            while (!start)
            {
                string pid = "P" + m_ParticipantDigits;
                switch (step)
                {
                    case 0:
                        var keys = new List<PanelButton>();
                        foreach (var d in new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" })
                            keys.Add(new PanelButton(d, () => { if (m_ParticipantDigits.Length < 6) m_ParticipantDigits += d; }, ButtonStyle.Normal, "digit" + d));
                        keys.Add(new PanelButton("Delete", () => { if (m_ParticipantDigits.Length > 0) m_ParticipantDigits = m_ParticipantDigits.Substring(0, m_ParticipantDigits.Length - 1); }, ButtonStyle.Normal, "delete"));
                        keys.Add(new PanelButton("Next", () => { if (m_ParticipantDigits.Length > 0) step = 1; }, m_ParticipantDigits.Length > 0 ? ButtonStyle.Primary : ButtonStyle.Normal, "next"));
                        m_Panel.Show("Setup (experimenter)", $"Participant ID:  <b>{(m_ParticipantDigits.Length > 0 ? pid : "P_")}</b>\nType the participant number.",
                            "Step 1 of 4", keys.ToArray());
                        break;
                    case 1:
                        var auto = ScheduleGenerator.AutoFirstCondition(pid);
                        var b1 = new List<PanelButton>
                        {
                            new PanelButton("Touch", () => m_Tasks = TaskSelection.Touch, Sel(m_Tasks == TaskSelection.Touch), "tasks-touch"),
                            new PanelButton("Pick & place", () => m_Tasks = TaskSelection.PickPlace, Sel(m_Tasks == TaskSelection.PickPlace), "tasks-pickplace"),
                            new PanelButton("Both tasks", () => m_Tasks = TaskSelection.Both, Sel(m_Tasks == TaskSelection.Both), "tasks-both"),
                            new PanelButton("With & without Alex", () => m_Conditions = ConditionSelection.Both, Sel(m_Conditions == ConditionSelection.Both), "cond-both"),
                            new PanelButton("Without Alex only", () => m_Conditions = ConditionSelection.AgentAbsentOnly, Sel(m_Conditions == ConditionSelection.AgentAbsentOnly), "cond-absent"),
                            new PanelButton("With Alex only", () => m_Conditions = ConditionSelection.AgentPresentOnly, Sel(m_Conditions == ConditionSelection.AgentPresentOnly), "cond-present"),
                        };
                        if (m_Conditions == ConditionSelection.Both)
                        {
                            b1.Add(new PanelButton("Order: Auto", () => m_OrderMode = ConditionOrderMode.Auto, Sel(m_OrderMode == ConditionOrderMode.Auto), "order-auto"));
                            b1.Add(new PanelButton("Without Alex first", () => m_OrderMode = ConditionOrderMode.AbsentFirst, Sel(m_OrderMode == ConditionOrderMode.AbsentFirst), "order-absent"));
                            b1.Add(new PanelButton("With Alex first", () => m_OrderMode = ConditionOrderMode.PresentFirst, Sel(m_OrderMode == ConditionOrderMode.PresentFirst), "order-present"));
                        }
                        b1.Add(new PanelButton("Back", () => step = 0, ButtonStyle.Normal, "back"));
                        b1.Add(new PanelButton("Next", () => step = 2, ButtonStyle.Primary, "next"));
                        m_Panel.Show("Session plan (experimenter)",
                            $"Task: <b>{TasksName(m_Tasks)}</b>   ·   Alex: <b>{ConditionsName(m_Conditions)}</b>" +
                            (m_Conditions == ConditionSelection.Both ? $"\nOrder Auto for {pid} = {(auto == PresenceCondition.AgentAbsent ? "without Alex first" : "with Alex first")}" : "") +
                            (m_Tasks == TaskSelection.Both ? $"\nTask order (auto) = {(ScheduleGenerator.AutoTouchFirst(pid) ? "touch first" : "pick & place first")}" : ""),
                            "Step 2 of 4", b1.ToArray());
                        break;
                    case 2:
                        var layout = m_Protocol.agentAbsentLayout;
                        string autoOrder = layout.OrderLabel(ScheduleGenerator.AutoMugOrder(pid), o => m_Protocol.Visual(o).label);
                        string fixedOrder = layout.OrderLabel(0, o => m_Protocol.Visual(o).label);
                        int balanced = m_Protocol.BalancedTrialsPerBlock(m_TrialsPerBlock);
                        m_Panel.Show("Setup (experimenter)",
                            $"Trials per block: <b>{balanced}</b> (multiple of 3)\nMug order (left to right): Auto = {autoOrder}, Fixed = {fixedOrder}",
                            "Step 3 of 4",
                            new PanelButton("- 3", () => m_TrialsPerBlock = Math.Max(3, balanced - 3), ButtonStyle.Normal, "trials-minus"),
                            new PanelButton("+ 3", () => m_TrialsPerBlock = Math.Min(60, balanced + 3), ButtonStyle.Normal, "trials-plus"),
                            new PanelButton("Order: Auto", () => m_MugOrderMode = MugOrderMode.Auto, Sel(m_MugOrderMode == MugOrderMode.Auto), "mug-auto"),
                            new PanelButton("Order: Fixed", () => m_MugOrderMode = MugOrderMode.Fixed, Sel(m_MugOrderMode == MugOrderMode.Fixed), "mug-fixed"),
                            new PanelButton("Back", () => step = 1, ButtonStyle.Normal, "back"),
                            new PanelButton("Next", () => step = 3, ButtonStyle.Primary, "next"));
                        break;
                    default:
                        bool storage = StorageWritable(root);
                        var sb = new StringBuilder();
                        sb.AppendLine($"Participant <b>{pid}</b>  ·  Mugs: {m_MugOrderMode}  ·  {m_Protocol.BalancedTrialsPerBlock(m_TrialsPerBlock)} trials/block");
                        sb.AppendLine("<b>Run order:</b> " + PlanSummary(pid));
                        sb.AppendLine((storage ? "OK" : "<color=#ff7070>FAIL</color>") + "  storage   " +
                                      (m_Effectors.HeadTracked ? "OK" : "<color=#ffb040>check</color>") + "  head tracking");
                        if (m_Interrupted.Count > 0) sb.AppendLine($"{m_Interrupted.Count} earlier session(s) marked Interrupted.");
                        m_Panel.Show("Setup (experimenter)", sb.ToString(), "Step 4 of 4",
                            new PanelButton("Back", () => step = 2, ButtonStyle.Normal, "back"),
                            new PanelButton("Start session", () => { if (storage) start = true; }, storage ? ButtonStyle.Primary : ButtonStyle.Normal, "start"));
                        break;
                }
                yield return null;
            }
            if (!OpenSession("P" + m_ParticipantDigits)) Phase = StudyPhase.Faulted;
        }

        static string TaskName(StudyTask t) => t == StudyTask.PickPlace ? "Pick & place" : "Touch";
        static string TasksName(TaskSelection t) => t == TaskSelection.Both ? "Touch + Pick & place" : t == TaskSelection.PickPlace ? "Pick & place" : "Touch";
        static string ConditionsName(ConditionSelection c) => c == ConditionSelection.AgentAbsentOnly ? "without Alex only" : c == ConditionSelection.AgentPresentOnly ? "with Alex only" : "with & without";

        /// <summary>Readable run order for the setup summary, e.g. "Touch practice → Touch without Alex → …".</summary>
        string PlanSummary(string pid)
        {
            var preview = ScheduleGenerator.Generate(m_Protocol, 1, "preview", pid, m_OrderMode, m_MugOrderMode, m_TrialsPerBlock, m_Tasks, m_Conditions);
            var parts = new List<string>();
            foreach (var b in preview.sequence)
            {
                string task = TaskName(b.trials.Count > 0 ? b.trials[0].task : StudyTask.Touch);
                parts.Add(b.training ? $"{task} practice" : $"{task} {(b.presence == PresenceCondition.AgentPresent ? "with" : "without")} Alex ({b.trials.Count})");
            }
            return string.Join("  →  ", parts);
        }

        /// <summary>Debug / test hook: choose the session plan on the setup screen.</summary>
        public void DebugSetTask(StudyTask task) => m_Tasks = task == StudyTask.PickPlace ? TaskSelection.PickPlace : TaskSelection.Touch;
        public void DebugSetPlan(TaskSelection tasks, ConditionSelection conditions) { m_Tasks = tasks; m_Conditions = conditions; }
        public StudyTask Task => m_Task;
        public TaskSelection Tasks => m_Tasks;
        public ConditionSelection Conditions => m_Conditions;

        static ButtonStyle Sel(bool selected) => selected ? ButtonStyle.Selected : ButtonStyle.Normal;

        /// <summary>Debug / test hook: fills the setup screen.</summary>
        public void DebugSetParticipant(string digits) { m_ParticipantDigits = digits; }

        float m_LastStorageCheck = -10f;
        bool m_StorageOk;
        bool StorageWritable(string root)
        {
            if (Time.unscaledTime - m_LastStorageCheck < 2f) return m_StorageOk;
            m_LastStorageCheck = Time.unscaledTime;
            try
            {
                Directory.CreateDirectory(root);
                string probe = Path.Combine(root, ".write_probe");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                m_StorageOk = true;
            }
            catch { m_StorageOk = false; }
            return m_StorageOk;
        }

        bool OpenSession(string participantId)
        {
            m_Cfg = m_Protocol.CreateFrozenCopy();
            long seed = m_Cfg.seed != 0 ? m_Cfg.seed : StudyRng.NewSeed();
            var now = DateTime.UtcNow;
            m_SessionTimestamp = now.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            m_SessionId = $"{m_Cfg.taskId}_{participantId}_{now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}";
            string responsesName = $"{participantId}_{now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)}.csv";
            SessionWriter writer;
            try { writer = SessionWriter.Create(SessionWriter.SessionsRoot(Application.persistentDataPath), m_SessionId, participantId, m_Cfg.schemaVersion, responsesName); }
            catch (Exception e) { m_Panel.Show("Setup problem", "Could not create the session folder:\n" + e.Message, ""); return false; }

            m_Schedule = ScheduleGenerator.Generate(m_Cfg, seed, m_SessionId, participantId, m_OrderMode, m_MugOrderMode, m_TrialsPerBlock, m_Tasks, m_Conditions);
            m_Logger.Begin(writer, m_SessionId, participantId, m_SessionTimestamp, m_Cfg, seed, m_Schedule.conditionOrder, m_Schedule.mugOrderLabel);
            var man = writer.Manifest;
            man.protocolId = m_Cfg.protocolId; man.protocolVersion = m_Cfg.protocolVersion; man.protocolHash = m_Cfg.ComputeHash(); man.seed = seed;
            man.plannedTrials = 0;
            foreach (var b in m_Schedule.blocks) man.plannedTrials += b.trials.Count;
            man.exportRoute = $"adb pull /sdcard/Android/data/{Application.identifier}/files/{SessionWriter.RootFolderName}/{m_SessionId}";

            foreach (var mug in m_Layout.Mugs) mug.ApplyIdentity(m_Cfg.Visual(mug.Owner));
            m_Task = m_Schedule.sequence[0].trials[0].task;
            m_Layout.SetTask(m_Task);
            m_Agent.SetAvatar(m_Cfg.agentAvatar);
            m_Layout.tableWidth_m = m_Cfg.realTableWidth_m;
            m_Layout.tableDepth_m = m_Cfg.realTableDepth_m;

            var meta = new SessionMetadata
            {
                schemaVersion = m_Cfg.schemaVersion, sessionId = m_SessionId, participantId = participantId, sessionTimestamp = m_SessionTimestamp,
                taskId = m_Cfg.taskId, protocolId = m_Cfg.protocolId, protocolVersion = m_Cfg.protocolVersion, protocolHash = man.protocolHash,
                seed = seed, scheduleGeneratorVersion = ScheduleGenerator.Version, conditionOrder = m_Schedule.conditionOrder,
                conditionOrderSource = m_Schedule.conditionOrderSource, mugOrder = m_Schedule.mugOrderLabel, mugOrderSource = m_Schedule.mugOrderSource,
                trialsPerBlock = m_Schedule.trialsPerBlock, trainingTrials = m_Schedule.training.trials.Count,
                agentAbsentLayout = m_Cfg.agentAbsentLayout.layoutName, agentPresentLayout = m_Cfg.agentPresentLayout.layoutName,
                agentAbsentLayoutJson = JsonUtility.ToJson(m_Cfg.agentAbsentLayout), agentPresentLayoutJson = JsonUtility.ToJson(m_Cfg.agentPresentLayout),
                responseHand = m_Cfg.responseHand.ToString(), agentName = m_Cfg.agentName, task = m_Schedule.taskOrder, taskSelection = m_Schedule.taskSelection, conditionSelection = m_Schedule.conditionSelection,
                agentAvatar = m_Agent.Avatar.ToString(), appVersion = Application.version,
                unityVersion = Application.unityVersion, platform = Application.platform.ToString(), deviceModel = SystemInfo.deviceModel,
                operatingSystem = SystemInfo.operatingSystem, xrLoader = XRGeneralSettings.Instance?.Manager?.activeLoader?.name ?? "none",
                requestedRefreshRate_hz = RequestRefreshRate(m_Cfg.targetRefreshRate_hz), reportedRefreshRate_hz = ReadRefreshRate(),
                buildInfo = LoadBuildInfo(), interruptedSessionsFoundAtStartup = m_Interrupted
            };
            bool ok = m_Logger.WriteJsonOnce("protocol_config.json", m_Cfg.ToJson(), "Frozen protocol parameters used for this session.")
                      && m_Logger.WriteJsonOnce("session_metadata.json", JsonUtility.ToJson(meta, true), "Session, layouts, device and version metadata.")
                      && m_Logger.WriteJsonOnce("schedule.json", JsonUtility.ToJson(m_Schedule, true), "Generated schedule (reproducible from the seed).")
                      && m_Logger.WriteJsonOnce("schedule.csv", m_Schedule.ToCsv(), "Generated schedule, one row per planned trial.");
            if (!ok) { m_Panel.Show("Logging problem", "Could not save the session files.", ""); return false; }

            m_Trials.Initialize(m_Cfg, seed);
            RegisterPoseSources();
            m_SessionOpen = true;
            m_Logger.LogSessionEvent("SessionStarted", $"seed={seed} order={m_Schedule.conditionOrder} mugs={m_Schedule.mugOrderLabel}");
            m_Logger.Flush(true);
            return true;
        }

        void RegisterPoseSources()
        {
            m_Logger.ClearPoseSources();
            m_Logger.AddPoseSource("head", m_Head, () => m_Effectors.HeadTracked);
            m_Logger.AddPoseSource("controller_left", m_LeftController, () => true);
            m_Logger.AddPoseSource("controller_right", m_RightController, () => true);
            foreach (var hand in new[] { StudyHand.Left, StudyHand.Right })
            {
                var h = hand;
                m_Logger.AddPointSource(h == StudyHand.Left ? "effector_left_rest" : "effector_right_rest", () =>
                {
                    foreach (var e in m_Effectors.Active) if (e.hand == h) return (true, e.restPoint);
                    return (false, Vector3.zero);
                });
            }
            foreach (var mug in m_Layout.Mugs) m_Logger.AddPoseSource("mug_" + mug.Label, mug.transform, () => true);
        }

        static string LoadBuildInfo()
        {
            var ta = Resources.Load<TextAsset>("Task1BuildInfo");
            return ta ? ta.text : "unavailable (Editor)";
        }

        static XRDisplaySubsystem Display() => XRGeneralSettings.Instance?.Manager?.activeLoader?.GetLoadedSubsystem<XRDisplaySubsystem>();

        static float RequestRefreshRate(float hz)
        {
#if TASK1_META_OPENXR && !UNITY_EDITOR
            var d = Display();
            if (d != null && hz > 0f) { try { d.TryRequestDisplayRefreshRate(hz); } catch (Exception e) { Debug.LogWarning(e.Message); } }
#endif
            return hz;
        }

        static float ReadRefreshRate()
        {
            var d = Display();
            return d != null && d.TryGetDisplayRefreshRate(out float hz) ? hz : 0f;
        }

        // ------------------------------------------------------------------- Calibration (experimenter)

        IEnumerator Calibration(bool midSession)
        {
            var previousPhase = Phase;
            Phase = StudyPhase.Calibration;
            m_Recalibrating = true;
            // Mugs are fixed (kinematic) while the table preview moves; physics returns afterwards.
            m_Layout.SetTask(StudyTask.Touch);
            SetMugsVisible(true);
            m_Layout.HandRest.Show(HandRestMarker.Visual.Idle);
            var saved = m_Table.HasCalibration ? m_Table.Data : null;
            bool done = false;

            if (saved != null && !midSession)
            {
                int choice = 0;
                while (choice == 0)
                {
                    m_Panel.Show("Table calibration (experimenter)",
                        $"A saved calibration was found ({saved.method}).\nCheck that the blue oval sits on the near edge of the real table and the mugs sit on its surface.",
                        "", new PanelButton("Use saved", () => choice = 1, ButtonStyle.Primary, "use-saved"),
                        new PanelButton("Recalibrate", () => choice = 2, ButtonStyle.Normal, "recalibrate"));
                    yield return null;
                }
                if (choice == 1) { m_Table.Apply(saved, true); done = true; LogCalibration("UsedSaved"); }
            }

            while (!done)
            {
                // Live preview follows the right controller; trigger / A captures.
                int mode = 0; // 0 = controller, 1 = plane detection
                bool cancel = false;
                AwaitingCalibrationCapture = true;
                TableCalibrationData captured = null;
                m_CaptureWas = m_Capture.IsPressed();
                while (captured == null && !cancel)
                {
                    if (mode == 0)
                    {
                        bool tracked = m_RightController && IsRightControllerTracked();
                        if (tracked) m_Table.Preview(TableCalibration.FromController(new Pose(m_RightController.position, m_RightController.rotation), m_Cfg.controllerOnTableHeight_m));
                        m_Panel.Show("Table calibration (experimenter)",
                            "Lay the RIGHT controller flat on the table, centred on the near edge in front of the participant's seat, pointing across the table.\nThen press the trigger.",
                            tracked ? "Controller tracked - the mugs follow it." : "Pick up the right controller...",
                            new PanelButton("Detect table automatically", () => { mode = 1; m_Detector.Begin(); }, ButtonStyle.Normal, "auto-detect"),
                            new PanelButton(midSession ? "Cancel" : "Use saved", () => cancel = true, ButtonStyle.Normal, "cancel"));
                        bool pressed = m_Capture.IsPressed();
                        if (tracked && pressed && !m_CaptureWas)
                            captured = TableCalibration.FromController(new Pose(m_RightController.position, m_RightController.rotation), m_Cfg.controllerOnTableHeight_m);
                        m_CaptureWas = pressed;
                    }
                    else
                    {
                        var found = m_Detector.TryGetTable();
                        if (found != null) m_Table.Preview(found);
                        m_Panel.Show("Table calibration (experimenter)", "Automatic table detection uses the table from the headset's room setup.\n" + m_Detector.Status, "",
                            new PanelButton("Use detected table", () => { if (found != null) captured = found; }, found != null ? ButtonStyle.Primary : ButtonStyle.Normal, "use-detected"),
                            new PanelButton("Use controller instead", () => { mode = 0; m_Detector.Stop(); }, ButtonStyle.Normal, "use-controller"));
                    }
                    if (m_DebugCalibration != null) { captured = m_DebugCalibration; m_DebugCalibration = null; }
                    yield return null;
                }
                AwaitingCalibrationCapture = false;
                m_Detector.Stop();
                if (cancel)
                {
                    if (m_Table.HasCalibration) m_Table.Apply(m_Table.Data, false);
                    else PlaceTableInFrontOfHead();
                    if (m_Table.HasCalibration || midSession) break;
                    continue;
                }

                captured.heightNudge_m = 0f;
                m_Table.Apply(captured, true);
                ApplyCurrentLayout();
                int verdict = 0;
                while (verdict == 0)
                {
                    m_Panel.Show("Check the calibration (experimenter)",
                        $"The mugs should stand on the real table in a straight line, the blue oval on the near edge.\nHeight offset: {m_Table.Data.heightNudge_m * 1000f:+0;-0;0} mm",
                        "",
                        new PanelButton("Raise 5 mm", () => { m_Table.NudgeHeight(0.005f); }, ButtonStyle.Normal, "raise"),
                        new PanelButton("Lower 5 mm", () => { m_Table.NudgeHeight(-0.005f); }, ButtonStyle.Normal, "lower"),
                        new PanelButton("Redo", () => verdict = 2, ButtonStyle.Normal, "redo"),
                        new PanelButton("Looks right", () => verdict = 1, ButtonStyle.Primary, "accept"));
                    if (m_DebugAccept) { m_DebugAccept = false; verdict = 1; }
                    yield return null;
                }
                if (verdict == 1) { done = true; LogCalibration(captured.method); }
            }
            m_Recalibrating = false;
            m_Layout.SetTask(m_SessionOpen ? m_Task : StudyTask.Touch);
            ApplyCurrentLayout();
            Phase = midSession ? previousPhase : StudyPhase.Calibration;
        }

        TableCalibrationData m_DebugCalibration;
        bool m_DebugAccept;

        /// <summary>Debug / test hook: calibrate as if the controller lay at this pose, and accept it.</summary>
        public void DebugCalibrate(Vector3 nearEdgeCentre, float yawDeg)
        {
            m_DebugCalibration = new TableCalibrationData { position = nearEdgeCentre, yaw_deg = yawDeg, method = "Debug" };
            m_DebugAccept = true;
        }

        bool IsRightControllerTracked() => m_Effectors.ControllerTracked(StudyHand.Right);

        void LogCalibration(string method)
        {
            m_CalibrationCount++;
            var d = m_Table.Data;
            if (m_SessionOpen)
            {
                m_Logger.WriteJsonOnce($"calibration_{m_CalibrationCount}.json", JsonUtility.ToJson(d, true), "Table calibration (near-edge centre, yaw).");
                m_Logger.LogSessionEvent("Calibrated", $"{method} pos={Csv.F(d.position.x)};{Csv.F(d.position.y)};{Csv.F(d.position.z)} yaw={Csv.F(d.yaw_deg)} nudge={Csv.F(d.heightNudge_m)}");
                m_Logger.Flush(true);
            }
        }

        PresenceCondition m_CurrentPresence = PresenceCondition.AgentAbsent;
        void ApplyCurrentLayout()
        {
            var cfg = m_Cfg ? m_Cfg : m_Protocol;
            var layout = ScheduleGenerator.LayoutFor(cfg, m_CurrentPresence);
            m_Layout.Apply(layout, m_Schedule != null ? m_Schedule.mugOrderIndex : 0);
            if (m_Recalibrating) m_Agent.SetPresent(false);
        }

        void ApplyPresence(PresenceCondition p, string context)
        {
            m_CurrentPresence = p;
            ApplyCurrentLayout();
            m_Logger.LogSessionEvent("AgentPresence", (p == PresenceCondition.AgentPresent ? "present" : "absent") + " (" + context + ")");
        }

        // ------------------------------------------------------------------- Training (participant)

        /// <summary>Practice for one task. The first practice of the session also introduces the mugs' owners.</summary>
        IEnumerator Training(ScheduledBlock training, bool first)
        {
            Phase = StudyPhase.Training;
            m_Task = training.trials[0].task;
            m_Layout.SetTask(m_Task);
            m_CurrentPresence = training.presence;
            ApplyCurrentLayout();
            m_Layout.HandRest.Show(HandRestMarker.Visual.Idle);

            if (first)
            {
                string what = m_Tasks == TaskSelection.Both ? "touch mugs, and later pick them up and move them," : m_Task == StudyTask.PickPlace ? "pick up mugs and move them" : "touch mugs";
                yield return Page("Welcome", $"In this study you will {what} on the table in front of you.\nPress Next to continue.", "Next");
                while (true)
                {
                    yield return OwnershipIntro();
                    bool allCorrect = true;
                    yield return RecallQuiz(r => allCorrect = r);
                    if (allCorrect) break;
                    m_Logger.LogSessionEvent("OwnershipIntroRepeated", $"attempt={m_RecallAttempts}");
                }
            }
            else
            {
                yield return Page("New task", m_Task == StudyTask.PickPlace
                    ? "Next you will pick up the mugs and move them onto a white square.\nThe mugs still belong to the same people."
                    : "Next you will only touch the mugs.\nThe mugs still belong to the same people.", "Next");
            }
            ApplyCurrentLayout();
            string hand = m_Cfg.HandName;
            if (m_Task == StudyTask.PickPlace)
            {
                yield return Page("How it works",
                    $"1. Rest your {hand} hand on the blue oval.\n2. Wait. One mug will light up.\n3. With your {hand} hand, pick up the glowing mug and put it down on the white square.\n4. Let go, then put your hand back on the oval.",
                    "Next");
                yield return Page("Practice", $"Let's practise. Use only your {hand} hand, and grab the mug directly - pointing does not work.\nDuring practice you will see whether you picked the right mug.", "Start practice");
            }
            else
            {
                yield return Page("How it works",
                    $"1. Rest your {hand} hand on the blue oval.\n2. Wait. One mug will light up.\n3. Touch the glowing mug as quickly as you can. A light touch is enough.\n4. Put your hand back on the oval.",
                    "Next");
                yield return Page("Practice", $"Let's practise. Use your {hand} hand.\nDuring practice you will see whether you touched the right mug.", "Start practice");
            }
            yield return RunBlock(training, (k, n) => $"{TaskName(m_Task)} practice {k} of {n}");
            if (Phase == StudyPhase.Faulted || Phase == StudyPhase.Aborted) yield break;
            yield return Page("Practice complete", m_Task == StudyTask.PickPlace
                ? "Well done! The study starts next.\nFrom now on there is no right/wrong feedback - just move the glowing mug onto the square as quickly as you can."
                : "Well done! The study starts next.\nFrom now on there is no right/wrong feedback - just touch the glowing mug as quickly as you can.", "Continue");
        }

        IEnumerator OwnershipIntro()
        {
            // The agent is introduced here even if the first block is agent-absent.
            m_Agent.SetPresent(true);
            m_Logger.LogSessionEvent("AgentPresence", "present (ownership intro)");
            var order = new[] { Owner.Owned, Owner.AgentOwned, Owner.Neutral };
            foreach (var o in order)
            {
                var v = m_Cfg.Visual(o);
                var mug = m_Layout.MugFor(o);
                string whose = o == Owner.Owned ? "is YOUR mug" : o == Owner.AgentOwned ? $"belongs to {m_Cfg.agentName}, who is sitting across the table" : "belongs to no one";
                if (mug) mug.SetCue(true, Color.Lerp(v.color, Color.white, 0.3f), 1.2f);
                yield return Page("Whose mug is whose?", $"The {v.colorName.ToUpperInvariant()} mug with the {v.shape.ToString().ToLowerInvariant()} ({v.label}) {whose}.", "Next");
                if (mug) mug.SetCue(false, m_Cfg.cueColor, m_Cfg.cueIntensity);
            }
            yield return Page("Whose mug is whose?", "You may touch every mug during the study, no matter whose it is.", "Next");
        }

        /// <summary>
        /// Ownership check: the participant answers by touching the mug ("Touch YOUR mug") - the same action as
        /// the task - or with the ray buttons. A wrong answer repeats the ownership introduction.
        /// </summary>
        IEnumerator RecallQuiz(Action<bool> result)
        {
            m_RecallAttempts++;
            bool all = true;
            var order = new[] { Owner.Owned, Owner.AgentOwned, Owner.Neutral };
            for (int q = 0; q < order.Length; q++)
            {
                var o = order[q];
                var v = m_Cfg.Visual(o);
                Owner? answer = null;
                Action<StudyMug, string, double, int, long> onTouch = (mug, eff, t, f, k) => { if (!m_Recalibrating) answer ??= mug.Owner; };
                m_Contacts.Contacted += onTouch;
                CurrentQuizAnswer = o;
                string ask = o == Owner.Owned ? "Touch YOUR mug." : o == Owner.AgentOwned ? $"Touch {m_Cfg.agentName}'s mug." : "Touch the mug that belongs to no one.";
                while (!answer.HasValue)
                {
                    if (!m_Recalibrating)
                        m_Panel.Show("Quick check", ask, $"Question {q + 1} of 3  ·  touch the mug, or point and pinch on an answer",
                            new PanelButton("Mine", () => answer = Owner.Owned, ButtonStyle.Normal, "Owned"),
                            new PanelButton($"{m_Cfg.agentName}'s", () => answer = Owner.AgentOwned, ButtonStyle.Normal, "AgentOwned"),
                            new PanelButton("No one's", () => answer = Owner.Neutral, ButtonStyle.Normal, "Neutral"));
                    yield return null;
                }
                m_Contacts.Contacted -= onTouch;
                CurrentQuizAnswer = null;
                m_Logger.LogRecall(m_RecallAttempts, v.label, o, answer.Value);
                if (answer.Value != o)
                {
                    all = false;
                    m_Feedback?.Incorrect();
                    string correct = o == Owner.Owned ? "YOUR mug" : o == Owner.AgentOwned ? $"{m_Cfg.agentName}'s mug" : "the mug that belongs to no one";
                    yield return Page("Not quite", $"{correct.Substring(0, 1).ToUpperInvariant() + correct.Substring(1)} is the {v.colorName} mug with the {v.shape.ToString().ToLowerInvariant()} ({v.label}). Let's go over the mugs once more.", "OK");
                    break;
                }
                m_Feedback?.Correct();
                yield return Wait(0.6f);
            }
            m_Logger.Flush(true);
            result(all);
        }

        static IEnumerator Wait(float seconds)
        {
            float t0 = Time.unscaledTime;
            while (Time.unscaledTime - t0 < seconds) yield return null;
        }

        /// <summary>
        /// One instruction at a time. Continue by resting the hand on the blue oval for a second (always within
        /// reach) or by pressing the button with a ray. The hand must leave the oval first, so a resting hand
        /// never skips a page by accident.
        /// </summary>
        IEnumerator Page(string title, string body, string button)
        {
            bool next = false;
            bool armed = !HandOnOval();
            float holdStart = -1f;
            const float hold = 1.0f;
            while (!next)
            {
                bool on = HandOnOval();
                if (!on) { armed = true; holdStart = -1f; }
                else if (armed)
                {
                    if (holdStart < 0f) holdStart = Time.unscaledTime;
                    if (Time.unscaledTime - holdStart >= hold) next = true;
                }
                if (!m_Recalibrating)
                {
                    m_Layout.HandRest.Show(on && armed ? HandRestMarker.Visual.Ready : HandRestMarker.Visual.Idle);
                    string hint = on && armed ? "Keep your hand on the oval..." : $"Rest your hand on the blue oval (or press {button}) to continue";
                    m_Panel.Show(title, body, (ProgressForPhase().Length > 0 ? ProgressForPhase() + "  ·  " : "") + hint,
                        new PanelButton(button, () => next = true, ButtonStyle.Primary, "next"));
                }
                yield return null;
            }
            m_Feedback?.Button();
            m_Layout.HandRest.Show(HandRestMarker.Visual.Idle);
        }

        bool HandOnOval()
        {
            if (m_Cfg == null || m_Effectors == null) return false;
            var e = m_Effectors.ResponseEffector(m_Cfg.responseHand);
            if (e == null) return false;
            m_Layout.HandRest.Measure(e.restPoint, out float d, out float h);
            return d <= m_Cfg.restRadius_m && h <= m_Cfg.restMaxHeight_m;
        }

        string ProgressForPhase() => Phase == StudyPhase.Training ? "Training" : Phase == StudyPhase.Block || Phase == StudyPhase.BlockIntro ? $"Block {m_BlockNumber} of {m_Schedule?.blocks.Count}" : "";

        // ------------------------------------------------------------------- Blocks

        IEnumerator BlockIntro(ScheduledBlock block)
        {
            Phase = StudyPhase.BlockIntro;
            m_Task = block.trials[0].task;
            m_Layout.SetTask(m_Task);
            ApplyPresence(block.presence, block.blockId);
            // Identical wording in both conditions.
            yield return Page($"Part {m_BlockNumber} of {m_Schedule.blocks.Count}",
                m_Task == StudyTask.PickPlace
                    ? $"Rest your {m_Cfg.HandName} hand on the blue oval. When a mug lights up, pick it up and put it on the white square as quickly as you can.\nPress Start when you are ready."
                    : $"Rest your {m_Cfg.HandName} hand on the blue oval and touch the mug that lights up as quickly as you can.\nPress Start when you are ready.", "Start");
            m_Logger.LogSessionEvent("BlockStarted", block.blockId);
        }

        IEnumerator RunBlock(ScheduledBlock block, Func<int, int, string> progress)
        {
            if (!block.training) Phase = StudyPhase.Block;
            yield return m_Trials.RunBlock(block, progress);
            if (m_Trials.Faulted) { EnterFault(m_Logger.LastError); yield break; }
            m_Logger.LogSessionEvent(block.training ? "TrainingFinished" : "BlockFinished", block.blockId);
            m_Logger.Flush(true);
        }

        IEnumerator Break()
        {
            Phase = StudyPhase.Break;
            yield return Page("Break", "Take a short break. Press Continue when you are ready for the next part.", "Continue");
        }

        // ------------------------------------------------------------------- End / abort / faults

        void End(string status)
        {
            Phase = status == "Completed" ? StudyPhase.End : StudyPhase.Aborted;
            if (!m_SessionOpen) return;
            m_Logger.LogSessionEvent("SessionEnded", status);
            m_Logger.Flush(true);
            m_Logger.Close(status);
            m_SessionOpen = false;
            foreach (var m in m_Layout.Mugs) m.SetCue(false, m_Cfg.cueColor, 0f);
            m_Agent.SetPresent(false);
            m_Layout.HandRest.Show(HandRestMarker.Visual.Hidden);
            m_Panel.Show(status == "Completed" ? "Thank you!" : "Session ended",
                status == "Completed" ? "You have finished the study. Please take off the headset when the researcher tells you." : "The session has ended. Thank you.",
                "");
        }

        void EnterFault(string error)
        {
            if (Phase == StudyPhase.Faulted) return;
            Phase = StudyPhase.Faulted;
            if (m_Flow != null) StopCoroutine(m_Flow);
            m_Panel.Show("Please wait", "Please wait for the researcher.", "");
            m_LastOutcome = "LOGGING FAULT: " + error;
        }

        void OnLoggingFailed(string error)
        {
            m_Trials.RequestPause(FaultReason.LoggingFailure, error);
            EnterFault(error);
        }

        void OnApplicationQuit()
        {
            if (m_SessionOpen) { m_Logger.LogSessionEvent("ApplicationQuit"); m_Logger.Close("Interrupted"); m_SessionOpen = false; }
        }

        // ------------------------------------------------------------------- Experimenter actions

        public void PauseTrials() { m_Trials.RequestPause(FaultReason.ResearcherPause, "experimenter"); m_Logger.LogSessionEvent("ExperimenterPause"); }
        public void ResumeTrials() { m_Trials.Resume(); m_Logger.LogSessionEvent("ExperimenterResume"); }
        public void SkipTrial() { m_Trials.Skip(); m_Logger.LogSessionEvent("ExperimenterSkip"); }

        public void Recalibrate()
        {
            if (Phase == StudyPhase.Setup || Phase == StudyPhase.End || Phase == StudyPhase.Aborted) return;
            if (Phase == StudyPhase.Calibration) return;
            StartCoroutine(RecalibrateRoutine());
        }

        IEnumerator RecalibrateRoutine()
        {
            bool wasRunning = m_Trials.Running;
            if (wasRunning) m_Trials.RequestPause(FaultReason.ResearcherPause, "recalibration");
            m_Trials.SuppressPausePanel = true;
            yield return Calibration(true);
            m_Trials.SuppressPausePanel = false;
            if (wasRunning) m_Trials.Resume();
        }

        public void Abort()
        {
            if (!m_SessionOpen) return;
            m_Trials.RequestPause(FaultReason.OperatorAbort, "experimenter");
            if (m_Flow != null) StopCoroutine(m_Flow);
            End("Aborted");
        }

        public string StatusLine
        {
            get
            {
                var sb = new StringBuilder(Phase.ToString());
                if (m_Trials.Running) sb.Append($"  ·  {m_Trials.CurrentPlanned?.blockId} trial {m_Trials.BlockTrialIndex + 1}/{m_Trials.BlockTrialCount}  ·  {m_Trials.State}");
                var man = m_Logger.Manifest;
                if (man != null) sb.Append($"  ·  valid {man.validTrials}, invalid {man.invalidAttempts}");
                return sb.ToString();
            }
        }

        public string StatusDetail
        {
            get
            {
                var sb = new StringBuilder();
                sb.AppendLine($"Session {(m_SessionId.Length > 0 ? m_SessionId : "(not started)")}");
                if (m_Schedule != null) sb.AppendLine($"Order {m_Schedule.conditionOrder}, mugs {m_Schedule.mugOrderLabel}, seed {m_Schedule.seed}");
                if (m_Trials.Paused) sb.AppendLine($"<color=#ffb040>Paused: {m_Trials.PauseReason}</color>");
                if (m_LastOutcome.Length > 0) sb.AppendLine("Last: " + m_LastOutcome);
                if (m_Logger.Manifest != null) sb.Append("Data: " + m_Logger.SessionDirectory);
                return sb.ToString();
            }
        }
    }
}
