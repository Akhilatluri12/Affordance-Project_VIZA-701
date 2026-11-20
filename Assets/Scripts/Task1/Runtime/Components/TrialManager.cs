using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace XRStudy.Task1
{
    /// <summary>
    /// Runs a block of planned trials through the <see cref="TrialStateMachine"/> (the sole owner of trial state,
    /// cue timing and outcomes). This component feeds it frames and contacts, shows instructions and feedback,
    /// paces trials, recovers from tracking/focus loss and repeats invalid attempts at the end of the block.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public class TrialManager : MonoBehaviour, ITrialSink
    {
        [SerializeField] LayoutController m_Layout;
        [SerializeField] EffectorTracker m_Effectors;
        [SerializeField] ContactDetector m_Contacts;
        [SerializeField] FeedbackPlayer m_Feedback;
        [SerializeField] StudyPanel m_Panel;
        [SerializeField] DataLogger m_Logger;
        [SerializeField] XRInteractionManager m_InteractionManager;

        ProtocolConfig m_Cfg;
        TrialStateMachine m_Machine;
        FaultReason? m_PauseReason;
        bool m_ResumeRequested;
        bool m_SkipAfterResume;
        string m_Progress = "";
        double m_AwaitStartSince;
        bool m_IdlePromptShown;
        readonly List<TrialRecord> m_BlockRecords = new List<TrialRecord>();

        public TrialState State => m_Machine?.State ?? TrialState.Idle;
        public bool Running { get; private set; }
        public bool Paused => State == TrialState.Paused;
        public bool Faulted => State == TrialState.Faulted || (m_Logger && m_Logger.Failed);
        public string PauseReason => m_PauseReason?.ToString() ?? "";
        public bool InTimedState => m_Machine != null && m_Machine.InTimedState;
        public PlannedTrial CurrentPlanned => m_Machine?.Current?.planned;
        public TrialRecord CurrentRecord => m_Machine?.Current;
        public int BlockTrialIndex { get; private set; }
        public int BlockTrialCount { get; private set; }
        public IReadOnlyList<TrialRecord> BlockRecords => m_BlockRecords;
        public bool IgnoreFocus { get; set; }
        /// <summary>Set while another screen (recalibration) owns the panel during a pause.</summary>
        public bool SuppressPausePanel { get; set; }
        public event Action<TrialRecord> TrialCommitted;

        public void Configure(LayoutController layout, EffectorTracker effectors, ContactDetector contacts, FeedbackPlayer feedback,
            StudyPanel panel, DataLogger logger)
        {
            m_Layout = layout; m_Effectors = effectors; m_Contacts = contacts; m_Feedback = feedback; m_Panel = panel; m_Logger = logger;
        }

        public void ConfigureInteraction(XRInteractionManager manager) => m_InteractionManager = manager;

        public void Initialize(ProtocolConfig frozenConfig, long seed)
        {
            m_Cfg = frozenConfig;
            // Foreperiods use their own stream derived from the saved seed (reproducible, independent of the schedule).
            m_Machine = new TrialStateMachine(m_Cfg, this, new StudyRng(seed ^ 0x5DEECE66DL), m_Logger.NextEventId);
            m_Logger.CurrentAttemptId = () => m_Machine?.Current?.attemptId ?? "";
            m_Logger.InTimedState = () => m_Machine != null && m_Machine.InTimedState;
            m_Layout.HandRest.SetWindow(m_Cfg.speedWindow_s);
            m_Contacts.Apply(m_Cfg);
            m_Contacts.Contacted -= OnContact;
            m_Contacts.Contacted += OnContact;
            m_Machine.DeselectRequested += reason => { m_Logger.LogSessionEvent("DeselectRequested", reason); CancelAllSelections(); };
            foreach (var mug in m_Layout.Mugs)
            {
                var bridge = mug.GetComponent<GrabEventBridge>();
                if (bridge) { bridge.Receiver = this; bridge.DesignatedHand = m_Cfg.responseHand; }
                var filter = mug.GetComponent<DesignatedHandSelectFilter>();
                if (filter) filter.designatedHand = m_Cfg.responseHand;
            }
        }

        // ---------------------------------------------------------------- pick & place events

        public void HandleSelectEntered(StudyMug mug, bool designated, string interactor, long key)
        {
            m_Feedback?.Contact(EffectorIdFromInteractor(interactor));
            if (Running && m_Machine?.Current != null)
                m_Machine.OnSelectEntered(mug.Owner, mug.Label, designated, interactor, key, MonotonicClock.Now, Time.frameCount);
            else m_Logger?.LogSessionEvent("GrabOutsideTrial", interactor, mug.Label);
        }

        public void HandleSelectExited(StudyMug mug, bool designated, string interactor, bool canceled, long key)
        {
            if (Running && m_Machine?.Current != null)
                m_Machine.OnSelectExited(mug.Owner, mug.Label, designated, interactor, canceled, key, MonotonicClock.Now, Time.frameCount);
        }

        static string EffectorIdFromInteractor(string name)
        {
            string side = name.Contains("Left") ? "Left" : "Right";
            return side + (name.Contains("Controller") ? "Controller" : "Hand");
        }

        void CancelAllSelections()
        {
            if (!m_InteractionManager) return;
            foreach (var m in m_Layout.Mugs)
                if (m && m.IsSelected) m_InteractionManager.CancelInteractableSelection((UnityEngine.XR.Interaction.Toolkit.Interactables.IXRSelectInteractable)m.Grab);
        }

        /// <summary>Spec FR-05: no far/ray grabbing during pick & place trials (UI rays come back between blocks).</summary>
        void SetFarCasting(bool on)
        {
            foreach (var nf in FindObjectsByType<NearFarInteractor>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                nf.enableFarCasting = on;
        }

        bool AnyMugSelected()
        {
            foreach (var m in m_Layout.Mugs) if (m && m.IsSelected) return true;
            return false;
        }

        /// <summary>Debug simulator only: injects a grab/release as if XRI had raised it.</summary>
        long m_DebugKey = long.MaxValue / 2;
        public void DebugSelect(StudyMug mug, bool entered)
        {
            if (entered) HandleSelectEntered(mug, true, "Sim/Right Hand", ++m_DebugKey);
            else HandleSelectExited(mug, true, "Sim/Right Hand", false, ++m_DebugKey);
        }

        void OnDestroy() { if (m_Contacts) m_Contacts.Contacted -= OnContact; }

        void OnContact(StudyMug mug, string effector, double t, int frame, long key)
        {
            bool pickPlace = m_Machine?.Current?.planned?.task == StudyTask.PickPlace;
            if (!pickPlace) m_Feedback?.Contact(effector);
            if (Running && m_Machine?.Current != null) m_Machine.OnContact(mug.Owner, mug.Label, effector, key, t, frame);
            else m_Logger?.LogSessionEvent("ContactOutsideTrial", effector, mug.Label);
        }

        // ---------------------------------------------------------------- pause / skip

        public void RequestPause(FaultReason reason, string detail = "")
        {
            if (m_Machine == null || !Running) return;
            if (m_Machine.State == TrialState.Faulted) return;
            if (m_Machine.State == TrialState.Paused && reason != FaultReason.LoggingFailure) return;
            m_PauseReason = reason;
            m_ResumeRequested = false;
            m_Machine.Fault(reason, MonotonicClock.Now, Time.frameCount, detail);
            SetAllCues(false);
        }

        public void Resume() { if (Paused) m_ResumeRequested = true; }

        /// <summary>Experimenter skip: closes the current attempt as OperatorSkip (not repeated).</summary>
        public void Skip()
        {
            if (m_Machine == null || !Running) return;
            if (Paused) { m_ResumeRequested = true; m_SkipAfterResume = true; return; }
            m_Machine.Fault(FaultReason.OperatorSkip, MonotonicClock.Now, Time.frameCount, "experimenter");
        }

        // ---------------------------------------------------------------- block

        public IEnumerator RunBlock(ScheduledBlock block, Func<int, int, string> progress)
        {
            Running = true;
            m_BlockRecords.Clear();
            bool pickPlace = block.trials.Count > 0 && block.trials[0].task == StudyTask.PickPlace;
            if (pickPlace) SetFarCasting(false);
            var queue = new List<PlannedTrial>(block.trials);
            var repeats = new Dictionary<string, int>();
            int planned = block.trials.Count;

            for (int i = 0; i < queue.Count && !Faulted; i++)
            {
                var trial = queue[i];
                BlockTrialIndex = i;
                BlockTrialCount = queue.Count;
                m_Progress = progress(Math.Min(i + 1, planned), planned) + (i >= planned ? "  (repeat)" : "");
                TrialRecord finished = null;

                while (finished == null && !Faulted)
                {
                    yield return Prepare(trial);
                    if (Faulted) break;
                    m_Machine.Begin(trial, $"{trial.plannedTrialId}-a{trial.attemptNumber}", MonotonicClock.Now, Time.frameCount);
                    if (m_SkipAfterResume)
                    {
                        m_SkipAfterResume = false;
                        m_Machine.Fault(FaultReason.OperatorSkip, MonotonicClock.Now, Time.frameCount, "experimenter");
                    }
                    Snapshot(m_Machine.Current);
                    m_AwaitStartSince = MonotonicClock.Now;
                    m_IdlePromptShown = false;

                    while (m_Machine.State != TrialState.Finished && m_Machine.State != TrialState.Paused && m_Machine.State != TrialState.Faulted)
                        yield return null;
                    if (Faulted) break;

                    var rec = m_Machine.Current;
                    bool closed = rec.outcome != TrialOutcome.None;
                    if (m_Machine.State == TrialState.Paused) yield return WaitForResume();
                    if (closed) finished = rec;
                }
                if (finished == null) break;

                yield return ShowFeedback(finished, block.training);

                if (m_Cfg.repeatInvalidTrials && OutcomeInfo.IsInvalid(finished.outcome) &&
                    finished.outcome != TrialOutcome.OperatorSkip && finished.outcome != TrialOutcome.OperatorAbort)
                {
                    repeats.TryGetValue(trial.plannedTrialId, out int used);
                    if (used < m_Cfg.maxRepeatsPerPlannedTrial)
                    {
                        repeats[trial.plannedTrialId] = used + 1;
                        queue.Add(trial.CloneAsRepeat(finished.attemptId, trial.attemptNumber + 1));
                        m_Logger.LogSessionEvent("RepeatQueued", $"{trial.plannedTrialId} after {finished.outcome}");
                    }
                }

                float t0 = Time.unscaledTime;
                while (Time.unscaledTime - t0 < m_Cfg.interTrialInterval_s && !Faulted) yield return null;
            }
            SetAllCues(false);
            if (pickPlace)
            {
                yield return WaitForSafeDeselection();
                yield return ResetMugs(true);
                SetFarCasting(true);
            }
            Running = false;
            m_Layout.HandRest.Show(HandRestMarker.Visual.Waiting);
        }

        IEnumerator Prepare(PlannedTrial t)
        {
            SetAllCues(false);
            if (t.task == StudyTask.PickPlace)
            {
                yield return WaitForSafeDeselection();
                yield return ResetMugs(true);
            }
            else m_Layout.ResetMugsToSlots();
            // Never start a trial while a hand is still touching a mug.
            float waited = 0f;
            while (m_Contacts.AnyContact() && waited < 10f)
            {
                m_Panel.Show(TitleFor(t), "Please move your hand away from the mugs.", m_Progress);
                waited += Time.unscaledDeltaTime;
                yield return null;
            }
            m_Layout.HandRest.ClearHistory();
            m_Logger.LogSessionEvent("Prepared", $"{t.plannedTrialId} target={t.targetOwner} slot={t.targetSlotName}");
        }

        /// <summary>Reset only after the participant has let go; cancel through XRI as a last resort.</summary>
        IEnumerator WaitForSafeDeselection()
        {
            float waited = 0f;
            while (AnyMugSelected() && waited < 4f)
            {
                m_Panel.Show(m_Machine?.Current?.planned?.training == true ? "Practice" : "Study",
                    "Please put the mug down on the table.", m_Progress);
                waited += Time.unscaledDeltaTime;
                yield return null;
            }
            if (AnyMugSelected()) { m_Logger.LogSessionEvent("ForcedDeselect", "mug still held after 4 s"); CancelAllSelections(); }
        }

        /// <summary>Safe reset of all mugs to their slots (cancel selection, freeze, zero velocities, move, restore).</summary>
        IEnumerator ResetMugs(bool safe)
        {
            if (!safe) { m_Layout.ResetMugsToSlots(); yield break; }
            var routines = new List<Coroutine>();
            foreach (var m in m_Layout.Mugs)
                if (m) routines.Add(StartCoroutine(m.SafeReset(m_InteractionManager, m_Layout.SlotPoseOf(m))));
            foreach (var r in routines) yield return r;
        }

        IEnumerator WaitForResume()
        {
            while (true)
            {
                if (Faulted) yield break;
                bool auto = m_PauseReason.HasValue && OutcomeInfo.AutoResumes(m_PauseReason.Value);
                bool trackingOk = m_Effectors.HeadTracked && m_Effectors.ResponseEffector(m_Cfg.responseHand) != null;
                bool focusOk = Application.isFocused || IgnoreFocus;
                if (SuppressPausePanel) { }
                else if (m_PauseReason == FaultReason.TrackingLoss && !trackingOk)
                    m_Panel.Show("Paused", "We lost track of your hand.\nKeep both hands in view of the headset.", m_Progress);
                else if (m_PauseReason == FaultReason.ResearcherPause)
                    m_Panel.Show("Paused", "Please wait. The researcher will continue the study.", m_Progress);
                if ((auto && trackingOk && focusOk) || (m_ResumeRequested && focusOk)) break;
                yield return null;
            }
            m_ResumeRequested = false;
            m_Logger.LogSessionEvent("Resumed", PauseReason);
            m_PauseReason = null;
            m_Machine.ClearPause(MonotonicClock.Now, Time.frameCount);
        }

        IEnumerator ShowFeedback(TrialRecord r, bool training)
        {
            var target = m_Layout.MugFor(r.planned.targetOwner);
            StudyMug touched = null;
            if (!string.IsNullOrEmpty(r.contactOwner) && Enum.TryParse(r.contactOwner, out Owner o)) touched = m_Layout.MugFor(o);
            string title = TitleFor(r.planned);
            float hold = training ? m_Cfg.trainingFeedback_s : m_Cfg.neutralFeedback_s;
            switch (r.outcome)
            {
                case TrialOutcome.Success when training:
                    m_Feedback.Correct();
                    if (target) target.Flash(new Color(0.3f, 0.9f, 0.4f), hold);
                    m_Panel.Show(title, "Correct!", m_Progress);
                    break;
                case TrialOutcome.WrongTarget when training:
                    m_Feedback.Incorrect();
                    if (touched) touched.Flash(new Color(0.95f, 0.3f, 0.3f), hold);
                    if (target) target.Flash(m_Cfg.cueColor, hold);
                    m_Panel.Show(title, r.Task == StudyTask.PickPlace
                        ? $"That was the {touched?.Label} mug.\nPick up the mug that lights up."
                        : $"That was the {touched?.Label} mug.\nTouch the mug that lights up.", m_Progress);
                    break;
                case TrialOutcome.ReleaseOutsidePad:
                case TrialOutcome.PlacementLeftPad:
                    m_Feedback.Incorrect();
                    m_Panel.Show(title, "Put the mug down inside the white square, standing upright.", m_Progress);
                    hold = Mathf.Max(hold, 1.6f);
                    break;
                case TrialOutcome.PlacementUnstableTimeout:
                case TrialOutcome.OutOfBoundsDrop:
                    m_Feedback.Incorrect();
                    m_Panel.Show(title, "Set the mug down gently and let go once it stands still on the square.", m_Progress);
                    hold = Mathf.Max(hold, 1.6f);
                    break;
                case TrialOutcome.TimeoutBeforeGrab:
                case TrialOutcome.TimeoutBeforePlacement:
                    m_Panel.Show(title, "Time is up. When a mug lights up, pick it up and put it on the square as quickly as you can.", m_Progress);
                    hold = Mathf.Max(hold, 1.6f);
                    break;
                case TrialOutcome.NonDesignatedSelection:
                    m_Feedback.Incorrect();
                    m_Panel.Show(title, $"Please use your {m_Cfg.HandName} hand.", m_Progress);
                    hold = Mathf.Max(hold, 1.6f);
                    break;
                case TrialOutcome.Success:
                case TrialOutcome.WrongTarget:
                    m_Feedback.Neutral();
                    m_Panel.Show(title, "Next", m_Progress);
                    break;
                case TrialOutcome.PrematureDeparture:
                case TrialOutcome.PrematureContact:
                    m_Feedback.Incorrect();
                    m_Panel.Show(title, "Too early. Keep your hand on the oval until a mug lights up.", m_Progress);
                    hold = Mathf.Max(hold, 1.6f);
                    break;
                case TrialOutcome.TimeoutBeforeMovement:
                case TrialOutcome.TimeoutBeforeContact:
                    m_Panel.Show(title, "Time is up. When a mug lights up, touch it as quickly as you can.", m_Progress);
                    hold = Mathf.Max(hold, 1.6f);
                    break;
                case TrialOutcome.TrackingLoss:
                case TrialOutcome.FocusLoss:
                case TrialOutcome.ResearcherPause:
                    m_Panel.Show(title, "This trial will be repeated later.", m_Progress);
                    hold = 1.2f;
                    break;
                default:
                    hold = 0.3f;
                    break;
            }
            float t0 = Time.unscaledTime;
            while (Time.unscaledTime - t0 < hold) yield return null;
        }

        static string TitleFor(PlannedTrial t) => t.training ? "Practice" : "Study";

        void Snapshot(TrialRecord r)
        {
            var table = m_Layout.Table.TablePose;
            r.tableOrigin = table.position;
            r.tableYaw_deg = table.rotation.eulerAngles.y;
            r.handRest = m_Layout.HandRest.transform.position;
            if (m_Layout.Pad) r.padCentre = m_Layout.Pad.transform.position;
            var slots = m_Layout.SlotPositions();
            if (slots.Count == 3) { r.mugSlot0 = slots[0]; r.mugSlot1 = slots[1]; r.mugSlot2 = slots[2]; }
        }

        // ---------------------------------------------------------------- frame loop

        void Update()
        {
            if (!Running || m_Machine?.Current == null) return;
            double now = MonotonicClock.Now;
            var e = m_Effectors.ResponseEffector(m_Cfg.responseHand);
            float dist = float.PositiveInfinity, height = float.PositiveInfinity;
            if (e != null)
            {
                m_Layout.HandRest.Measure(e.restPoint, out dist, out height);
                m_Layout.HandRest.Sample(now, e.restPoint);
            }
            var input = new FrameInput
            {
                time = now, frame = Time.frameCount, frameDt = Time.unscaledDeltaTime,
                restDistance_m = dist, restHeight_m = height, handSpeed_mps = m_Layout.HandRest.Speed,
                handTracked = e != null, headTracked = m_Effectors.HeadTracked, restEffector = e?.id ?? ""
            };
            var planned = m_Machine.Current.planned;
            if (planned.task == StudyTask.PickPlace && m_Layout.Pad)
                input.target = m_Layout.Pad.Evaluate(m_Cfg, m_Layout.MugFor(planned.targetOwner), m_Layout.Table.TablePose);
            m_Machine.Tick(input);
            UpdatePrompt(now);
        }

        void UpdatePrompt(double now)
        {
            var t = m_Machine.Current.planned;
            string hand = m_Cfg.HandName;
            switch (m_Machine.State)
            {
                case TrialState.AwaitStart:
                    bool onRest = m_Machine.HandOnRest;
                    m_Layout.HandRest.Show(onRest ? HandRestMarker.Visual.Ready : HandRestMarker.Visual.Idle);
                    if (onRest) { m_AwaitStartSince = now; m_IdlePromptShown = false; }
                    string body = onRest ? "Hold still..." : $"Place your {hand} hand on the blue oval.";
                    if (!onRest && now - m_AwaitStartSince > m_Cfg.idlePrompt_s)
                    {
                        if (!m_IdlePromptShown) { m_IdlePromptShown = true; m_Logger.LogSessionEvent("IdlePrompt"); }
                        body = $"Place your {hand} hand flat on the blue oval in front of you to continue.";
                    }
                    m_Panel.Show(TitleFor(t), body, m_Progress);
                    break;
                case TrialState.Foreperiod:
                    m_Layout.HandRest.Show(HandRestMarker.Visual.Ready);
                    m_Panel.Show(TitleFor(t), "Wait for a mug to light up.", m_Progress);
                    break;
                case TrialState.AwaitMovement:
                case TrialState.AwaitContact:
                case TrialState.AwaitGrab:
                    m_Layout.HandRest.Show(HandRestMarker.Visual.Idle);
                    m_Panel.Show(TitleFor(t), !t.training ? "" : t.task == StudyTask.PickPlace
                        ? "Pick up the glowing mug and put it on the white square!" : "Touch the glowing mug!", m_Progress);
                    break;
                case TrialState.AwaitPlacement:
                    m_Panel.Show(TitleFor(t), t.training ? "Put it on the white square and let go." : "", m_Progress);
                    break;
                case TrialState.ConfirmingPlacement:
                    m_Panel.Show(TitleFor(t), "", m_Progress);
                    break;
            }
        }

        // ---------------------------------------------------------------- ITrialSink

        public void Emit(StudyEvent e) => m_Logger.LogEvent(e);

        public void CueChanged(Owner target, bool on, double time, int frame)
        {
            var mug = m_Layout.MugFor(target);
            if (mug) mug.SetCue(on, m_Cfg.cueColor, m_Cfg.cueIntensity);
        }

        public void TrialFinished(TrialRecord record)
        {
            m_BlockRecords.Add(record);
            // Tracking loss is detected inside the state machine; remember it so the pause resumes automatically.
            if (record.outcome == TrialOutcome.TrackingLoss && !m_PauseReason.HasValue) m_PauseReason = FaultReason.TrackingLoss;
            // The outcome is fixed before this commit, so the flush never falls inside a timed response.
            if (!m_Logger.CommitTrial(record)) RequestPause(FaultReason.LoggingFailure, m_Logger.LastError);
            TrialCommitted?.Invoke(record);
        }

        void SetAllCues(bool on)
        {
            if (!m_Layout) return;
            foreach (var m in m_Layout.Mugs) if (m) m.SetCue(on, m_Cfg ? m_Cfg.cueColor : Color.yellow, m_Cfg ? m_Cfg.cueIntensity : 1f);
        }

        void OnApplicationFocus(bool focus)
        {
            if (!focus && !IgnoreFocus && Running) RequestPause(FaultReason.FocusLoss, "application focus lost");
        }

        void OnApplicationPause(bool paused)
        {
            if (paused && !IgnoreFocus && Running) RequestPause(FaultReason.FocusLoss, "application paused");
        }
    }
}
