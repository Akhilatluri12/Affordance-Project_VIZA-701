using System.Collections.Generic;
using UnityEngine;

namespace XRStudy.Task1
{
    /// <summary>
    /// Debug mode: simulates a participant and experimenter so the whole flow runs without a headset.
    /// It fills in setup, calibrates the table in front of the camera, answers the ownership quiz, and drives a
    /// virtual right hand that rests on the marker and reaches for the lit mug (sometimes the wrong one).
    /// Off by default; enable "Simulate On Start" (Editor or a dev build) to use it. Data is logged normally.
    /// </summary>
    public class TrialSimulator : MonoBehaviour
    {
        [SerializeField] bool m_SimulateOnStart;
        [SerializeField] StudyFlowController m_Flow;
        [SerializeField] TrialManager m_Trials;
        [SerializeField] LayoutController m_Layout;
        [SerializeField] EffectorTracker m_Effectors;
        [SerializeField] StudyPanel m_Panel;
        [SerializeField] Transform m_Head;

        [Header("Simulated participant")]
        [Range(0f, 1f)] public float accuracy = 0.9f;
        [Tooltip("Session plan the simulated experimenter selects on the setup screen.")]
        public TaskSelection tasks = TaskSelection.Both;
        public ConditionSelection conditions = ConditionSelection.Both;
        public Vector2 reactionTime_s = new Vector2(0.25f, 0.40f);
        public float reachDuration_s = 0.35f;
        public float uiInterval_s = 0.35f;

        enum HandPhase { Rest, Waiting, Reaching, Transporting, Returning }
        StudyMug m_Held;
        Vector3 m_HeldOffset;
        readonly Effector m_Hand = new Effector { id = "SimRightHand", hand = StudyHand.Right, tracked = true };
        readonly List<Effector> m_List = new List<Effector>();
        HandPhase m_Phase;
        float m_PhaseStart, m_ReactionDelay;
        Vector3 m_From, m_To, m_Pos;
        float m_LastUi;
        bool m_Active;

        public bool Active => m_Active;

        public void Configure(StudyFlowController flow, TrialManager trials, LayoutController layout, EffectorTracker effectors, StudyPanel panel, Transform head)
        {
            m_Flow = flow; m_Trials = trials; m_Layout = layout; m_Effectors = effectors; m_Panel = panel; m_Head = head;
        }

        void Start() { if (m_SimulateOnStart) Activate(); }

        public void Activate()
        {
            m_Active = true;
            m_Effectors.Override = () => { m_List.Clear(); m_List.Add(m_Hand); return m_List; };
            m_Effectors.HeadTrackedOverride = () => true;
            m_Flow.EditorIgnoreFocus = true;
            m_Pos = RestPoint();
            Debug.Log("[Task1] Debug simulation active.");
        }

        Vector3 RestPoint() => m_Layout.HandRest ? m_Layout.HandRest.transform.position + Vector3.up * 0.03f : Vector3.zero;

        void Update()
        {
            if (!m_Active) return;
            DriveUi();
            DriveHand();
            m_Hand.restPoint = m_Pos;
            m_Hand.contactPoints.Clear();
            m_Hand.contactPoints.Add(m_Pos);
        }

        void DriveUi()
        {
            if (Time.unscaledTime - m_LastUi < uiInterval_s) return;
            m_LastUi = Time.unscaledTime;
            if (m_Flow.AwaitingCalibrationCapture)
            {
                Vector3 fwd = m_Head ? m_Head.forward : Vector3.forward; fwd.y = 0f; fwd.Normalize();
                Vector3 head = m_Head ? m_Head.position : new Vector3(0f, 1.2f, 0f);
                m_Flow.DebugCalibrate(new Vector3(head.x, 0.74f, head.z) + fwd * 0.3f, Quaternion.LookRotation(fwd).eulerAngles.y);
                return;
            }
            if (m_Flow.CurrentQuizAnswer.HasValue)
            {
                m_Panel.Click(m_Flow.CurrentQuizAnswer.Value.ToString(), true);
                return;
            }
            if (m_Flow.Phase == StudyPhase.Setup && m_Panel.TitleText.StartsWith("Setup") && m_Panel.BodyText.Contains("P_"))
            {
                m_Flow.DebugSetParticipant(Random.Range(1, 999).ToString());
                m_Flow.DebugSetPlan(tasks, conditions);
            }
            foreach (var id in new[] { "start", "next", "accept", "use-saved" })
                if (m_Panel.Click(id, true)) return;
        }

        void DriveHand()
        {
            float t = Time.unscaledTime;
            var state = m_Trials.State;
            switch (m_Phase)
            {
                case HandPhase.Rest:
                    m_Pos = RestPoint();
                    if (m_Trials.Running && state == TrialState.AwaitMovement)
                    {
                        m_Phase = HandPhase.Waiting;
                        m_PhaseStart = t;
                        m_ReactionDelay = Random.Range(reactionTime_s.x, reactionTime_s.y);
                    }
                    break;
                case HandPhase.Waiting:
                    if (t - m_PhaseStart >= m_ReactionDelay)
                    {
                        var target = m_Trials.CurrentPlanned != null ? m_Trials.CurrentPlanned.targetOwner : Owner.Neutral;
                        if (Random.value > accuracy) target = (Owner)(((int)target + 1 + Random.Range(0, 2)) % 3);
                        var mug = m_Layout.MugFor(target);
                        m_From = m_Pos;
                        m_To = mug ? mug.transform.position + Vector3.up * (mug.BodyHeight * 0.5f) : m_Pos;
                        m_Phase = HandPhase.Reaching;
                        m_PhaseStart = t;
                    }
                    break;
                case HandPhase.Reaching:
                    m_Pos = Vector3.Lerp(m_From, m_To, Mathf.SmoothStep(0f, 1f, (t - m_PhaseStart) / reachDuration_s));
                    if (t - m_PhaseStart >= reachDuration_s + 0.15f)
                    {
                        var planned = m_Trials.CurrentPlanned;
                        if (planned != null && planned.task == StudyTask.PickPlace && m_Layout.Pad)
                        {
                            // Grab the mug under the hand and carry it to the pad.
                            StudyMug nearest = null; float best = float.PositiveInfinity;
                            foreach (var m in m_Layout.Mugs) { float d = m.DistanceTo(m_Pos); if (d < best) { best = d; nearest = m; } }
                            m_Held = nearest;
                            if (m_Held)
                            {
                                m_Held.Body.isKinematic = true;
                                m_HeldOffset = m_Held.transform.position - m_Pos;
                                m_Trials.DebugSelect(m_Held, true);
                            }
                            m_From = m_Pos;
                            m_To = m_Layout.Pad.transform.position - m_HeldOffset + Vector3.up * 0.002f;
                            m_Phase = HandPhase.Transporting;
                        }
                        else m_Phase = HandPhase.Returning;
                        m_PhaseStart = t; m_From = m_Pos;
                    }
                    break;
                case HandPhase.Transporting:
                    m_Pos = Vector3.Lerp(m_From, m_To, Mathf.SmoothStep(0f, 1f, (t - m_PhaseStart) / reachDuration_s));
                    if (m_Held) m_Held.transform.position = m_Pos + m_HeldOffset;
                    bool stillHolding = m_Held && m_Trials.State == TrialState.AwaitPlacement;
                    if (t - m_PhaseStart >= reachDuration_s + 0.1f || (!stillHolding && m_Held && m_Trials.State != TrialState.AwaitPlacement && t - m_PhaseStart > 0.05f))
                    {
                        if (m_Held)
                        {
                            m_Trials.DebugSelect(m_Held, false);
                            m_Held.SetGrabbable(m_Held.Grabbable);   // restore physics: it rests on the table collider
                            m_Held = null;
                        }
                        m_Phase = HandPhase.Returning; m_PhaseStart = t; m_From = m_Pos;
                    }
                    break;
                case HandPhase.Returning:
                    m_Pos = Vector3.Lerp(m_From, RestPoint(), Mathf.SmoothStep(0f, 1f, (t - m_PhaseStart) / reachDuration_s));
                    if (t - m_PhaseStart >= reachDuration_s && state != TrialState.AwaitMovement && state != TrialState.AwaitContact &&
                        state != TrialState.AwaitGrab) m_Phase = HandPhase.Rest;
                    break;
            }
        }
    }
}
