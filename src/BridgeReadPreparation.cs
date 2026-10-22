// Prepare only this bridge's managed query code, without invoking any query or game API.
using System;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        private static int _readPreparationState, _readPreparedMethods, _readPreparationFailures;
        private static long _readPreparationTicks;

        internal static bool IsReadPreparationTarget(MethodInfo method)
        {
            if (method == null || method.DeclaringType == null || method.DeclaringType.Assembly != typeof(BridgeMod).Assembly
                || method.IsAbstract || method.ContainsGenericParameters || method.GetMethodBody() == null) return false;
            for (Type owner = method.DeclaringType; owner != null; owner = owner.DeclaringType)
                if (owner == typeof(PolytopiaCityOps.CityOptionsSnapshot)) return true;
            return method.DeclaringType == typeof(BridgeMod) && method.Name == "WorldDump";
        }

        internal static void StartReadPreparation()
        {
            if (Interlocked.CompareExchange(ref _readPreparationState, 1, 0) != 0) return;
            try
            {
                if (!ThreadPool.QueueUserWorkItem(PrepareReadMethods)) Interlocked.Exchange(ref _readPreparationState, 5);
            }
            catch { Interlocked.Exchange(ref _readPreparationState, 5); }
        }

        private static void PrepareReadMethods(object unused)
        {
            Interlocked.Exchange(ref _readPreparationState, 2);
            long started = Stopwatch.GetTimestamp();
            try
            {
                // Metadata is confined to our query types and their own nested helpers. No native types, receivers,
                // delegates, constructors, static initializers or game values are requested.
                var types = new System.Collections.Generic.List<Type>();
                types.Add(typeof(BridgeMod)); types.Add(typeof(PolytopiaCityOps.CityOptionsSnapshot));
                for (int i = 1; i < types.Count; i++)
                    types.AddRange(types[i].GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic));
                foreach (Type type in types)
                    foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                        | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        if (!IsReadPreparationTarget(method)) continue;
                        try
                        {
                            RuntimeHelpers.PrepareMethod(method.MethodHandle);
                            Interlocked.Increment(ref _readPreparedMethods);
                        }
                        catch { Interlocked.Increment(ref _readPreparationFailures); }
                    }
            }
            catch { Interlocked.Increment(ref _readPreparationFailures); }
            finally
            {
                Interlocked.Exchange(ref _readPreparationTicks, Stopwatch.GetTimestamp() - started);
                Interlocked.Exchange(ref _readPreparationState, _readPreparationFailures == 0 ? 3 : 4);
            }
        }

        internal static string ReadPreparationJson()
        {
            int state = Interlocked.CompareExchange(ref _readPreparationState, 0, 0);
            string status = state == 0 ? "not_started" : state == 1 ? "queued" : state == 2 ? "preparing"
                : state == 3 ? "complete" : state == 4 ? "partial" : "unavailable";
            return new JsonObj().Str("status", status).Bool("background", true)
                .Raw("preparedMethods", Interlocked.CompareExchange(ref _readPreparedMethods, 0, 0).ToString(CultureInfo.InvariantCulture))
                .Raw("failures", Interlocked.CompareExchange(ref _readPreparationFailures, 0, 0).ToString(CultureInfo.InvariantCulture))
                .Raw("elapsedMs", (Interlocked.Read(ref _readPreparationTicks) * 1000.0 / Stopwatch.Frequency).ToString("F3", CultureInfo.InvariantCulture))
                .Str("scope", "bridge-managed-code-only; no query invocation or native values").Close();
        }
    }
}
