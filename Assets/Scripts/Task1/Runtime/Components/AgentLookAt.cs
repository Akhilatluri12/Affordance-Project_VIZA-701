using UnityEngine;

namespace XRStudy.Task1
{
    /// <summary>
    /// Humanoid head/eye gaze toward the participant's head via Animator IK (needs IK Pass on the base layer).
    /// The weights are constant and the target is always the participant, so behaviour is identical across
    /// trials and conditions and never reacts to trial events.
    /// </summary>
    [RequireComponent(typeof(Animator))]
    public class AgentLookAt : MonoBehaviour
    {
        public Transform target;
        [Range(0f, 1f)] public float weight = 1f;
        [Range(0f, 1f)] public float bodyWeight = 0.05f;
        [Range(0f, 1f)] public float headWeight = 0.6f;
        [Range(0f, 1f)] public float eyesWeight = 0f;   // eyes are not mapped on the CC rigs (see VirtualHumanSetup)
        [Range(0f, 1f)] public float clampWeight = 0.6f;

        Animator m_Animator;

        void Awake() => m_Animator = GetComponent<Animator>();

        void OnAnimatorIK(int layerIndex)
        {
            var t = target ? target : (Camera.main ? Camera.main.transform : null);
            if (!t) { m_Animator.SetLookAtWeight(0f); return; }
            m_Animator.SetLookAtWeight(weight, bodyWeight, headWeight, eyesWeight, clampWeight);
            m_Animator.SetLookAtPosition(t.position);
        }
    }
}
