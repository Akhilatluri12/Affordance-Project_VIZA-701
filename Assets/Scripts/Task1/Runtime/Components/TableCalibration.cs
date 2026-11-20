using System;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace XRStudy.Task1
{
    [Serializable]
    public class TableCalibrationData
    {
        public Vector3 position;   // centre of the table's near edge, on the table surface (tracking/world space)
        public float yaw_deg;      // direction across the table, yaw only
        public string method;      // "Controller" | "PlaneDetection"
        public string savedUtc;
        public float heightNudge_m;
    }

    /// <summary>
    /// The table origin: centre of the near edge, on the table surface, +z pointing across the table.
    /// Every study object is a child of this transform. Saved to persistentDataPath so it survives restarts;
    /// can be redone at any time.
    /// </summary>
    public class TableCalibration : MonoBehaviour
    {
        const string FileName = "table_calibration.json";

        public bool HasCalibration { get; private set; }
        public TableCalibrationData Data { get; private set; }
        public Pose TablePose => new Pose(transform.position, transform.rotation);
        public event Action<TableCalibrationData> Changed;

        public static string SavePath => Path.Combine(Application.persistentDataPath, FileName);

        /// <summary>Controller lying on the table at the centre of the near edge, pointing across the table.</summary>
        public static TableCalibrationData FromController(Pose controller, float controllerOnTableHeight)
        {
            Vector3 fwd = controller.rotation * Vector3.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            return new TableCalibrationData
            {
                position = controller.position - Vector3.up * controllerOnTableHeight,
                yaw_deg = Quaternion.LookRotation(fwd.normalized, Vector3.up).eulerAngles.y,
                method = "Controller",
            };
        }

        public void Apply(TableCalibrationData data, bool save)
        {
            Data = data;
            HasCalibration = true;
            transform.SetPositionAndRotation(data.position + Vector3.up * data.heightNudge_m, Quaternion.Euler(0f, data.yaw_deg, 0f));
            if (save) Save();
            Changed?.Invoke(data);
        }

        /// <summary>Preview without saving (used while the experimenter is positioning the controller).</summary>
        public void Preview(TableCalibrationData data) =>
            transform.SetPositionAndRotation(data.position + Vector3.up * data.heightNudge_m, Quaternion.Euler(0f, data.yaw_deg, 0f));

        public void NudgeHeight(float delta)
        {
            if (Data == null) return;
            Data.heightNudge_m += delta;
            Apply(Data, true);
        }

        public void Save()
        {
            try
            {
                Data.savedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                File.WriteAllText(SavePath, JsonUtility.ToJson(Data, true));
            }
            catch (Exception e) { Debug.LogWarning("[Task1] Could not save calibration: " + e.Message); }
        }

        public static TableCalibrationData Load()
        {
            try { return File.Exists(SavePath) ? JsonUtility.FromJson<TableCalibrationData>(File.ReadAllText(SavePath)) : null; }
            catch (Exception e) { Debug.LogWarning("[Task1] Could not read calibration: " + e.Message); return null; }
        }
    }
}
