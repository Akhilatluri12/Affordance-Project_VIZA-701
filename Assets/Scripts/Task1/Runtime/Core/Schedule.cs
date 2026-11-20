using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace XRStudy.Task1
{
    /// <summary>SplitMix64: fully specified PRNG so a saved seed reproduces the schedule on any runtime.</summary>
    public sealed class StudyRng
    {
        ulong m_State;
        public StudyRng(long seed) { m_State = unchecked((ulong)seed); }

        public ulong NextULong()
        {
            unchecked
            {
                ulong z = m_State += 0x9E3779B97F4A7C15UL;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }

        public double NextDouble() => (NextULong() >> 11) * (1.0 / (1UL << 53));

        public int NextInt(int maxExclusive)
        {
            if (maxExclusive <= 0) throw new ArgumentOutOfRangeException(nameof(maxExclusive));
            return (int)(NextDouble() * maxExclusive);
        }

        public void Shuffle<T>(IList<T> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = NextInt(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        public static long NewSeed()
        {
            var bytes = Guid.NewGuid().ToByteArray();
            long s = BitConverter.ToInt64(bytes, 0) & long.MaxValue;
            return s == 0 ? 1 : s;
        }
    }

    [Serializable]
    public class PlannedTrial
    {
        public string plannedTrialId;
        public int blockIndex;          // 0 = training, 1..n = experimental blocks
        public string blockId;          // "Training", "B1", "B2"
        public int trialIndexInBlock;   // 0-based position in the block's planned order
        public bool training;
        public StudyTask task;
        public PresenceCondition presence;
        public string layoutName;
        public int mugOrderIndex;
        public Owner targetOwner;
        public int targetSlot;
        public string targetSlotName;
        public int attemptNumber = 1;
        public string replacesAttemptId = "";

        public PlannedTrial CloneAsRepeat(string originalAttemptId, int nextAttemptNumber)
        {
            var c = (PlannedTrial)MemberwiseClone();
            c.attemptNumber = nextAttemptNumber;
            c.replacesAttemptId = originalAttemptId;
            return c;
        }
    }

    [Serializable]
    public class ScheduledBlock
    {
        public string blockId;
        public int blockIndex;
        public bool training;
        public PresenceCondition presence;
        public string layoutName;
        public List<PlannedTrial> trials = new List<PlannedTrial>();
    }

    [Serializable]
    public class StudySchedule
    {
        public string scheduleId;
        public string participantId;
        public StudyTask task;
        public long seed;
        public string generatorVersion = ScheduleGenerator.Version;
        public string conditionOrder;        // "AbsentFirst" | "PresentFirst"
        public string conditionOrderSource;  // "Auto(participant id)" | "Manual"
        public int mugOrderIndex;
        public string mugOrderLabel;         // e.g. "P1-N-NPC" (left to right)
        public string mugOrderSource;
        public int trialsPerBlock;
        public ScheduledBlock training;                                     // first practice block
        public List<ScheduledBlock> blocks = new List<ScheduledBlock>();      // experimental blocks in run order
        /// <summary>Everything in run order: each task's practice block followed by its experimental blocks.</summary>
        public List<ScheduledBlock> sequence = new List<ScheduledBlock>();
        public string taskSelection = "Touch";
        public string conditionSelection = "Both";
        public string taskOrder = "Touch";                                   // e.g. "Touch>PickPlace"
        public string taskOrderSource = "";

        public string ToCsv()
        {
            var sb = new StringBuilder();
            sb.AppendLine(Csv.Row("schedule_id", "participant_id", "task", "rng_seed", "condition_order", "mug_order", "block", "is_training",
                "agent_present", "layout_name", "trial_index", "planned_trial_id", "target_ownership", "target_slot"));
            void Add(ScheduledBlock b)
            {
                foreach (var t in b.trials)
                    sb.AppendLine(Csv.Row(scheduleId, participantId, t.task.ToString(), seed.ToString(CultureInfo.InvariantCulture), conditionOrder, mugOrderLabel,
                        t.blockId, Csv.B(t.training), Csv.B(t.presence == PresenceCondition.AgentPresent), t.layoutName,
                        Csv.I(t.trialIndexInBlock + 1), t.plannedTrialId, t.targetOwner.ToString(), t.targetSlotName));
            }
            if (sequence.Count > 0) foreach (var b in sequence) Add(b);
            else
            {
                if (training != null) Add(training);
                foreach (var b in blocks) Add(b);
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Within-subject schedule: a training block, then one block per agent condition. Inside every block the
    /// target ownership (Owned / Neutral / AgentOwned) appears equally often in a seeded random order with a
    /// bounded run length. Condition order and mug order are counterbalanced from the participant ID by default.
    /// </summary>
    public static class ScheduleGenerator
    {
        public const string Version = "2.0.0";

        /// <summary>Stable number from a participant ID: its digits if it has any, else an FNV-1a hash.</summary>
        public static long ParticipantNumber(string participantId)
        {
            var digits = new StringBuilder();
            foreach (var c in participantId ?? "") if (char.IsDigit(c)) digits.Append(c);
            if (digits.Length > 0 && digits.Length <= 15) return long.Parse(digits.ToString(), CultureInfo.InvariantCulture);
            unchecked
            {
                ulong h = 1469598103934665603UL;
                foreach (var c in participantId ?? "") { h ^= c; h *= 1099511628211UL; }
                return (long)(h & 0x7FFFFFFFFFFFFFFFUL);
            }
        }

        /// <summary>Even participant numbers start agent-absent, odd ones agent-present.</summary>
        public static PresenceCondition AutoFirstCondition(string participantId) =>
            ParticipantNumber(participantId) % 2 == 0 ? PresenceCondition.AgentAbsent : PresenceCondition.AgentPresent;

        /// <summary>Cycles through all six left-to-right orders every 12 consecutive participant numbers.</summary>
        public static int AutoMugOrder(string participantId) => (int)((ParticipantNumber(participantId) / 2) % 6);

        /// <summary>Single task, both agent conditions (kept for existing callers and tests).</summary>
        public static StudySchedule Generate(ProtocolConfig cfg, long seed, string sessionId, string participantId,
            ConditionOrderMode orderMode, MugOrderMode mugOrderMode, int trialsPerBlock, StudyTask task = StudyTask.Touch) =>
            Generate(cfg, seed, sessionId, participantId, orderMode, mugOrderMode, trialsPerBlock,
                task == StudyTask.PickPlace ? TaskSelection.PickPlace : TaskSelection.Touch, ConditionSelection.Both);

        /// <summary>Touch first for participant numbers 0-11, 24-35, …; pick & place first for 12-23, … (crossed with the
        /// condition and mug-order counterbalancing, which cycle every 2 and 12 numbers).</summary>
        public static bool AutoTouchFirst(string participantId) => (ParticipantNumber(participantId) / 12) % 2 == 0;

        /// <summary>
        /// Session plan: for each selected task (order counterbalanced when both) a practice block, then one block per
        /// selected agent condition (order counterbalanced when both). Ownership is balanced within every block.
        /// </summary>
        public static StudySchedule Generate(ProtocolConfig cfg, long seed, string sessionId, string participantId,
            ConditionOrderMode orderMode, MugOrderMode mugOrderMode, int trialsPerBlock, TaskSelection tasks, ConditionSelection conditions)
        {
            var s = GenerateCore(cfg, seed, sessionId, participantId, orderMode, mugOrderMode, trialsPerBlock, tasks, conditions);
            return s;
        }

        static StudySchedule GenerateCore(ProtocolConfig cfg, long seed, string sessionId, string participantId,
            ConditionOrderMode orderMode, MugOrderMode mugOrderMode, int trialsPerBlock, TaskSelection tasks, ConditionSelection conditions)
        {
            StudyTask task = tasks == TaskSelection.PickPlace ? StudyTask.PickPlace : StudyTask.Touch;
            if (seed == 0) throw new ArgumentException("Seed must be non-zero.");
            var rng = new StudyRng(seed);

            PresenceCondition first = conditions == ConditionSelection.AgentAbsentOnly ? PresenceCondition.AgentAbsent
                : conditions == ConditionSelection.AgentPresentOnly ? PresenceCondition.AgentPresent
                : orderMode switch
                {
                    ConditionOrderMode.AbsentFirst => PresenceCondition.AgentAbsent,
                    ConditionOrderMode.PresentFirst => PresenceCondition.AgentPresent,
                    _ => AutoFirstCondition(participantId)
                };
            var second = first == PresenceCondition.AgentAbsent ? PresenceCondition.AgentPresent : PresenceCondition.AgentAbsent;
            int mugOrder = mugOrderMode == MugOrderMode.Fixed ? 0 : AutoMugOrder(participantId);
            int perBlock = cfg.BalancedTrialsPerBlock(trialsPerBlock);

            var firstLayout = LayoutFor(cfg, first);
            var s = new StudySchedule
            {
                scheduleId = sessionId + "_sched",
                participantId = participantId,
                task = task,
                seed = seed,
                conditionOrder = first == PresenceCondition.AgentAbsent ? "AbsentFirst" : "PresentFirst",
                conditionOrderSource = conditions != ConditionSelection.Both ? "Single condition" : orderMode == ConditionOrderMode.Auto ? "Auto(participant id)" : "Manual",
                mugOrderIndex = firstLayout ? firstLayout.EffectiveOrder(mugOrder) : 0,
                mugOrderSource = mugOrderMode == MugOrderMode.Auto ? "Auto(participant id)" : "Fixed",
                trialsPerBlock = perBlock,
            };
            s.mugOrderLabel = firstLayout ? firstLayout.OrderLabel(mugOrder, o => cfg.Visual(o).label) : "";

            if (conditions != ConditionSelection.Both) s.conditionOrder = first == PresenceCondition.AgentAbsent ? "AbsentOnly" : "PresentOnly";
            var presences = conditions == ConditionSelection.Both ? new[] { first, second } : new[] { first };

            StudyTask[] taskList = tasks switch
            {
                TaskSelection.PickPlace => new[] { StudyTask.PickPlace },
                TaskSelection.Both => AutoTouchFirst(participantId) ? new[] { StudyTask.Touch, StudyTask.PickPlace } : new[] { StudyTask.PickPlace, StudyTask.Touch },
                _ => new[] { StudyTask.Touch },
            };
            s.taskSelection = tasks.ToString();
            s.conditionSelection = conditions.ToString();
            s.taskOrder = string.Join(">", taskList);
            s.taskOrderSource = tasks == TaskSelection.Both ? "Auto(participant id)" : "Single task";

            bool prefix = taskList.Length > 1;
            int expIndex = 0;
            foreach (var t in taskList)
            {
                string pre = prefix ? (t == StudyTask.Touch ? "Touch-" : "PickPlace-") : "";
                // Practice runs under the first block's condition so the first experimental block starts familiar.
                var tr = MakeBlock(cfg, rng, pre + "Training", 0, true, first, cfg.trainingTrials, mugOrder, t);
                s.sequence.Add(tr);
                if (s.training == null) s.training = tr;
                for (int c = 0; c < presences.Length; c++)
                {
                    var b = MakeBlock(cfg, rng, pre + "B" + (c + 1), ++expIndex, false, presences[c], perBlock, mugOrder, t);
                    s.sequence.Add(b);
                    s.blocks.Add(b);
                }
            }
            return s;
        }

        public static StudyLayout LayoutFor(ProtocolConfig cfg, PresenceCondition p) =>
            p == PresenceCondition.AgentPresent ? cfg.agentPresentLayout : cfg.agentAbsentLayout;

        static ScheduledBlock MakeBlock(ProtocolConfig cfg, StudyRng rng, string id, int index, bool training,
            PresenceCondition presence, int count, int mugOrder, StudyTask task)
        {
            var layout = LayoutFor(cfg, presence);
            var block = new ScheduledBlock { blockId = id, blockIndex = index, training = training, presence = presence, layoutName = layout ? layout.layoutName : "" };
            var targets = BalancedSequence(rng, count, Math.Max(1, cfg.maxSameTargetRun));
            for (int i = 0; i < targets.Count; i++)
            {
                int slot = layout ? layout.SlotOf(targets[i], mugOrder) : (int)targets[i];
                block.trials.Add(new PlannedTrial
                {
                    plannedTrialId = $"{id}-T{i + 1:00}",
                    blockIndex = index,
                    blockId = id,
                    trialIndexInBlock = i,
                    training = training,
                    task = task,
                    presence = presence,
                    layoutName = block.layoutName,
                    mugOrderIndex = layout ? layout.EffectiveOrder(mugOrder) : 0,
                    targetOwner = targets[i],
                    targetSlot = slot,
                    targetSlotName = layout ? layout.slots[slot].slotName : slot.ToString(),
                });
            }
            return block;
        }

        /// <summary>Equal counts per ownership (count rounded up to a multiple of 3), shuffled, run length bounded.</summary>
        public static List<Owner> BalancedSequence(StudyRng rng, int count, int maxRun)
        {
            int n = Math.Max(1, (count + 2) / 3);
            var list = new List<Owner>();
            for (int i = 0; i < n; i++) { list.Add(Owner.Owned); list.Add(Owner.Neutral); list.Add(Owner.AgentOwned); }
            for (int attempt = 0; attempt < 500; attempt++)
            {
                rng.Shuffle(list);
                if (LongestRun(list) <= maxRun) break;
            }
            return list;
        }

        public static int LongestRun(List<Owner> list)
        {
            int best = 0, run = 0;
            for (int i = 0; i < list.Count; i++)
            {
                run = i > 0 && list[i] == list[i - 1] ? run + 1 : 1;
                if (run > best) best = run;
            }
            return best;
        }
    }
}
