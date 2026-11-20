using System;
using System.Collections.Generic;
using UnityEngine;

namespace XRStudy.Task1
{
    /// <summary>
    /// Buffered event and pose collection, safe trial commits, export manifest and fault reporting.
    /// Rows are buffered in memory each frame and written to disk only at trial boundaries / outside timed
    /// states, so flushing never falls inside a timed response.
    /// </summary>
    [DefaultExecutionOrder(200)]
    public class DataLogger : MonoBehaviour
    {
        struct PoseSource
        {
            public string id;
            public Transform transform;
            public Func<bool> valid;
            public Func<(bool valid, Vector3 position)> point;   // for sources without a transform (hand joints)
        }

        readonly List<PoseSource> m_PoseSources = new List<PoseSource>();
        SessionWriter m_Writer;
        string m_SessionId = "", m_ParticipantId = "", m_Schema = "", m_TaskId = "";
        string m_SessionTimestamp = "", m_ConditionOrder = "", m_MugOrder = "";
        long m_Seed;
        StudyHand m_Hand;
        long m_NextEventId;
        float m_LastFlush;

        public float idleFlushInterval_s = 2f;
        public bool Active => m_Writer != null;
        public bool Failed => m_Writer != null && m_Writer.Failed;
        public string LastError => m_Writer?.LastError ?? "";
        public string SessionDirectory => m_Writer?.Directory ?? "";
        public SessionManifest Manifest => m_Writer?.Manifest;
        public bool PoseRecording { get; set; } = true;

        /// <summary>Provides the current attempt id and whether a timed state is active.</summary>
        public Func<string> CurrentAttemptId { get; set; } = () => "";
        public Func<bool> InTimedState { get; set; } = () => false;

        public event Action<string> LoggingFailed;

        public long NextEventId() => ++m_NextEventId;

        public void AddPoseSource(string id, Transform t, Func<bool> valid)
        {
            if (t) m_PoseSources.Add(new PoseSource { id = id, transform = t, valid = valid ?? (() => true) });
        }

        public void AddPointSource(string id, Func<(bool valid, Vector3 position)> point)
        {
            if (point != null) m_PoseSources.Add(new PoseSource { id = id, point = point });
        }

        public void ClearPoseSources() => m_PoseSources.Clear();

        public void Begin(SessionWriter writer, string sessionId, string participantId, string sessionTimestamp, ProtocolConfig cfg,
            long seed, string conditionOrder, string mugOrder)
        {
            m_Writer = writer;
            m_SessionId = sessionId; m_ParticipantId = participantId; m_SessionTimestamp = sessionTimestamp;
            m_Schema = cfg.schemaVersion; m_TaskId = cfg.taskId; m_Seed = seed; m_Hand = cfg.responseHand;
            m_ConditionOrder = conditionOrder; m_MugOrder = mugOrder;
            m_NextEventId = 0;
            m_LastFlush = Time.unscaledTime;
        }

        public void LogEvent(StudyEvent e)
        {
            if (m_Writer == null) return;
            m_Writer.Append(SessionWriter.EventsFile, TrialCsv.EventRow(e, m_Schema, m_SessionId));
        }

        /// <summary>Session-level event outside the trial state machine.</summary>
        public void LogSessionEvent(string type, string detail = "", string mugId = "")
        {
            LogEvent(new StudyEvent
            {
                eventId = NextEventId(), time_s = MonotonicClock.Now, frame = Time.frameCount, type = type, detail = detail,
                mugId = mugId, attemptId = CurrentAttemptId?.Invoke() ?? "", state = "Session"
            });
        }

        /// <summary>Commits a finished attempt and flushes everything to storage. Returns false on failure.</summary>
        public bool CommitTrial(TrialRecord r)
        {
            if (m_Writer == null) return false;
            m_Writer.Append(m_Writer.ResponsesFile, ResponsesCsv.Row(r, m_ParticipantId, m_SessionTimestamp, m_ConditionOrder, m_Seed, m_MugOrder, m_SessionId));
            m_Writer.Append(SessionWriter.TrialsFile, TrialCsv.Row(r, m_Schema, m_SessionId, m_ParticipantId, m_TaskId, m_Seed, m_MugOrder, m_Hand));
            var m = m_Writer.Manifest;
            if (!r.planned.training)
            {
                m.attempts++;
                if (r.Valid) m.validTrials++; else m.invalidAttempts++;
                if (r.attemptNumber > 1) m.replacementAttempts++;
            }
            return Flush(true);
        }

        public void LogRecall(int attempt, string mugLabel, Owner trueOwner, Owner answered)
        {
            m_Writer?.Append(SessionWriter.RecallFile, Csv.Row(m_SessionId, m_ParticipantId, Csv.I(attempt), mugLabel, trueOwner.ToString(),
                answered.ToString(), Csv.B(trueOwner == answered), Csv.T(MonotonicClock.Now)));
        }

        public bool WriteJsonOnce(string name, string json, string description) => m_Writer != null && m_Writer.WriteJsonOnce(name, json, description) || ReportIfFailed();

        public bool Flush(bool withManifest)
        {
            if (m_Writer == null) return false;
            bool ok = m_Writer.Flush() && (!withManifest || m_Writer.WriteManifest());
            m_LastFlush = Time.unscaledTime;
            if (!ok) ReportIfFailed();
            return ok;
        }

        bool ReportIfFailed()
        {
            if (m_Writer != null && m_Writer.Failed) LoggingFailed?.Invoke(m_Writer.LastError);
            return false;
        }

        public void Close(string status)
        {
            if (m_Writer == null) return;
            m_Writer.Close(status);
            m_Writer = null;
        }

        void LateUpdate()
        {
            if (m_Writer == null || m_Writer.Failed) return;
            if (PoseRecording && m_PoseSources.Count > 0)
            {
                // One sample per rendered frame; the actual rate is recoverable from time_s / frameDt_s.
                double t = MonotonicClock.Now;
                string attempt = CurrentAttemptId?.Invoke() ?? "";
                string time = Csv.T(t), frame = Csv.I(Time.frameCount), dt = Csv.F(Time.unscaledDeltaTime);
                foreach (var s in m_PoseSources)
                {
                    Vector3 p; Quaternion q = Quaternion.identity; bool valid;
                    if (s.point != null)
                    {
                        var v = s.point();
                        if (!v.valid) continue;
                        p = v.position; valid = true;
                    }
                    else
                    {
                        if (!s.transform) continue;
                        s.transform.GetPositionAndRotation(out p, out q);
                        valid = s.valid();
                    }
                    m_Writer.Append(SessionWriter.PosesFile, Csv.Row(m_SessionId, attempt, time, frame, dt, s.id,
                        Csv.F(p.x), Csv.F(p.y), Csv.F(p.z), Csv.F(q.x), Csv.F(q.y), Csv.F(q.z), Csv.F(q.w), Csv.B(valid)));
                }
            }
            // Outside timed states, flush periodically so long pauses do not accumulate memory.
            if (!InTimedState() && Time.unscaledTime - m_LastFlush > idleFlushInterval_s) Flush(false);
        }

        void OnApplicationPause(bool paused)
        {
            // Headset removed / app backgrounded: persist what we have (may already be outside a timed state).
            if (paused && m_Writer != null) Flush(true);
        }
    }

    /// <summary>Shared monotonic elapsed-time source for every timestamp (seconds).</summary>
    public static class MonotonicClock
    {
        public static double Now => Time.realtimeSinceStartupAsDouble;
    }
}
