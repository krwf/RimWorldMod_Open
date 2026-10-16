using System.Threading;
using UnityEngine;

namespace KRWF.RimKata
{
    internal sealed class RimKataPushVisual
    {
        private readonly Vector3 start, end;
        private int elapsed;

        internal RimKataPushVisual(Vector3 start, Vector3 end)
        {
            this.start = start;
            this.end = end;
        }

        internal void SetElapsed(int ticks)
            => Volatile.Write(ref elapsed, Mathf.Clamp(ticks, 0, RimKataPushMotion.Duration));

        internal void Place(ref Vector3 drawLoc)
        {
            float progress = Volatile.Read(ref elapsed) / (float)RimKataPushMotion.Duration;
            // Add travel to the existing pose; retain its facing, height and body offsets.
            drawLoc.x += Mathf.Lerp(start.x, end.x, progress) - end.x;
            drawLoc.z += Mathf.Lerp(start.z, end.z, progress) - end.z;
        }
    }
}
