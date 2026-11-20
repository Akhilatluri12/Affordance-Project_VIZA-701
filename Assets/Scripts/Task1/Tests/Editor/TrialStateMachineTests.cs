using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace XRStudy.Task1.Tests
{
    /// <summary>Injected rest / cue / movement / contact events for the cued mug-touch task.</summary>
    public class TrialStateMachineTests
    {
        class Sink : ITrialSink
        {
            public readonly List<StudyEvent> events = new List<StudyEvent>();
            public readonly List<TrialRecord> finished = new List<TrialRecord>();
            public readonly List<(bool on, double t)> cues = new List<(bool, double)>();
            public void Emit(StudyEvent e) => events.Add(e);
            public void CueChanged(Owner target, bool on, double time, int frame) => cues.Add((on, time));
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
        float m_Dist, m_Height;
        bool m_Tracked;

        [SetUp]
        public void SetUp()
        {
            m_Cfg = TestFixtures.Config();
            m_Cfg.foreperiodMin_s = 1.0f; m_Cfg.foreperiodMax_s = 1.0f;
            m_Sink = new Sink();
            m_M = new TrialStateMachine(m_Cfg, m_Sink, new StudyRng(1), () => ++m_Id);
            m_Trial = ScheduleGenerator.Generate(m_Cfg, 5, "S", "P4", ConditionOrderMode.Auto, MugOrderMode.Auto, 12).blocks[0].trials[0];
            m_T = 100.0; m_Frame = 1000; m_Dist = 0.01f; m_Height = 0.03f; m_Tracked = true;
            m_M.Begin(m_Trial, "att1", m_T, m_Frame);
        }

        [TearDown] public void TearDown() => TestFixtures.Destroy(m_Cfg);

        void Step(int frames = 1)
        {
            for (int i = 0; i < frames; i++)
            {
                m_T += Dt; m_Frame++;
                m_M.Tick(new FrameInput { time = m_T, frame = m_Frame, frameDt = (float)Dt, restDistance_m = m_Dist, restHeight_m = m_Height,
                    handSpeed_mps = 0f, handTracked = m_Tracked, headTracked = true, restEffector = "RightHand" });
            }
        }

        void StepFor(double s) => Step((int)System.Math.Ceiling(s / Dt));

        void ReachForeperiod()
        {
            StepFor(m_Cfg.restHold_s + 2 * Dt);
            Assert.AreEqual(TrialState.Foreperiod, m_M.State);
        }

        void ReachCue()
        {
            ReachForeperiod();
            StepFor(1.0 + 2 * Dt);
            Assert.AreEqual(TrialState.AwaitMovement, m_M.State);
            Assert.IsTrue(m_M.CueVisible);
        }

        void LeaveRest()
        {
            m_Dist = 0.20f;
            StepFor(m_Cfg.onsetPersistence_s + 2 * Dt);
            Assert.AreEqual(TrialState.AwaitContact, m_M.State);
        }

        Owner Other => m_Trial.targetOwner == Owner.Owned ? Owner.Neutral : Owner.Owned;
        void Touch(Owner o, long? key = null) => m_M.OnContact(o, o.ToString(), "RightHand", key ?? ++m_Key, m_T, m_Frame);

        [Test]
        public void HandMustRestBeforeTheCue()
        {
            m_Dist = 0.3f;
            StepFor(3.0);
            Assert.AreEqual(TrialState.AwaitStart, m_M.State);
            Assert.IsFalse(m_M.HandOnRest);
            m_Dist = 0.01f; Step();
            Assert.IsTrue(m_M.HandOnRest, "marker shows ready as soon as the hand is on it");
        }

        [Test] // Debounce: leaving briefly restarts the hold
        public void RestHoldIsDebounced()
        {
            StepFor(0.3);
            m_Dist = 0.2f; Step();
            m_Dist = 0.01f;
            StepFor(0.3);
            Assert.AreEqual(TrialState.AwaitStart, m_M.State);
            StepFor(0.25);
            Assert.AreEqual(TrialState.Foreperiod, m_M.State);
        }

        [Test]
        public void CorrectTouchRecordsResponseTime()
        {
            ReachCue();
            double cue = m_M.Current.cueTime.Value;
            Step(10);
            LeaveRest();
            Step(20);
            Touch(m_Trial.targetOwner);
            var r = m_Sink.finished.Single();
            Assert.AreEqual(TrialOutcome.Success, r.outcome);
            Assert.IsTrue(r.Correct && r.Valid);
            Assert.AreEqual(m_T - cue, r.ResponseTime.Value, 1e-9);
            Assert.AreEqual(r.movementOnsetTime.Value - cue, r.InitiationTime.Value, 1e-9);
            Assert.AreEqual(m_Trial.targetOwner.ToString(), r.contactMugId);
            Assert.AreEqual("RightHand", r.contactEffector);
            Assert.IsFalse(m_M.CueVisible, "cue turns off at the response");
        }

        [Test] // Wrong mug is a valid response with correct = 0 and a response time
        public void WrongMugIsRecordedAsIncorrect()
        {
            ReachCue(); LeaveRest();
            Touch(Other);
            var r = m_Sink.finished.Single();
            Assert.AreEqual(TrialOutcome.WrongTarget, r.outcome);
            Assert.IsFalse(r.Correct);
            Assert.IsTrue(r.Valid);
            Assert.IsTrue(r.ResponseTime.HasValue);
            Assert.AreEqual(Other.ToString(), r.contactOwner);
        }

        [Test] // Fast reach: contact before the onset persistence elapsed uses the pending crossing time
        public void ContactBeforeOnsetConfirmationUsesCrossingTime()
        {
            ReachCue();
            m_Dist = 0.2f; Step(1);
            double crossing = m_T;
            Step(1);
            Touch(m_Trial.targetOwner);
            var r = m_Sink.finished.Single();
            Assert.AreEqual(crossing, r.movementOnsetTime.Value, 1e-9);
            Assert.AreEqual(0, (int)(r.flags & TrialFlags.OnsetDetectionFailure));
        }

        [Test] // Contact while the hand never left the rest zone: flagged, onset left blank
        public void ContactWithoutOnsetIsFlagged()
        {
            ReachCue();
            Touch(m_Trial.targetOwner);
            var r = m_Sink.finished.Single();
            Assert.IsTrue((r.flags & TrialFlags.OnsetDetectionFailure) != 0);
            Assert.IsFalse(r.movementOnsetTime.HasValue);
            Assert.IsTrue(r.ResponseTime.HasValue);
        }

        [Test]
        public void LeavingBeforeCueIsPremature()
        {
            ReachForeperiod();
            m_Dist = 0.2f; Step();
            var r = m_Sink.finished.Single();
            Assert.AreEqual(TrialOutcome.PrematureDeparture, r.outcome);
            Assert.IsFalse(r.cueTime.HasValue, "timer never continues silently");
            Assert.IsFalse(r.Valid);
        }

        [Test]
        public void TouchBeforeCueIsPremature()
        {
            ReachForeperiod();
            Touch(m_Trial.targetOwner);
            Assert.AreEqual(TrialOutcome.PrematureContact, m_Sink.finished.Single().outcome);
        }

        [Test]
        public void TimeoutsAreStageSpecific()
        {
            ReachCue();
            StepFor(m_Cfg.cueToContactTimeout_s + Dt);
            Assert.AreEqual(TrialOutcome.TimeoutBeforeMovement, m_Sink.finished.Single().outcome);
            TearDown(); SetUp();
            ReachCue(); LeaveRest();
            StepFor(m_Cfg.cueToContactTimeout_s);
            var r = m_Sink.finished.Single();
            Assert.AreEqual(TrialOutcome.TimeoutBeforeContact, r.outcome);
            Assert.IsTrue(r.movementOnsetTime.HasValue, "earlier timestamps kept");
            Assert.IsFalse(r.ResponseTime.HasValue);
        }

        [Test]
        public void DuplicateContactEventsAreIgnored()
        {
            ReachCue(); LeaveRest();
            Touch(m_Trial.targetOwner, 7);
            Touch(m_Trial.targetOwner, 7);
            Assert.AreEqual(1, m_Sink.finished.Count);
            Assert.AreEqual(1, m_Sink.events.Count(e => e.type == "DuplicateIgnored"));
        }

        [Test] // Hand-tracking loss mid-trial invalidates the attempt (repeated later by TrialManager)
        public void TrackingLossInvalidatesAndPauses()
        {
            ReachCue(); LeaveRest();
            m_Tracked = false; Step(2); m_Tracked = true; Step();
            Assert.AreEqual(TrialState.AwaitContact, m_M.State, "short glitch tolerated");
            m_Tracked = false;
            StepFor(m_Cfg.trackingLossGrace_s + 2 * Dt);
            var r = m_Sink.finished.Single();
            Assert.AreEqual(TrialOutcome.TrackingLoss, r.outcome);
            Assert.IsTrue(OutcomeInfo.IsInvalid(r.outcome));
            Assert.AreEqual(TrialState.Paused, m_M.State);
            m_M.ClearPause(m_T, m_Frame);
            Assert.AreEqual(TrialState.Idle, m_M.State);
        }

        [Test]
        public void PauseBeforeTimedPhaseDoesNotConsumeAttempt()
        {
            Step(3);
            Assert.IsFalse(m_M.Fault(FaultReason.ResearcherPause, m_T, m_Frame));
            Assert.AreEqual(0, m_Sink.finished.Count);
        }

        [Test]
        public void SkipClosesAttemptAndContinues()
        {
            Step(3);
            Assert.IsTrue(m_M.Fault(FaultReason.OperatorSkip, m_T, m_Frame));
            Assert.AreEqual(TrialOutcome.OperatorSkip, m_Sink.finished.Single().outcome);
            Assert.AreEqual(TrialState.Finished, m_M.State);
        }

        [Test]
        public void ContactAfterOutcomeIsIgnored()
        {
            ReachCue(); LeaveRest();
            Touch(m_Trial.targetOwner);
            Touch(Other);
            Assert.AreEqual(1, m_Sink.finished.Count);
            Assert.AreEqual(TrialOutcome.Success, m_Sink.finished[0].outcome);
        }

        [Test]
        public void FixedCueDurationTurnsOff()
        {
            m_Cfg.cueDuration_s = 0.3f;
            ReachCue();
            StepFor(0.3 + Dt);
            Assert.IsFalse(m_M.CueVisible);
        }
    }
}
