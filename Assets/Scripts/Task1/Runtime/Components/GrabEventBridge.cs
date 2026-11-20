using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace XRStudy.Task1
{
    /// <summary>
    /// Forwards XRI grab/release of this mug to the TrialManager, tagged with interactor identity and whether it
    /// was the designated hand. Each event gets a key from (type, interactor, mug, frame); a repeated callback
    /// produces the same key, which the state machine ignores and flags.
    /// </summary>
    [RequireComponent(typeof(StudyMug), typeof(XRGrabInteractable))]
    public class GrabEventBridge : MonoBehaviour
    {
        StudyMug m_Mug;
        XRGrabInteractable m_Grab;

        public TrialManager Receiver { get; set; }
        public StudyHand DesignatedHand { get; set; } = StudyHand.Right;

        void Awake() { m_Mug = GetComponent<StudyMug>(); m_Grab = GetComponent<XRGrabInteractable>(); }

        void OnEnable()
        {
            if (!m_Grab) m_Grab = GetComponent<XRGrabInteractable>();
            m_Grab.selectEntered.AddListener(OnEntered);
            m_Grab.selectExited.AddListener(OnExited);
        }

        void OnDisable()
        {
            m_Grab.selectEntered.RemoveListener(OnEntered);
            m_Grab.selectExited.RemoveListener(OnExited);
        }

        void OnEntered(SelectEnterEventArgs a) =>
            Receiver?.HandleSelectEntered(m_Mug, IsDesignated(a.interactorObject), Name(a.interactorObject), Key(1, a.interactorObject));

        void OnExited(SelectExitEventArgs a) =>
            Receiver?.HandleSelectExited(m_Mug, IsDesignated(a.interactorObject), Name(a.interactorObject), a.isCanceled, Key(2, a.interactorObject));

        bool IsDesignated(IXRSelectInteractor i) => DesignatedHandSelectFilter.Matches(i, DesignatedHand);

        static string Name(IXRSelectInteractor i) =>
            i is Component c ? (c.transform.parent ? c.transform.parent.name + "/" : "") + c.name : "unknown";

        long Key(int type, IXRSelectInteractor i)
        {
            unchecked
            {
                ulong h = 1469598103934665603UL;
                void Mix(long v) { h ^= (ulong)v; h *= 1099511628211UL; }
                Mix(type); Mix(i is Object o ? o.GetInstanceID() : 0); Mix(GetInstanceID()); Mix(Time.frameCount);
                return (long)(h & 0x7FFFFFFFFFFFFFFFUL);
            }
        }
    }
}
