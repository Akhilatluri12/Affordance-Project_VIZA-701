using System;

namespace XRStudy.Task1
{
    /// <summary>Mug ownership (independent variable). Ownership never restricts interaction.</summary>
    public enum Owner { Owned = 0, Neutral = 1, AgentOwned = 2 }

    public enum StudyHand { Right = 0, Left = 1 }

    public enum PresenceCondition { AgentAbsent = 0, AgentPresent = 1 }

    /// <summary>Which agent condition runs first. Auto = counterbalanced from the participant ID.</summary>
    public enum ConditionOrderMode { Auto = 0, AbsentFirst = 1, PresentFirst = 2 }

    /// <summary>Left-to-right mug order. Auto = counterbalanced from the participant ID; Fixed = canonical P1, N, NPC.</summary>
    public enum MugOrderMode { Auto = 0, Fixed = 1 }

    public enum MugShape { Diamond = 0, Triangle = 1, Square = 2 }

    /// <summary>Which task(s) a session runs. Both = each task gets its own practice and blocks (order counterbalanced).</summary>
    public enum TaskSelection { Touch = 0, PickPlace = 1, Both = 2 }

    /// <summary>Which agent conditions a session runs. Both = counterbalanced within-subject design (Layouts 1 and 2).</summary>
    public enum ConditionSelection { Both = 0, AgentAbsentOnly = 1, AgentPresentOnly = 2 }

    /// <summary>Which agent body is shown in Layout 2. Virtual humans come from the team's virtual-humans project.</summary>
    public enum AgentAvatar { Placeholder = 0, VirtualHumanMale = 1, VirtualHumanFemale = 2 }

    /// <summary>Touch = cued touch of the target mug. PickPlace = cued reach-grab-place onto the pad (spec Task 1).</summary>
    public enum StudyTask { Touch = 0, PickPlace = 1 }

    /// <summary>Trial state machine states.</summary>
    public enum TrialState
    {
        Idle = 0,
        Prepare,
        AwaitStart,      // hand must rest on the marker
        Foreperiod,      // random wait before the cue
        AwaitMovement,   // cue shown; waiting for the hand to leave the rest marker
        AwaitContact,    // Touch: movement started; waiting for the first mug contact
        AwaitGrab,       // PickPlace: movement started; waiting for the first designated-hand grab
        AwaitPlacement,  // PickPlace: target held; waiting for release on the pad
        ConfirmingPlacement, // PickPlace: released in the pad; waiting for the stability hold
        Finished,
        Paused,
        Faulted
    }

    /// <summary>Distinct outcome codes for every attempt.</summary>
    public enum TrialOutcome
    {
        None = 0,
        Success,                 // Touch: cued mug touched first. PickPlace: cued mug grabbed and placed stably on the pad
        WrongTarget,             // first touch / grab was another mug (valid response, correct = 0)
        PrematureDeparture,      // hand left the rest marker before the cue
        PrematureContact,        // a mug was touched or grabbed before the cue
        TimeoutBeforeMovement,
        TimeoutBeforeContact,
        TimeoutBeforeGrab,
        TimeoutBeforePlacement,
        ReleaseOutsidePad,       // released the target outside the pad
        PlacementLeftPad,        // released in the pad but it slid/tipped out during the stability check
        PlacementUnstableTimeout,
        OutOfBoundsDrop,         // the mug fell off the table / left the workspace
        NonDesignatedSelection,  // grabbed with the other hand (should be prevented by the select filter)
        TrackingLoss,
        FocusLoss,
        ResearcherPause,
        OperatorSkip,
        OperatorAbort,
        LoggingFailure
    }

    public enum FaultReason
    {
        TrackingLoss,
        FocusLoss,
        ResearcherPause,
        OperatorSkip,
        OperatorAbort,
        LoggingFailure
    }

    [Flags]
    public enum TrialFlags
    {
        None = 0,
        OnsetDetectionFailure = 1 << 0,
        DuplicateEventIgnored = 1 << 1,
        EventOrderAnomaly = 1 << 2,
        Regrabbed = 1 << 3,
        SlowFrames = 1 << 5,
        TrackingGlitchRecovered = 1 << 6
    }

    public static class OutcomeInfo
    {
        /// <summary>The participant made a response (right or wrong mug).</summary>
        public static bool IsResponse(TrialOutcome o) => o == TrialOutcome.Success || o == TrialOutcome.WrongTarget;

        public static bool IsTechnical(TrialOutcome o) =>
            o == TrialOutcome.TrackingLoss || o == TrialOutcome.FocusLoss || o == TrialOutcome.ResearcherPause ||
            o == TrialOutcome.OperatorSkip || o == TrialOutcome.OperatorAbort || o == TrialOutcome.LoggingFailure;

        /// <summary>Attempts that produced no usable response and may be repeated at block end.</summary>
        public static bool IsInvalid(TrialOutcome o) => !IsResponse(o) && o != TrialOutcome.None;

        public static TrialOutcome FromFault(FaultReason r) => r switch
        {
            FaultReason.TrackingLoss => TrialOutcome.TrackingLoss,
            FaultReason.FocusLoss => TrialOutcome.FocusLoss,
            FaultReason.ResearcherPause => TrialOutcome.ResearcherPause,
            FaultReason.OperatorSkip => TrialOutcome.OperatorSkip,
            FaultReason.OperatorAbort => TrialOutcome.OperatorAbort,
            _ => TrialOutcome.LoggingFailure
        };

        /// <summary>Faults after which the session resumes automatically once the cause clears.</summary>
        public static bool AutoResumes(FaultReason r) => r == FaultReason.TrackingLoss || r == FaultReason.FocusLoss || r == FaultReason.OperatorSkip;
    }
}
