using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace XRStudy.Task1
{
    /// <summary>Visual identity of one ownership category (labels/colours/shapes from the study slides).</summary>
    [Serializable]
    public class OwnershipVisual
    {
        public Owner owner;
        public string label = "P1";
        public string colorName = "blue";
        public Color color = Color.blue;
        public MugShape shape = MugShape.Diamond;
    }

    /// <summary>
    /// Versioned protocol parameters (timing, tolerances, counts, visuals). Layout geometry lives in
    /// <see cref="StudyLayout"/> assets. A frozen copy + SHA-256 hash is saved with every session.
    /// </summary>
    [CreateAssetMenu(menuName = "XR Study/Task 1 Protocol Config", fileName = "Task1ProtocolConfig")]
    public class ProtocolConfig : ScriptableObject
    {
        [Header("Identity")]
        public string protocolId = "Task1-MugTouch-MR";
        public string protocolVersion = "0.2.0-pilot";
        public string schemaVersion = "2.0";
        public string taskId = "Task1";

        [Header("Layouts (one per agent condition; Layout 2 = Layout 1 + agent)")]
        public StudyLayout agentAbsentLayout;
        public StudyLayout agentPresentLayout;

        [Header("Response hand and hand rest")]
        [Tooltip("Hand that rests on the marker and is expected to respond. Contacts by either hand are logged.")]
        public StudyHand responseHand = StudyHand.Right;
        [Tooltip("Horizontal tolerance around the hand-rest marker centre.")]
        public float restRadius_m = 0.07f;
        [Tooltip("Max height of the resting hand point above the table.")]
        public float restMaxHeight_m = 0.12f;
        [Tooltip("Continuous time on the marker before a trial can start (debounce + readiness).")]
        public float restHold_s = 0.5f;
        public float stillnessSpeed_mps = 0.15f;
        public float speedWindow_s = 0.1f;

        [Header("Foreperiod and cue")]
        public float foreperiodMin_s = 0.8f;
        public float foreperiodMax_s = 1.5f;
        [Tooltip("0 = cue stays on until the response (touch) / grab (pick & place).")]
        public float cueDuration_s = 0f;
        public Color cueColor = new Color(1f, 0.85f, 0.15f, 1f);
        public float cueIntensity = 2.5f;

        [Header("Movement onset (leaving the rest marker)")]
        public float onsetMarginBeyondRest_m = 0.02f;
        public float onsetPersistence_s = 0.05f;

        [Header("Contact")]
        [Tooltip("Contact volume = mug radius + this padding (also above the rim).")]
        public float contactPadding_m = 0.01f;
        public float hoverDistance_m = 0.05f;
        [Tooltip("An effector must move this far out before the same mug can register a new contact.")]
        public float contactRearm_m = 0.02f;
        public bool hoverHighlight = true;

        [Header("Task")]
        [Tooltip("Task(s) preselected on the setup screen (the experimenter can change them per session).")]
        public TaskSelection defaultTasks = TaskSelection.Both;
        [Tooltip("Agent conditions preselected on the setup screen.")]
        public ConditionSelection defaultConditions = ConditionSelection.Both;

        [Header("Pick & place (spec section 9 pilot values; pad size lives in the StudyLayout)")]
        public float grabToPlacementTimeout_s = 10f;
        [Tooltip("Max time after an in-pad release for the stability check to succeed.")]
        public float placementSettleTimeout_s = 3f;
        public float padVerticalTolerance_m = 0.02f;
        public float padMaxTilt_deg = 15f;
        public float placementMaxSpeed_mps = 0.03f;
        public float placementMaxAngularSpeed_degps = 10f;
        public float placementHold_s = 0.3f;
        public float dropBelowTable_m = 0.08f;
        public float outOfBoundsRadius_m = 1.0f;

        [Header("Timeouts and pacing")]
        public float cueToContactTimeout_s = 5f;
        public float trackingLossGrace_s = 0.15f;
        public float interTrialInterval_s = 1.0f;
        public float trainingFeedback_s = 1.6f;
        public float neutralFeedback_s = 0.5f;
        [Tooltip("Re-show the instruction if the participant has not rested the hand for this long.")]
        public float idlePrompt_s = 6f;
        public float slowFrameFactor = 1.5f;

        [Header("Design")]
        [Tooltip("Default experimental trials per block (rounded up to a multiple of 3 for balance).")]
        public int trialsPerBlock = 12;
        public int trainingTrials = 6;
        [Tooltip("Longest run of the same target ownership in a row.")]
        public int maxSameTargetRun = 2;
        [Tooltip("0 = generate and save a new seed per session.")]
        public long seed = 0;
        public ConditionOrderMode conditionOrder = ConditionOrderMode.Auto;
        public MugOrderMode mugOrder = MugOrderMode.Auto;

        [Header("Repeats (spec: technical interruptions repeated later)")]
        public bool repeatInvalidTrials = true;
        public int maxRepeatsPerPlannedTrial = 2;

        [Header("Mug identity (default: slide colours/shapes)")]
        public List<OwnershipVisual> ownershipVisuals = new List<OwnershipVisual>
        {
            new OwnershipVisual { owner = Owner.Owned, label = "P1", colorName = "blue", color = new Color(0.36f, 0.62f, 1f), shape = MugShape.Diamond },
            new OwnershipVisual { owner = Owner.Neutral, label = "N", colorName = "green", color = new Color(0.42f, 0.80f, 0.45f), shape = MugShape.Triangle },
            new OwnershipVisual { owner = Owner.AgentOwned, label = "NPC", colorName = "red", color = new Color(0.93f, 0.40f, 0.40f), shape = MugShape.Square },
        };
        public string agentName = "Alex";
        [Tooltip("Agent body for Layout 2 (same for every participant unless changed here).")]
        public AgentAvatar agentAvatar = AgentAvatar.VirtualHumanMale;

        [Header("Display")]
        public float targetRefreshRate_hz = 90f;

        [Header("Real table (for the invisible occluder that hides the agent's legs behind the table)")]
        public float realTableWidth_m = 1.2f;
        public float realTableDepth_m = 0.8f;

        [Header("Calibration")]
        [Tooltip("Height of the controller's tracking point above the table when it lies on the table.")]
        public float controllerOnTableHeight_m = 0.03f;

        public string HandName => responseHand == StudyHand.Right ? "right" : "left";

        public OwnershipVisual Visual(Owner o) => ownershipVisuals.Find(v => v.owner == o) ?? new OwnershipVisual { owner = o, label = o.ToString() };

        public int BalancedTrialsPerBlock(int requested) => Math.Max(3, ((requested + 2) / 3) * 3);

        public ProtocolConfig CreateFrozenCopy()
        {
            var copy = CreateInstance<ProtocolConfig>();
            JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(this), copy);
            copy.name = name + " (session copy)";
            copy.hideFlags = HideFlags.DontSave;
            return copy;
        }

        public string ToJson() => JsonUtility.ToJson(this, true);

        public string ComputeHash()
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(JsonUtility.ToJson(this)));
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        public List<string> Validate()
        {
            var p = new List<string>();
            if (!agentAbsentLayout || !agentPresentLayout) p.Add("Both layouts must be assigned.");
            else
            {
                p.AddRange(agentAbsentLayout.Validate());
                p.AddRange(agentPresentLayout.Validate());
            }
            if (foreperiodMax_s < foreperiodMin_s) p.Add("Foreperiod max is below min.");
            if (restRadius_m <= 0f) p.Add("Rest radius must be positive.");
            if (trainingTrials < 3) p.Add("Use at least 3 training trials.");
            foreach (Owner o in Enum.GetValues(typeof(Owner)))
                if (ownershipVisuals.Find(v => v.owner == o) == null) p.Add("Missing visual for " + o);
            return p;
        }
    }
}
