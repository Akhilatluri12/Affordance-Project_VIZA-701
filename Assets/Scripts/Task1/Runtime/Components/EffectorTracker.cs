using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Hands;

namespace XRStudy.Task1
{
    /// <summary>A body part that can touch mugs and rest on the marker: a tracked hand or a controller.</summary>
    public class Effector
    {
        public string id;                 // RightHand | LeftHand | RightController | LeftController | Sim...
        public StudyHand hand;
        public bool tracked;
        public Vector3 restPoint;         // palm (hands) or controller grip point
        public readonly List<Vector3> contactPoints = new List<Vector3>(6);
    }

    /// <summary>
    /// Collects world-space contact points once per rendered frame: fingertips + palm from XR Hands when hands are
    /// tracked (primary), otherwise a point at the controller's front. A debug simulator can override all input.
    /// </summary>
    [DefaultExecutionOrder(50)]
    public class EffectorTracker : MonoBehaviour
    {
        static readonly XRHandJointID[] k_ContactJoints =
        {
            XRHandJointID.IndexTip, XRHandJointID.MiddleTip, XRHandJointID.ThumbTip, XRHandJointID.RingTip,
            XRHandJointID.LittleTip, XRHandJointID.Palm
        };

        [SerializeField] Transform m_TrackingSpace;        // XR Origin's Camera Offset (hand joints are relative to it)
        [SerializeField] Transform m_LeftController;
        [SerializeField] Transform m_RightController;
        [SerializeField] InputActionReference m_LeftControllerTracked;
        [SerializeField] InputActionReference m_RightControllerTracked;
        [SerializeField] InputActionReference m_HeadTracked;
        [Tooltip("Contact point in controller space (in front of the grip).")]
        [SerializeField] Vector3 m_ControllerTipOffset = new Vector3(0f, -0.01f, 0.05f);

        readonly Effector[] m_Hands = { new Effector { id = "LeftHand", hand = StudyHand.Left }, new Effector { id = "RightHand", hand = StudyHand.Right } };
        readonly Effector[] m_Controllers = { new Effector { id = "LeftController", hand = StudyHand.Left }, new Effector { id = "RightController", hand = StudyHand.Right } };
        readonly List<Effector> m_Active = new List<Effector>(4);
        readonly List<XRHandSubsystem> m_HandSubsystems = new List<XRHandSubsystem>();
        int m_LastFrame = -1;

        /// <summary>Debug/simulation override: when set, replaces all tracked input.</summary>
        public Func<List<Effector>> Override { get; set; }
        public Func<bool> HeadTrackedOverride { get; set; }

        public IReadOnlyList<Effector> Active { get { Refresh(); return m_Active; } }

        public void Configure(Transform trackingSpace, Transform left, Transform right, InputActionReference leftTracked,
            InputActionReference rightTracked, InputActionReference headTracked)
        {
            m_TrackingSpace = trackingSpace; m_LeftController = left; m_RightController = right;
            m_LeftControllerTracked = leftTracked; m_RightControllerTracked = rightTracked; m_HeadTracked = headTracked;
        }

        public bool HeadTracked => HeadTrackedOverride?.Invoke() ?? Pressed(m_HeadTracked, true);

        static bool Pressed(InputActionReference r, bool fallback)
        {
            if (r == null || r.action == null) return fallback;
            if (!r.action.enabled) r.action.Enable();
            return r.action.IsPressed();
        }

        public void Refresh()
        {
            if (m_LastFrame == Time.frameCount) return;
            m_LastFrame = Time.frameCount;
            m_Active.Clear();
            if (Override != null)
            {
                var sim = Override();
                if (sim != null) m_Active.AddRange(sim);
                return;
            }

            XRHandSubsystem hands = null;
            SubsystemManager.GetSubsystems(m_HandSubsystems);
            foreach (var s in m_HandSubsystems) if (s.running) { hands = s; break; }

            for (int i = 0; i < 2; i++)
            {
                var e = m_Hands[i];
                e.tracked = false;
                e.contactPoints.Clear();
                if (hands == null) continue;
                var hand = i == 0 ? hands.leftHand : hands.rightHand;
                if (!hand.isTracked) continue;
                foreach (var id in k_ContactJoints)
                    if (hand.GetJoint(id).TryGetPose(out var pose)) e.contactPoints.Add(ToWorld(pose.position));
                if (hand.GetJoint(XRHandJointID.Palm).TryGetPose(out var palm)) e.restPoint = ToWorld(palm.position);
                e.tracked = e.contactPoints.Count > 0;
                if (e.tracked) m_Active.Add(e);
            }

            for (int i = 0; i < 2; i++)
            {
                var e = m_Controllers[i];
                var t = i == 0 ? m_LeftController : m_RightController;
                var tracked = i == 0 ? m_LeftControllerTracked : m_RightControllerTracked;
                e.contactPoints.Clear();
                // A hand that is tracked as a hand takes precedence over the (put-down) controller on that side.
                e.tracked = t && Pressed(tracked, false) && !m_Hands[i].tracked;
                if (!e.tracked) continue;
                e.restPoint = t.position;
                e.contactPoints.Add(t.TransformPoint(m_ControllerTipOffset));
                e.contactPoints.Add(t.position);
                m_Active.Add(e);
            }
        }

        Vector3 ToWorld(Vector3 trackingSpacePoint) => m_TrackingSpace ? m_TrackingSpace.TransformPoint(trackingSpacePoint) : trackingSpacePoint;

        /// <summary>Whether that controller is tracked, even while the same-side hand is tracked as a hand.</summary>
        public bool ControllerTracked(StudyHand hand)
        {
            if (Override != null) return true;
            return Pressed(hand == StudyHand.Left ? m_LeftControllerTracked : m_RightControllerTracked, false);
        }

        /// <summary>The response hand's effector (hand preferred, controller as fallback).</summary>
        public Effector ResponseEffector(StudyHand hand)
        {
            Refresh();
            Effector controller = null;
            foreach (var e in m_Active)
            {
                if (e.hand != hand) continue;
                if (!e.id.Contains("Controller")) return e;
                controller = e;
            }
            return controller;
        }
    }
}
