using System;
using System.Diagnostics;
using System.Globalization;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        private readonly UpdateFrameTimings _updateFrameTimings = new UpdateFrameTimings();
        internal sealed class UpdateFrameTimings
        {
            private static readonly int[] BoundsMs = { 1, 2, 4, 8, 16, 25, 34, 50, 75, 100, 150, 250, 500 };
            private readonly long[] intervals = new long[14], work = new long[14];
            private long frames, intervalCount, invalidSamples, previous;
            private bool hasPrevious;
            private double intervalMs, bridgeWorkMs, maxIntervalMs, maxBridgeWorkMs;
            private static void Bucket(long[] counts, double ms)
            {
                int index=0;
                while(index<BoundsMs.Length && ms>BoundsMs[index]) index++;
                counts[index]++;
            }
            internal void Record(long entry, long exit, long frequency)
            {
                frames++;
                if(frequency<=0 || exit<entry || (hasPrevious && entry<previous))
                { invalidSamples++; hasPrevious=false; return; }
                double own=(exit-entry)*1000.0/frequency;
                bridgeWorkMs+=own; maxBridgeWorkMs=Math.Max(maxBridgeWorkMs,own); Bucket(work,own);
                if(hasPrevious)
                {
                    double gap=(entry-previous)*1000.0/frequency;
                    intervalCount++; intervalMs+=gap; maxIntervalMs=Math.Max(maxIntervalMs,gap); Bucket(intervals,gap);
                }
                previous=entry; hasPrevious=true;
            }
            internal string Json()
            {
                var bounds=new JsonArr(); foreach(int value in BoundsMs) bounds.Raw(value.ToString(CultureInfo.InvariantCulture)); bounds.Raw("null");
                var gaps=new JsonArr(); foreach(long value in intervals) gaps.Raw(value.ToString(CultureInfo.InvariantCulture));
                var costs=new JsonArr(); foreach(long value in work) costs.Raw(value.ToString(CultureInfo.InvariantCulture));
                return new JsonObj().Str("schema","update-frame-timing-1")
                    .Str("scope","completed bridge OnUpdate callbacks; entry intervals, not GPU/rendered FPS; work excludes hooks outside OnUpdate")
                    .Raw("frames",frames.ToString(CultureInfo.InvariantCulture)).Raw("intervalCount",intervalCount.ToString(CultureInfo.InvariantCulture))
                    .Raw("invalidSamples",invalidSamples.ToString(CultureInfo.InvariantCulture))
                    .Raw("intervalMs",intervalMs.ToString("F3",CultureInfo.InvariantCulture))
                    .Raw("bridgeWorkMs",bridgeWorkMs.ToString("F3",CultureInfo.InvariantCulture))
                    .Raw("maxIntervalMs",maxIntervalMs.ToString("F3",CultureInfo.InvariantCulture))
                    .Raw("maxBridgeWorkMs",maxBridgeWorkMs.ToString("F3",CultureInfo.InvariantCulture))
                    .Raw("bucketUpperBoundsMs",bounds.Close()).Raw("intervalHistogram",gaps.Close())
                    .Raw("bridgeWorkHistogram",costs.Close()).Close();
            }
        }
    }
}
