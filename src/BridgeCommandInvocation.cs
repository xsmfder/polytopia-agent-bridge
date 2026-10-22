using System;
using System.Globalization;
using System.Reflection;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        // Common read-only qualification adapter for unit, tile and diplomacy menus.
        // Resolve the exact native bool IsValid(GameState, out string) before calling.
        private static bool? NativeCommandValidity(object command, object state, out string error)
        {
            error = "native validation unavailable";
            if (command == null || state == null) return null;
            try
            {
                MethodInfo selected = null;
                foreach (var method in command.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    var args = method.GetParameters();
                    if (method.Name != "IsValid" || method.ContainsGenericParameters || method.ReturnType != typeof(bool)
                        || args.Length != 2 || args[0].ParameterType.Name != "GameState"
                        || !args[0].ParameterType.IsInstanceOfType(state) || !args[1].IsOut
                        || args[1].ParameterType != typeof(string).MakeByRefType()) continue;
                    if (selected != null) return null;
                    selected = method;
                }
                if (selected == null) return null;
                var values = new[] { state, (object)null };
                bool? valid = NativeBool(selected.Invoke(command, values));
                error = values[1] as string;
                return valid;
            }
            catch (Exception failure)
            {
                error = "native validation unavailable: " + (failure.InnerException ?? failure).GetType().Name;
                return null;
            }
        }

        // Bind metadata before constructing/invoking anything. One-argument legacy
        // entry points are supported, but multiple supported shapes are ambiguous.
        private static MethodInfo FindCommandExecutor(Type managerType, Type commandType)
        {
            Type commandBase = FindGameType("CommandBase");
            if (managerType == null || commandType == null || commandBase == null
                || !commandBase.IsAssignableFrom(commandType)) return null;
            MethodInfo found = null;
            foreach (var method in managerType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (method.Name != "ExecuteCommand" || method.ContainsGenericParameters
                    || method.ReturnType != typeof(bool)) continue;
                var parameters = method.GetParameters();
                if ((parameters.Length != 1 && parameters.Length != 2)
                    || parameters[0].ParameterType != commandBase || parameters[0].IsOut) continue;
                if (parameters.Length == 2 && (!parameters[1].IsOut
                    || parameters[1].ParameterType != typeof(string).MakeByRefType())) continue;
                if (found != null) return null;
                found = method;
            }
            return found;
        }

        // No rebinding or retry after Invoke. A thrown native call stays unknown to
        // the caller's original settlement ticket, even if it changed something first.
        private static object InvokeCommandExecutor(MethodInfo method, object manager, object command,
            out string validationError)
        {
            validationError = "";
            var args = method.GetParameters().Length == 1 ? new[] {command} : new[] {command, null};
            object value;
            using (BeginNativeExecutionTiming()) value = method.Invoke(manager, args);
            if (args.Length == 2) validationError = args[1] as string ?? "";
            return value;
        }

        private static string CommandConstructionUnknown(string errorCode)
        {
            return new JsonObj().Bool("ok", false).Str("errorCode", errorCode)
                .Str("outcome", "unknown").Str("verdict", "unknown")
                .Bool("diagnosticsWithheld", true).Close();
        }

        private string BuildCommandInvocationFailure(object gs, long ticket, string label, string called,
            byte controlled, Exception error, bool audit)
        {
            var response = new JsonObj().Bool("ok", false).Str("action", label).Str("via", called)
                .Str("controlledPlayerId", controlled.ToString(CultureInfo.InvariantCulture))
                .Str("verdict", "unknown").Str("outcome", "unknown").Str("errorCode", "INVOKE_FAILED");
            if (audit) response.Bool("diagnostics", true).Str("invokeDetail", error.GetType().Name + ": " + error.Message);
            else response.Bool("diagnosticsWithheld", true);
            // The ticket already existed before Invoke. Preserve it on the failure
            // response so clients can reconcile this exact request without resending.
            return AttachCombatSettlement(response.Close(), gs, ticket);
        }
    }
}
