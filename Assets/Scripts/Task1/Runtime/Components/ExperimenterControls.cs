using UnityEngine;
using UnityEngine.InputSystem;

namespace XRStudy.Task1
{
    /// <summary>
    /// Hidden experimenter panel (pause, resume, skip trial, recalibrate, abort). Opened with the left
    /// controller's menu button, the left-hand system "menu" pinch gesture (wired by the scene builder), or
    /// F2 / M on a keyboard in the Editor. Hidden by default so participants don't see it.
    /// </summary>
    public class ExperimenterControls : MonoBehaviour
    {
        [SerializeField] StudyPanel m_Panel;
        [SerializeField] StudyFlowController m_Flow;

        InputAction m_Toggle;
        bool m_ConfirmAbort;
        float m_LastRefresh;

        public bool Open { get; private set; }

        public void Configure(StudyPanel panel, StudyFlowController flow) { m_Panel = panel; m_Flow = flow; }

        void OnEnable()
        {
            m_Toggle = new InputAction("ExperimenterPanel", InputActionType.Button);
            m_Toggle.AddBinding("<XRController>{LeftHand}/menuButton");
            m_Toggle.AddBinding("<Keyboard>/f2");
            m_Toggle.AddBinding("<Keyboard>/m");
            m_Toggle.performed += _ => Toggle();
            m_Toggle.Enable();
        }

        void OnDisable() { m_Toggle?.Dispose(); m_Toggle = null; }

        void Start() { if (m_Panel) m_Panel.Hide(); }

        public void Toggle()
        {
            Open = !Open;
            m_ConfirmAbort = false;
            if (Open) Refresh(); else m_Panel.Hide();
        }

        void Update()
        {
            if (Open && Time.unscaledTime - m_LastRefresh > 0.25f) Refresh();
        }

        void Refresh()
        {
            m_LastRefresh = Time.unscaledTime;
            if (!m_Panel || !m_Flow) return;
            if (m_ConfirmAbort)
            {
                m_Panel.Show("Experimenter", "Abort this session? Data recorded so far is kept and marked Aborted.", m_Flow.StatusLine,
                    new PanelButton("Yes, abort", () => { m_Flow.Abort(); Toggle(); }, ButtonStyle.Danger, "abort-yes"),
                    new PanelButton("Cancel", () => { m_ConfirmAbort = false; Refresh(); }, ButtonStyle.Normal, "abort-no"));
                return;
            }
            bool paused = m_Flow.TrialsPaused;
            m_Panel.Show("Experimenter", m_Flow.StatusDetail, m_Flow.StatusLine,
                paused ? new PanelButton("Resume", () => { m_Flow.ResumeTrials(); Refresh(); }, ButtonStyle.Primary, "resume")
                       : new PanelButton("Pause", () => { m_Flow.PauseTrials(); Refresh(); }, ButtonStyle.Normal, "pause"),
                new PanelButton("Skip trial", () => { m_Flow.SkipTrial(); Refresh(); }, ButtonStyle.Normal, "skip"),
                new PanelButton("Recalibrate", () => { Toggle(); m_Flow.Recalibrate(); }, ButtonStyle.Normal, "recalibrate"),
                new PanelButton("Abort", () => { m_ConfirmAbort = true; Refresh(); }, ButtonStyle.Danger, "abort"),
                new PanelButton("Close", Toggle, ButtonStyle.Normal, "close"));
        }
    }
}
