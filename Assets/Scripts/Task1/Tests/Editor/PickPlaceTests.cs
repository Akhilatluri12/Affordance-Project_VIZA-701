using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace XRStudy.Task1.Tests
{
    /// <summary>Pick & place (spec sections 7, 8, 10; AC-05 to AC-08) with injected grab/release/placement events.</summary>
    public class PickPlaceTests
    {
        class Sink : ITrialSink
        {
            public readonly List<StudyEvent> events = new List<StudyEvent>();
            public readonly List<TrialRecord> finished = new List<TrialRecord>();
            public void Emit(StudyEvent e) => events.Add(e);
            public void CueChanged(Owner target, bool on, double time, int frame) { }
            public void TrialFinished(TrialRecord r) => finished.Add(r);
        }

        const double Dt = 1.0 / 90.0;
        ProtocolConfig m_Cfg;
        Sink m_Sink;
        TrialStateMachine m_M;
        PlannedTrial m_Trial;
        double m_T;
        int m_Frame;
        long m_Id, m_Key;
        float m_Dist;
        PlacementSample m_Place;
        int m_Deselects;

        [SetUp]
        public void SetUp()
        {
            m_Cfg = TestFixtures.Config();
            m_Cfg.foreperiodMin_s = 1f; m_Cfg.foreperiodMax_s = 1f;
            m_Sink = new Sink();
            m_M = new TrialStateMachine(m_Cfg, m_Sink, new StudyRng(1), () => ++m_Id);
            m_M.DeselectRequested += _ => m_Deselects++;
            m_Trial = ScheduleGenerator.Generate(m_Cfg, 5, "S", "P4", ConditionOrderMode.Auto, MugOrderMode.Auto, 12, StudyTask.PickPlace).blocks[0].trials[0];
            m_T = 100; m_Frame = 1000; m_Dist = 0.01f; m_Place = default; m_Deselects = 0;
            m_M.Begin(m_Trial, "a1", m_T, m_Frame);
        }

        [TearDown] public void TearDown() => TestFixtures.Destroy(m_Cfg);

        void Step(int n = 1)
        {
            for (int i = 0; i < n; i++)
            {
                m_T += Dt; m_Frame++;
                m_M.Tick(new FrameInput { time = m_T, frame = m_Frame, frameDt = (float)Dt, restDistance_m = m_Dist, restHeight_m = 0.03f,
                    handTracked = true, headTracked = true, restEffector = "RightHand", target = m_Place });
            }
        }
        void StepFor(double s) => Step((int)System.Math.Ceiling(s / Dt));

        void ToGrabStage()
        {
            StepFor(m_Cfg.restHold_s + 2 * Dt);
            StepFor(1.0 + 2 * Dt);
            Assert.AreEqual(TrialState.AwaitMovement, m_M.State);
            m_Dist = 0.2f;
            StepFor(m_Cfg.onsetPersistence_s + 2 * Dt);
            Assert.AreEqual(TrialState.AwaitGrab, m_M.State, "pick & place waits for a grab, not a touch");
        }

        Owner Other => m_Trial.targetOwner == Owner.Owned ? Owner.Neutral : Owner.Owned;
        void Grab(Owner o, bool designated = true, long? key = null) => m_M.OnSelectEntered(o, o.ToString(), designated, "Right Hand/Near-Far", key ?? ++m_Key, m_T, m_Frame);
        void Release(Owner o, bool canceled = false) => m_M.OnSelectExited(o, o.ToString(), true, "Right Hand/Near-Far", canceled, ++m_Key, m_T, m_Frame);
        static PlacementSample OnPadStill => new PlacementSample { inPad = true, upright = true };

        [Test] // AC-05 / AC-07: correct transitions and durations; release and confirmation stay distinct
        public void SuccessfulPickAndPlace()
        {
            ToGrabStage();
            double cue = m_M.Current.cueTime.Value;
            Step(20);
            Grab(m_Trial.targetOwner);
            double grab = m_T;
            Assert.AreEqual(TrialState.AwaitPlacement, m_M.State);
            Assert.IsFalse(m_M.CueVisible, "cue ends at the grab");
            m_Place = new PlacementSample { selected = true, speed_mps = 0.5f };
            Step(30);
            m_Place = OnPadStill;
            Release(m_Trial.targetOwner);
            double release = m_T;
            Step();
            Assert.AreEqual(TrialState.ConfirmingPlacement, m_M.State);
            StepFor(m_Cfg.placementHold_s + Dt);
            var r = m_Sink.finished.Single();
            Assert.AreEqual(TrialOutcome.Success, r.outcome);
            Assert.IsTrue(r.Valid && r.Correct);
            Assert.AreEqual(grab - cue, r.ResponseTime.Value, 1e-9, "cue-to-grab");
            Assert.AreEqual(grab - r.movementOnsetTime.Value, r.MovementTime.Value, 1e-9, "reach-to-grasp");
            Assert.AreEqual(release - grab, r.PlacementDuration.Value, 1e-9);
            Assert.Greater(r.confirmationTime.Value, r.releaseTime.Value);
            Assert.GreaterOrEqual(r.ConfirmationLatency.Value, m_Cfg.placementHold_s - 1e-9);
        }

        [Test] // Touching is not a response in pick & place
        public void TouchDoesNotEndPickPlaceTrial()
        {
            ToGrabStage();
            m_M.OnContact(m_Trial.targetOwner, "X", "RightHand", ++m_Key, m_T, m_Frame);
            Assert.AreEqual(TrialState.AwaitGrab, m_M.State);
            Assert.AreEqual(0, m_Sink.finished.Count);
        }

        [Test] // AC-06: wrong grab
        public void WrongGrabEndsAttempt()
        {
            ToGrabStage();
            Grab(Other);
            var r = m_Sink.finished.Single();
            Assert.AreEqual(TrialOutcome.WrongTarget, r.outcome);
            Assert.IsFalse(r.Correct);
            Assert.IsTrue(r.ResponseTime.HasValue);
        }

        [Test]
        public void OtherHandIsRejected()
        {
            ToGrabStage();
            Grab(m_Trial.targetOwner, designated: false);
            Assert.AreEqual(TrialOutcome.NonDesignatedSelection, m_Sink.finished.Single().outcome);
        }

        [Test] // Premature grab and grab before the trial started
        public void EarlyGrabs()
        {
            Step(2);
            Grab(m_Trial.targetOwner);
            Assert.AreEqual(1, m_Deselects, "grab during readiness is cancelled through the interaction system");
            Assert.AreEqual(0, m_Sink.finished.Count);
            StepFor(m_Cfg.restHold_s + 2 * Dt);
            Assert.AreEqual(TrialState.Foreperiod, m_M.State);
            Grab(m_Trial.targetOwner);
            Assert.AreEqual(TrialOutcome.PrematureContact, m_Sink.finished.Single().outcome);
        }

        [Test] // AC-06: outside-pad release, drop, unstable, timeouts
        public void PlacementFailuresAreDistinct()
        {
            ToGrabStage(); Grab(m_Trial.targetOwner);
            m_Place = new PlacementSample { inPad = false, upright = true };
            Release(m_Trial.targetOwner); Step();
            Assert.AreEqual(TrialOutcome.ReleaseOutsidePad, m_Sink.finished.Single().outcome);
            Assert.IsFalse(m_Sink.finished[0].PlacementDuration.HasValue);
            Assert.IsTrue(m_Sink.finished[0].Correct, "the right mug was picked");
            Assert.IsFalse(m_Sink.finished[0].Valid);

            TearDown(); SetUp(); ToGrabStage(); Grab(m_Trial.targetOwner);
            m_Place = OnPadStill; Release(m_Trial.targetOwner); Step();
            m_Place = new PlacementSample { inPad = false };
            Step();
            Assert.AreEqual(TrialOutcome.PlacementLeftPad, m_Sink.finished.Single().outcome);

            TearDown(); SetUp(); ToGrabStage(); Grab(m_Trial.targetOwner);
            m_Place = OnPadStill; Release(m_Trial.targetOwner); Step();
            m_Place = new PlacementSample { inPad = true, upright = true, speed_mps = 0.2f };
            StepFor(m_Cfg.placementSettleTimeout_s + Dt);
            Assert.AreEqual(TrialOutcome.PlacementUnstableTimeout, m_Sink.finished.Single().outcome);

            TearDown(); SetUp(); ToGrabStage(); Grab(m_Trial.targetOwner);
            m_Place = new PlacementSample { outOfBounds = true, selected = true };
            Step();
            Assert.AreEqual(TrialOutcome.OutOfBoundsDrop, m_Sink.finished.Single().outcome);

            TearDown(); SetUp(); ToGrabStage();
            StepFor(m_Cfg.cueToContactTimeout_s);
            Assert.AreEqual(TrialOutcome.TimeoutBeforeGrab, m_Sink.finished.Single().outcome);

            TearDown(); SetUp(); ToGrabStage(); Grab(m_Trial.targetOwner);
            m_Place = new PlacementSample { selected = true };
            StepFor(m_Cfg.grabToPlacementTimeout_s + Dt);
            Assert.AreEqual(TrialOutcome.TimeoutBeforePlacement, m_Sink.finished.Single().outcome);
        }

        [Test] // AC-07: an in-pad release that wobbles must still hold still for the full dwell
        public void StabilityHoldResetsOnMotion()
        {
            ToGrabStage(); Grab(m_Trial.targetOwner);
            m_Place = OnPadStill; Release(m_Trial.targetOwner); Step();
            StepFor(m_Cfg.placementHold_s * 0.6);
            m_Place = new PlacementSample { inPad = true, upright = true, angularSpeed_degps = 40f };
            Step(3);
            m_Place = OnPadStill;
            StepFor(m_Cfg.placementHold_s * 0.6);
            Assert.AreEqual(TrialState.ConfirmingPlacement, m_M.State);
            StepFor(m_Cfg.placementHold_s * 0.5);
            Assert.AreEqual(TrialOutcome.Success, m_Sink.finished.Single().outcome);
        }

        [Test] // Regrab during confirmation returns to placement; cancelled selections are not releases
        public void RegrabAndCancel()
        {
            ToGrabStage(); Grab(m_Trial.targetOwner);
            Release(m_Trial.targetOwner, canceled: true);
            Assert.AreEqual(TrialState.AwaitPlacement, m_M.State);
            Assert.IsFalse(m_M.Current.releaseTime.HasValue);
            m_Place = OnPadStill; Release(m_Trial.targetOwner); Step();
            Assert.AreEqual(TrialState.ConfirmingPlacement, m_M.State);
            Grab(m_Trial.targetOwner);
            Assert.AreEqual(TrialState.AwaitPlacement, m_M.State);
            Assert.IsTrue((m_M.Current.flags & TrialFlags.Regrabbed) != 0);
        }

        [Test] // AC-05: duplicate listener events ignored and flagged
        public void DuplicateGrabIgnored()
        {
            ToGrabStage();
            Grab(m_Trial.targetOwner, key: 99);
            Grab(m_Trial.targetOwner, key: 99);
            Assert.AreEqual(1, m_M.Current.duplicateEvents);
            Assert.AreEqual(TrialState.AwaitPlacement, m_M.State);
        }

        [Test] // AC-08: tracking loss while holding invalidates the attempt
        public void TrackingLossWhileHolding()
        {
            ToGrabStage(); Grab(m_Trial.targetOwner);
            Assert.IsTrue(m_M.Fault(FaultReason.TrackingLoss, m_T, m_Frame));
            var r = m_Sink.finished.Single();
            Assert.AreEqual(TrialOutcome.TrackingLoss, r.outcome);
            Assert.IsTrue(r.contactTime.HasValue, "grab time kept");
            Assert.IsTrue(OutcomeInfo.IsInvalid(r.outcome));
        }

        [Test]
        public void PlacementEvaluatorGeometry()
        {
            var pad = new Pose(new Vector3(0.3f, 0.74f, 0.4f), Quaternion.Euler(0, 20, 0));
            var table = new Pose(new Vector3(0, 0.74f, 0), Quaternion.Euler(0, 20, 0));
            var size = new Vector2(0.14f, 0.14f);
            var inside = PlacementEvaluator.Evaluate(m_Cfg, pad, size, pad.position + pad.rotation * new Vector3(0.05f, 0f, -0.05f), Vector3.up, 0, 0, false, table);
            Assert.IsTrue(inside.inPad && inside.upright && inside.IsStable(m_Cfg));
            var outside = PlacementEvaluator.Evaluate(m_Cfg, pad, size, pad.position + pad.rotation * new Vector3(0.08f, 0f, 0f), Vector3.up, 0, 0, false, table);
            Assert.IsFalse(outside.inPad);
            var tipped = PlacementEvaluator.Evaluate(m_Cfg, pad, size, pad.position, Quaternion.Euler(30, 0, 0) * Vector3.up, 0, 0, false, table);
            Assert.IsFalse(tipped.upright);
            var dropped = PlacementEvaluator.Evaluate(m_Cfg, pad, size, new Vector3(0.3f, 0.5f, 0.4f), Vector3.up, 0, 0, false, table);
            Assert.IsTrue(dropped.outOfBounds);
            var held = PlacementEvaluator.Evaluate(m_Cfg, pad, size, pad.position, Vector3.up, 0, 0, true, table);
            Assert.IsFalse(held.IsStable(m_Cfg), "a held mug is never a confirmed placement");
        }

        [Test]
        public void ScheduleCarriesTask()
        {
            var s = ScheduleGenerator.Generate(m_Cfg, 9, "S", "P2", ConditionOrderMode.Auto, MugOrderMode.Auto, 12, StudyTask.PickPlace);
            Assert.IsTrue(s.blocks.SelectMany(b => b.trials).Concat(s.training.trials).All(t => t.task == StudyTask.PickPlace));
            StringAssert.Contains("PickPlace", s.ToCsv());
        }
    }
}
