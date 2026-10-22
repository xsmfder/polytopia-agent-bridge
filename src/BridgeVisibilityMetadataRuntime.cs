using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        // Immutable CLR method metadata only. Every tile still invokes the native visibility
        // filter with the CURRENT state, player and includeHidden=false; no result is retained.
        private static readonly ConcurrentDictionary<Type, Lazy<MethodInfo>> _unitVisibilityMethods =
            new ConcurrentDictionary<Type, Lazy<MethodInfo>>();
        private static readonly ConcurrentDictionary<Type, Lazy<MethodInfo>> _resourceVisibilityMethods =
            new ConcurrentDictionary<Type, Lazy<MethodInfo>>();
        private static long _visibilityMethodBuilds;

        internal static MethodInfo FindTileVisibilityMethod(Type type, bool unit)
        {
            if (type == null) return null;
            var cache = unit ? _unitVisibilityMethods : _resourceVisibilityMethods;
            Lazy<MethodInfo> plan;
            if (!cache.TryGetValue(type, out plan))
            {
                if (cache.Count >= 1024) return BuildTileVisibilityMethod(type, unit);
                plan = cache.GetOrAdd(type, t => new Lazy<MethodInfo>(
                    () => BuildTileVisibilityMethod(t, unit), LazyThreadSafetyMode.ExecutionAndPublication));
            }
            try { return plan.Value; }
            catch
            {
                // Metadata failure is retried on a subsequent call, like the uncached path.
                Lazy<MethodInfo> removed;
                cache.TryRemove(type, out removed);
                throw;
            }
        }

        private static MethodInfo BuildTileVisibilityMethod(Type type, bool unit)
        {
            Interlocked.Increment(ref _visibilityMethodBuilds);
            MethodInfo result = null;
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (method.Name != (unit ? "GetUnit" : "GetResource")) continue;
                if (method.ContainsGenericParameters || method.ReturnType == typeof(void)) continue;
                var args = method.GetParameters();
                if (args.Length != (unit ? 3 : 2)) continue;
                if (args[0].ParameterType.Name.IndexOf("GameState", StringComparison.Ordinal) < 0) continue;
                if (args[1].ParameterType != typeof(byte)) continue;
                if (unit && args[2].ParameterType != typeof(bool)) continue;
                // The visibility filter is an authority boundary: metadata order must
                // never choose between compatible GameState overloads.
                if (result != null) return null;
                result = method;
            }
            return result;
        }
    }
}
