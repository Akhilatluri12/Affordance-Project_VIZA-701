using UnityEngine;

namespace XRStudy.Task1
{
    /// <summary>Kinematic state of the target mug relative to the placement pad, sampled once per frame.</summary>
    public struct PlacementSample
    {
        public bool inPad;          // base reference inside the pad's horizontal bounds and vertical tolerance
        public bool upright;        // tilt within tolerance
        public float speed_mps;
        public float angularSpeed_degps;
        public bool selected;
        public bool outOfBounds;    // fell below the table or left the workspace

        public bool IsStable(ProtocolConfig c) =>
            inPad && upright && !selected && speed_mps < c.placementMaxSpeed_mps && angularSpeed_degps < c.placementMaxAngularSpeed_degps;
    }

    /// <summary>Pure geometry for pad containment and stability (no snapping, no side effects).</summary>
    public static class PlacementEvaluator
    {
        /// <param name="pad">Pad centre on the table surface; up = table normal.</param>
        /// <param name="padSize">Full pad width (x) and depth (z).</param>
        public static PlacementSample Evaluate(ProtocolConfig c, Pose pad, Vector2 padSize, Vector3 mugBase, Vector3 mugUp,
            float speed, float angularSpeedDeg, bool selected, Pose table)
        {
            Vector3 local = Quaternion.Inverse(pad.rotation) * (mugBase - pad.position);
            bool inPad = Mathf.Abs(local.x) <= padSize.x * 0.5f && Mathf.Abs(local.z) <= padSize.y * 0.5f &&
                         Mathf.Abs(local.y) <= c.padVerticalTolerance_m;
            Vector3 flat = mugBase - table.position; flat.y = 0f;
            return new PlacementSample
            {
                inPad = inPad,
                upright = Vector3.Angle(mugUp, pad.rotation * Vector3.up) <= c.padMaxTilt_deg,
                speed_mps = speed,
                angularSpeed_degps = angularSpeedDeg,
                selected = selected,
                outOfBounds = mugBase.y < table.position.y - c.dropBelowTable_m || flat.magnitude > c.outOfBoundsRadius_m
            };
        }
    }
}
