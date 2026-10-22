using System;
using System.Diagnostics;
using System.Globalization;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        internal enum CityReadPhase
        { Identity, Bindings, Cities, FutureBuildings, UnitUpgrades, UnitActions, Ports, TileOptions, EconomicTiles, Rewards, Serialization, BindingConstants, MapContext }
        private const int CityReadPhaseCount = (int)CityReadPhase.MapContext + 1;

        private static readonly string[] CityReadPhaseNames = { "identity", "bindings", "cities", "future_buildings",
            "unit_upgrades", "unit_actions", "ports", "tile_options", "economic_tiles", "rewards", "serialization", "binding_constants", "map_context" };
        private readonly long[] _cityPhaseSamples = new long[CityReadPhaseCount], _cityPhaseTotal = new long[CityReadPhaseCount],
            _cityPhaseMax = new long[CityReadPhaseCount], _cityPhaseLast = new long[CityReadPhaseCount];
        private long _cityPhaseCalls;

        internal sealed class CityReadTiming : IDisposable
        {
            private readonly Action<long[], bool[]> publish;
            private readonly Func<long> clock;
            private readonly long[] ticks = new long[CityReadPhaseCount];
            private readonly bool[] used = new bool[CityReadPhaseCount];
            private int phase;
            private long started;
            private bool closed;
            internal CityReadTiming(Action<long[], bool[]> receiver, Func<long> timestamp)
            { publish = receiver; clock = timestamp; started = clock(); used[0] = true; }
            internal void Next(CityReadPhase next)
            {
                int index = (int)next;
                if (closed || index < 0 || index >= ticks.Length) return;
                long now = clock(); ticks[phase] += Math.Max(0L, now - started);
                phase = index; used[index] = true; started = now;
            }
            public void Dispose()
            {
                if (closed) return;
                ticks[phase] += Math.Max(0L, clock() - started); closed = true;
                if (publish != null) publish(ticks, used);
            }
        }

        internal static CityReadTiming BeginCityReadTiming()
        {
            var host = _performanceHost ?? _combatHookHost;
            return new CityReadTiming(host == null ? (Action<long[], bool[]>)null : host.RecordCityReadPhases,
                Stopwatch.GetTimestamp);
        }
        private void RecordCityReadPhases(long[] ticks, bool[] used)
        {
            _cityPhaseCalls++;
            for (int i = 0; i < _cityPhaseSamples.Length; i++)
            {
                _cityPhaseLast[i] = used[i] ? ticks[i] : 0L;
                if (!used[i]) continue;
                _cityPhaseSamples[i]++; _cityPhaseTotal[i] += ticks[i];
                _cityPhaseMax[i] = Math.Max(_cityPhaseMax[i], ticks[i]);
            }
        }
        private string CityReadPhasesJson()
        {
            var phases = new JsonObj();
            double scale = 1000.0 / Stopwatch.Frequency;
            for (int i = 0; i < CityReadPhaseNames.Length; i++)
                phases.Raw(CityReadPhaseNames[i], new JsonObj()
                    .Raw("samples", _cityPhaseSamples[i].ToString(CultureInfo.InvariantCulture))
                    .Raw("lastMs", (_cityPhaseLast[i] * scale).ToString("F3", CultureInfo.InvariantCulture))
                    .Raw("maxMs", (_cityPhaseMax[i] * scale).ToString("F3", CultureInfo.InvariantCulture))
                    .Raw("totalMs", (_cityPhaseTotal[i] * scale).ToString("F3", CultureInfo.InvariantCulture)).Close());
            return new JsonObj().Str("scope", "city snapshot body only; fixed phases, no game values")
                .Raw("calls", _cityPhaseCalls.ToString(CultureInfo.InvariantCulture)).Raw("phases", phases.Close()).Close();
        }
    }
}
