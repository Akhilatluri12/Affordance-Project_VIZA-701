using System;
using System.Collections.Generic;

namespace XRStudy.Task1
{
    /// <summary>Everything the state machine needs from one rendered frame.</summary>
    public struct FrameInput
    {
        public double time;
        public int frame;
        public float frameDt;
        /// <summary>Horizontal distance of the response hand's rest point from the marker centre (∞ if untracked).</summary>
        public float restDistance_m;
        /// <summary>Height of that point above the table surface.</summary>
        public float restHeight_m;
        public float handSpeed_mps;
        public bool handTracked;
        public bool headTracked;
        public string restEffector;
        /// <summary>Pick & place only: the target mug relative to the pad.</summary>
        public PlacementSample target;
    }

    public interface ITrialSink
    {
        void Emit(StudyEvent e);
        void CueChanged(Owner target, bool on, double time, int frame);
        void TrialFinished(TrialRecord record);
    }

    /// <summary>
    /// Sole owner of trial state, cue timing, event acceptance and outcomes for both tasks:
    /// Touch: hand rests on the marker → foreperiod → cue → hand leaves the marker → first mug contact.
    /// PickPlace (spec section 7): … → cue → movement → first designated-hand grab → release on the pad →
    /// containment + stability hold → confirmation.
    /// Pure C#: time and input arrive through <see cref="Tick"/>, <see cref="OnContact"/> and the select events,
    /// so tests drive it with injected events.
    /// </summary>
    public sealed class TrialStateMachine
    {
        readonly ProtocolConfig m_Cfg;
        readonly ITrialSink m_Sink;
        readonly StudyRng m_Rng;
        readonly Func<long> m_NextEventId;

        public TrialState State { get; private set; } = TrialState.Idle;
        public TrialRecord Current { get; private set; }
        public bool CueVisible { get; private set; }
        /// <summary>True while the response hand is on the marker (for the marker's "ready" state).</summary>
        public bool HandOnRest { get; private set; }

        double? m_HoldStart;
        double m_ForeperiodEnd;
        double? m_OnsetCandidate;
        int? m_OnsetCandidateFrame;
        double? m_TrackingLostSince;
        readonly HashSet<long> m_SeenEventKeys = new HashSet<long>();
        bool m_TargetHeld, m_PendingReleaseCheck;
        double? m_StableSince;

        /// <summary>Raised when a selection must be cancelled through the interaction system (never teleport a held mug).</summary>
        public event Action<string> DeselectRequested;

        public TrialStateMachine(ProtocolConfig cfg, ITrialSink sink, StudyRng rng, Func<long> nextEventId)
        {
            m_Cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
            m_Sink = sink ?? throw new ArgumentNullException(nameof(sink));
            m_Rng = rng ?? throw new ArgumentNullException(nameof(rng));
            m_NextEventId = nextEventId ?? throw new ArgumentNullException(nameof(nextEventId));
        }

        public bool InTimedState => State == TrialState.Foreperiod || State == TrialState.AwaitMovement || State == TrialState.AwaitContact ||
                                    State == TrialState.AwaitGrab || State == TrialState.AwaitPlacement || State == TrialState.ConfirmingPlacement;

        StudyTask Task => Current?.planned?.task ?? StudyTask.Touch;

        /// <summary>Prepare: caller has reset mugs to their slots and turned cues off.</summary>
        public void Begin(PlannedTrial trial, string attemptId, double now, int frame)
        {
            Current = new TrialRecord
            {
                attemptId = attemptId,
                attemptNumber = trial.attemptNumber,
                replacesAttemptId = trial.replacesAttemptId,
                planned = trial,
                attemptStart = now,
            };
            m_HoldStart = null; m_OnsetCandidate = null; m_OnsetCandidateFrame = null; m_TrackingLostSince = null;
            m_TargetHeld = false; m_PendingReleaseCheck = false; m_StableSince = null;
            m_SeenEventKeys.Clear();
            SetCue(false, now, frame);
            Enter(TrialState.Prepare, now, frame, "");
            Enter(TrialState.AwaitStart, now, frame, "");
        }

        public void Tick(in FrameInput f)
        {
            if (Current == null || State == TrialState.Idle || State == TrialState.Finished ||
                State == TrialState.Paused || State == TrialState.Faulted)
            { HandOnRest = OnRest(f); return; }

            if (InTimedState)
            {
                Current.framesInAttempt++;
                if (f.frameDt > Current.maxFrameDt_s) Current.maxFrameDt_s = f.frameDt;
                if (m_Cfg.targetRefreshRate_hz > 0f && f.frameDt > m_Cfg.slowFrameFactor / m_Cfg.targetRefreshRate_hz)
                {
                    Current.slowFrames++;
                    Current.flags |= TrialFlags.SlowFrames;
                }
            }

            if (CueVisible && m_Cfg.cueDuration_s > 0f && Current.cueTime.HasValue && f.time - Current.cueTime.Value >= m_Cfg.cueDuration_s)
                SetCue(false, f.time, f.frame);

            bool tracked = f.handTracked && f.headTracked;
            if (!tracked)
            {
                if (!m_TrackingLostSince.HasValue)
                {
                    m_TrackingLostSince = f.time;
                    Current.trackingDropouts++;
                    Emit("TrackingLost", f.time, f.frame, detail: f.handTracked ? "head" : "hand");
                }
                if (InTimedState && f.time - m_TrackingLostSince.Value >= m_Cfg.trackingLossGrace_s)
                {
                    Fault(FaultReason.TrackingLoss, f.time, f.frame, "tracking lost");
                    return;
                }
            }
            else if (m_TrackingLostSince.HasValue)
            {
                Emit("TrackingRestored", f.time, f.frame);
                if (InTimedState) Current.flags |= TrialFlags.TrackingGlitchRecovered;
                m_TrackingLostSince = null;
            }

            HandOnRest = tracked && OnRest(f);
            switch (State)
            {
                case TrialState.AwaitStart: TickAwaitStart(f); break;
                case TrialState.Foreperiod: TickForeperiod(f); break;
                case TrialState.AwaitMovement: TickAwaitMovement(f); break;
                case TrialState.AwaitContact: TickTimeout(f, TrialOutcome.TimeoutBeforeContact); break;
                case TrialState.AwaitGrab: TickTimeout(f, TrialOutcome.TimeoutBeforeGrab); break;
                case TrialState.AwaitPlacement: TickAwaitPlacement(f); break;
                case TrialState.ConfirmingPlacement: TickConfirming(f); break;
            }
        }

        bool OnRest(in FrameInput f) => f.restDistance_m <= m_Cfg.restRadius_m && f.restHeight_m <= m_Cfg.restMaxHeight_m;

        void TickAwaitStart(in FrameInput f)
        {
            if (HandOnRest && f.handSpeed_mps < m_Cfg.stillnessSpeed_mps)
            {
                if (!m_HoldStart.HasValue) m_HoldStart = f.time;
                if (f.time - m_HoldStart.Value >= m_Cfg.restHold_s)
                {
                    Current.readyTime = f.time;
                    Current.restEffector = f.restEffector ?? "";
                    double fp = m_Cfg.foreperiodMin_s + m_Rng.NextDouble() * (m_Cfg.foreperiodMax_s - m_Cfg.foreperiodMin_s);
                    Current.foreperiodPlanned = fp;
                    m_ForeperiodEnd = f.time + fp;
                    Enter(TrialState.Foreperiod, f.time, f.frame, "foreperiod_s=" + Csv.F(fp));
                }
            }
            else m_HoldStart = null;
        }

        void TickForeperiod(in FrameInput f)
        {
            // Leaving the marker (with a small extra margin against jitter) before the cue is premature.
            if (f.restDistance_m > m_Cfg.restRadius_m + m_Cfg.onsetMarginBeyondRest_m * 0.5f || f.restHeight_m > m_Cfg.restMaxHeight_m)
            {
                Finish(TrialOutcome.PrematureDeparture, f.time, f.frame, "Foreperiod");
                return;
            }
            if (f.time >= m_ForeperiodEnd)
            {
                Current.cueTime = f.time;
                Current.cueFrame = f.frame;
                SetCue(true, f.time, f.frame);
                Enter(TrialState.AwaitMovement, f.time, f.frame, "cue");
                TickAwaitMovement(f);
            }
        }

        void TickAwaitMovement(in FrameInput f)
        {
            if (State != TrialState.AwaitMovement) return;
            float threshold = m_Cfg.restRadius_m + m_Cfg.onsetMarginBeyondRest_m;
            if (f.restDistance_m > threshold || f.restHeight_m > m_Cfg.restMaxHeight_m + m_Cfg.onsetMarginBeyondRest_m)
            {
                if (!m_OnsetCandidate.HasValue) { m_OnsetCandidate = f.time; m_OnsetCandidateFrame = f.frame; }
                if (f.time - m_OnsetCandidate.Value >= m_Cfg.onsetPersistence_s)
                {
                    ConfirmOnset(f.time, f.frame);
                    Enter(Task == StudyTask.PickPlace ? TrialState.AwaitGrab : TrialState.AwaitContact, f.time, f.frame, "");
                    return;
                }
            }
            else { m_OnsetCandidate = null; m_OnsetCandidateFrame = null; }
            TickTimeout(f, TrialOutcome.TimeoutBeforeMovement);
        }

        void ConfirmOnset(double t, int frame)
        {
            Current.movementOnsetTime = m_OnsetCandidate;
            Current.movementOnsetFrame = m_OnsetCandidateFrame;
            Current.movementConfirmTime = t;
            Emit("MovementOnset", m_OnsetCandidate.Value, m_OnsetCandidateFrame ?? frame, detail: "confirmedAt_s=" + Csv.T(t));
        }

        void TickTimeout(in FrameInput f, TrialOutcome code)
        {
            if (Current.cueTime.HasValue && f.time - Current.cueTime.Value >= m_Cfg.cueToContactTimeout_s)
                Finish(code, f.time, f.frame, State.ToString());
        }

        // ------------------------------------------------------------ pick & place

        void TickAwaitPlacement(in FrameInput f)
        {
            if (f.target.outOfBounds) { Finish(TrialOutcome.OutOfBoundsDrop, f.time, f.frame, "AwaitPlacement"); return; }
            if (m_PendingReleaseCheck)
            {
                m_PendingReleaseCheck = false;
                if (!f.target.inPad) { Finish(TrialOutcome.ReleaseOutsidePad, f.time, f.frame, "AwaitPlacement"); return; }
                m_StableSince = null;
                Enter(TrialState.ConfirmingPlacement, f.time, f.frame, "");
                TickConfirming(f);
                return;
            }
            if (Current.contactTime.HasValue && f.time - Current.contactTime.Value >= m_Cfg.grabToPlacementTimeout_s)
                Finish(TrialOutcome.TimeoutBeforePlacement, f.time, f.frame, "AwaitPlacement");
        }

        void TickConfirming(in FrameInput f)
        {
            if (State != TrialState.ConfirmingPlacement) return;
            if (f.target.outOfBounds) { Finish(TrialOutcome.OutOfBoundsDrop, f.time, f.frame, "ConfirmingPlacement"); return; }
            if (!f.target.selected && !f.target.inPad) { Finish(TrialOutcome.PlacementLeftPad, f.time, f.frame, "ConfirmingPlacement"); return; }
            if (f.target.IsStable(m_Cfg))
            {
                if (!m_StableSince.HasValue) m_StableSince = f.time;
                if (f.time - m_StableSince.Value >= m_Cfg.placementHold_s)
                {
                    Current.confirmationTime = f.time;
                    Current.confirmationFrame = f.frame;
                    Emit("PlacementConfirmed", f.time, f.frame, Current.contactMugId);
                    Finish(TrialOutcome.Success, f.time, f.frame, "ConfirmingPlacement");
                    return;
                }
            }
            else m_StableSince = null;
            // A valid in-pad release may settle; it must become stable within the settle timeout.
            if (Current.releaseTime.HasValue && f.time - Current.releaseTime.Value >= m_Cfg.placementSettleTimeout_s)
                Finish(TrialOutcome.PlacementUnstableTimeout, f.time, f.frame, "ConfirmingPlacement");
        }

        /// <summary>XRI select (grab), tagged with interactor identity and a unique event key. Pick & place only.</summary>
        public void OnSelectEntered(Owner mugOwner, string mugLabel, bool designatedHand, string interactor, long eventKey, double t, int frame)
        {
            if (!AcceptKey(eventKey, "SelectEntered", mugLabel, interactor, t, frame)) return;
            Emit("SelectEntered", t, frame, mugLabel, interactor);
            if (Current == null || Task != StudyTask.PickPlace) return;
            switch (State)
            {
                case TrialState.Prepare:
                case TrialState.AwaitStart:
                    DeselectRequested?.Invoke("grab before the trial started");
                    return;
                case TrialState.Foreperiod:
                    RecordContact(mugOwner, mugLabel, interactor, t, frame);
                    Finish(TrialOutcome.PrematureContact, t, frame, "Foreperiod");
                    return;
                case TrialState.AwaitMovement:
                case TrialState.AwaitGrab:
                    if (State == TrialState.AwaitMovement)
                    {
                        if (m_OnsetCandidate.HasValue) ConfirmOnset(t, frame);
                        else { Current.flags |= TrialFlags.OnsetDetectionFailure; Emit("OnsetDetectionFailure", t, frame, mugLabel); }
                    }
                    RecordContact(mugOwner, mugLabel, interactor, t, frame);
                    if (!designatedHand) { Finish(TrialOutcome.NonDesignatedSelection, t, frame, State.ToString()); return; }
                    if (mugOwner != Current.planned.targetOwner) { Finish(TrialOutcome.WrongTarget, t, frame, State.ToString()); return; }
                    m_TargetHeld = true;
                    if (CueVisible) SetCue(false, t, frame);   // cue ends at the grab
                    Enter(TrialState.AwaitPlacement, t, frame, "grab");
                    return;
                case TrialState.AwaitPlacement:
                case TrialState.ConfirmingPlacement:
                    if (mugOwner == Current.planned.targetOwner && designatedHand)
                    {
                        if (m_TargetHeld) { FlagDuplicate("SelectEntered", mugLabel, interactor, t, frame); return; }
                        m_TargetHeld = true;
                        m_PendingReleaseCheck = false;
                        Current.flags |= TrialFlags.Regrabbed;
                        if (State == TrialState.ConfirmingPlacement) Enter(TrialState.AwaitPlacement, t, frame, "regrab");
                    }
                    else Emit("UnexpectedSelection", t, frame, mugLabel, interactor);
                    return;
            }
        }

        /// <summary>XRI release. Cancelled selections (safe resets) are logged but are not releases.</summary>
        public void OnSelectExited(Owner mugOwner, string mugLabel, bool designatedHand, string interactor, bool canceled, long eventKey, double t, int frame)
        {
            if (!AcceptKey(eventKey, "SelectExited", mugLabel, interactor, t, frame)) return;
            Emit(canceled ? "SelectCanceled" : "SelectExited", t, frame, mugLabel, interactor);
            if (Current == null || canceled || Task != StudyTask.PickPlace) return;
            if (State == TrialState.AwaitPlacement && mugOwner == Current.planned.targetOwner)
            {
                if (!m_TargetHeld) { FlagDuplicate("SelectExited", mugLabel, interactor, t, frame); return; }
                m_TargetHeld = false;
                if (!Current.firstReleaseTime.HasValue) Current.firstReleaseTime = t;
                Current.releaseTime = t; Current.releaseFrame = frame;
                m_PendingReleaseCheck = true;   // containment is evaluated on the next frame's pose
            }
        }

        bool AcceptKey(long key, string type, string mug, string interactor, double t, int frame)
        {
            if (m_SeenEventKeys.Add(key)) return true;
            FlagDuplicate(type, mug, interactor, t, frame);
            return false;
        }

        void FlagDuplicate(string type, string mug, string interactor, double t, int frame)
        {
            if (Current != null) { Current.flags |= TrialFlags.DuplicateEventIgnored; Current.duplicateEvents++; }
            Emit("DuplicateIgnored", t, frame, mug, interactor, type);
        }

        // ------------------------------------------------------------ touch

        /// <summary>First contact of an effector with a mug's contact volume (from ContactDetector). Touch task only.</summary>
        public void OnContact(Owner mugOwner, string mugLabel, string effector, long eventKey, double t, int frame)
        {
            if (Task == StudyTask.PickPlace && Current != null)
            {
                // In pick & place, touching is not a response; only grabs count.
                return;
            }
            if (!m_SeenEventKeys.Add(eventKey))
            {
                if (Current != null) { Current.flags |= TrialFlags.DuplicateEventIgnored; Current.duplicateEvents++; }
                Emit("DuplicateIgnored", t, frame, mugLabel, effector, "Contact");
                return;
            }
            Emit("Contact", t, frame, mugLabel, effector);
            if (Current == null) return;

            switch (State)
            {
                case TrialState.Foreperiod:
                    RecordContact(mugOwner, mugLabel, effector, t, frame);
                    Finish(TrialOutcome.PrematureContact, t, frame, "Foreperiod");
                    return;
                case TrialState.AwaitMovement:
                case TrialState.AwaitContact:
                    if (State == TrialState.AwaitMovement)
                    {
                        // Contact proves movement: use the pending crossing if there is one, never invent a time.
                        if (m_OnsetCandidate.HasValue) ConfirmOnset(t, frame);
                        else { Current.flags |= TrialFlags.OnsetDetectionFailure; Emit("OnsetDetectionFailure", t, frame, mugLabel); }
                    }
                    RecordContact(mugOwner, mugLabel, effector, t, frame);
                    Finish(mugOwner == Current.planned.targetOwner ? TrialOutcome.Success : TrialOutcome.WrongTarget, t, frame, State.ToString());
                    return;
            }
        }

        void RecordContact(Owner owner, string label, string effector, double t, int frame)
        {
            Current.contactTime = t; Current.contactFrame = frame;
            Current.contactMugId = label ?? "";
            Current.contactOwner = owner.ToString();
            Current.contactEffector = effector ?? "";
        }

        /// <summary>
        /// Pause / tracking / focus / skip / abort / logging fault. A timed attempt is closed as invalid; an
        /// attempt still waiting for the hand is only consumed by skip/abort. Returns true when an attempt closed.
        /// </summary>
        public bool Fault(FaultReason reason, double t, int frame, string detail = "")
        {
            bool closes = InTimedState || ((reason == FaultReason.OperatorSkip || reason == FaultReason.OperatorAbort) &&
                                           (State == TrialState.AwaitStart || State == TrialState.Prepare));
            bool closed = false;
            if (Current != null && closes && Current.outcome == TrialOutcome.None)
            {
                Finish(OutcomeInfo.FromFault(reason), t, frame, State.ToString(), detail);
                closed = true;
            }
            SetCue(false, t, frame);
            var next = reason == FaultReason.LoggingFailure ? TrialState.Faulted
                     : reason == FaultReason.OperatorSkip ? TrialState.Finished
                     : TrialState.Paused;
            Enter(next, t, frame, reason + (string.IsNullOrEmpty(detail) ? "" : ":" + detail));
            return closed;
        }

        /// <summary>Leave Paused (after acknowledgement or automatic recovery) so the next Begin starts fresh.</summary>
        public void ClearPause(double now, int frame)
        {
            if (State == TrialState.Paused) Enter(TrialState.Idle, now, frame, "resumed");
        }

        void Finish(TrialOutcome outcome, double t, int frame, string stage, string detail = "")
        {
            if (Current == null || Current.outcome != TrialOutcome.None) return;
            if (CueVisible) SetCue(false, t, frame);
            Current.outcome = outcome;
            Current.outcomeStage = stage;
            Current.outcomeDetail = detail;
            Current.endTime = t; Current.endFrame = frame;
            Current.CheckOrder();
            Emit("Outcome", t, frame, Current.contactMugId, Current.contactEffector, outcome + "@" + stage);
            Enter(TrialState.Finished, t, frame, outcome.ToString());
            m_Sink.TrialFinished(Current);
        }

        void SetCue(bool on, double t, int frame)
        {
            if (CueVisible == on) return;
            CueVisible = on;
            var target = Current?.planned?.targetOwner ?? Owner.Owned;
            if (Current != null && !on && Current.cueTime.HasValue && !Current.cueOffTime.HasValue)
            {
                Current.cueOffTime = t; Current.cueOffFrame = frame;
            }
            Emit(on ? "CueOn" : "CueOff", t, frame, target.ToString());
            m_Sink.CueChanged(target, on, t, frame);
        }

        void Enter(TrialState s, double t, int frame, string detail)
        {
            State = s;
            Emit("State:" + s, t, frame, detail: detail);
        }

        void Emit(string type, double t, int frame, string mugId = "", string interactor = "", string detail = "")
        {
            m_Sink.Emit(new StudyEvent
            {
                eventId = m_NextEventId(), time_s = t, frame = frame,
                attemptId = Current?.attemptId ?? "", plannedTrialId = Current?.planned?.plannedTrialId ?? "",
                blockId = Current?.planned?.blockId ?? "", type = type, mugId = mugId ?? "", interactor = interactor ?? "",
                state = State.ToString(), detail = detail ?? ""
            });
        }
    }
}
