using System;
using UnityEngine;

namespace XRStudy.Task1
{
    /// <summary>One timestamped application event. Every listener write goes through a unique id.</summary>
    public struct StudyEvent
    {
        public long eventId;
        public double time_s;
        public int frame;
        public string attemptId;
        public string plannedTrialId;
        public string blockId;
        public string type;
        public string mugId;
        public string interactor;
        public string state;
        public string detail;
    }

    /// <summary>Per-attempt record. Missing values stay null (written as blank, never 0).</summary>
    [Serializable]
    public class TrialRecord
    {
        public string attemptId;
        public int attemptNumber;
        public string replacesAttemptId;
        public PlannedTrial planned;

        // Timestamps on the shared monotonic clock (seconds) plus frame indices.
        public double? attemptStart;
        public double? readyTime;
        public double? foreperiodPlanned;
        public double? cueTime; public int? cueFrame;
        public double? cueOffTime; public int? cueOffFrame;
        public double? movementOnsetTime; public int? movementOnsetFrame;
        public double? movementConfirmTime;
        /// <summary>Touch task: first contact. Pick & place: first grab (select) time.</summary>
        public double? contactTime; public int? contactFrame;
        public double? firstReleaseTime;
        public double? releaseTime; public int? releaseFrame;
        public double? confirmationTime; public int? confirmationFrame;
        public double? endTime; public int? endFrame;

        public string contactMugId = "";
        public string contactOwner = "";
        public string contactEffector = "";
        public string restEffector = "";

        public TrialOutcome outcome = TrialOutcome.None;
        public TrialFlags flags = TrialFlags.None;
        public string outcomeStage = "";
        public string outcomeDetail = "";
        public int duplicateEvents;

        // Diagnostics
        public int framesInAttempt;
        public int slowFrames;
        public float maxFrameDt_s;
        public int trackingDropouts;

        // Layout snapshot (world space, metres)
        public Vector3 tableOrigin;
        public float tableYaw_deg;
        public Vector3 handRest;
        public Vector3 padCentre;
        public Vector3 mugSlot0, mugSlot1, mugSlot2;

        public StudyTask Task => planned != null ? planned.task : StudyTask.Touch;

        /// <summary>
        /// Response time: Touch = first contact − cue onset; Pick & place = first grab − cue onset (cue-to-grab).
        /// Blank when there was no touch/grab after the cue.
        /// </summary>
        public double? ResponseTime => Diff(contactTime, cueTime);
        public double? InitiationTime => Diff(movementOnsetTime, cueTime);
        /// <summary>Touch: contact − onset. Pick & place: reach-to-grasp duration (grab − onset).</summary>
        public double? MovementTime => Diff(contactTime, movementOnsetTime);
        /// <summary>Pick & place: successful release − grab (the stability dwell is not included).</summary>
        public double? PlacementDuration => outcome == TrialOutcome.Success ? Diff(releaseTime, contactTime) : null;
        public double? ConfirmationLatency => outcome == TrialOutcome.Success ? Diff(confirmationTime, releaseTime) : null;
        /// <summary>The first mug touched/grabbed was the cued one (blank in CSVs when nothing was touched).</summary>
        public bool Correct => contactOwner.Length > 0 && planned != null && contactOwner == planned.targetOwner.ToString() &&
                               outcome != TrialOutcome.PrematureContact;
        public bool Valid => OutcomeInfo.IsResponse(outcome) && (flags & TrialFlags.EventOrderAnomaly) == 0;

        static double? Diff(double? a, double? b) => a.HasValue && b.HasValue ? a.Value - b.Value : (double?)null;

        /// <summary>Flags (does not correct) out-of-order timestamps.</summary>
        public void CheckOrder()
        {
            double? prev = null;
            foreach (var t in new[] { cueTime, movementOnsetTime, contactTime, releaseTime, confirmationTime })
            {
                if (!t.HasValue) continue;
                if (prev.HasValue && t.Value < prev.Value) { flags |= TrialFlags.EventOrderAnomaly; return; }
                prev = t;
            }
        }
    }
}
