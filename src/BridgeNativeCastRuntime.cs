// A cast can be a match, a known nonmatch, or unavailable. Preserve that distinction.
using System;
using System.Reflection;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        private static bool HasNativeIdentity(Type type)
        {
            for(var parent=type;parent!=null;parent=parent.BaseType)
                if(parent.FullName=="Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase") return true;
            return type.GetMember("Pointer",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance).Length>0;
        }

        private static MethodInfo UniqueNativeCastMethod(Type type,out bool named)
        {
            named=false;MethodInfo selected=null;
            if(type==null) return null;
            foreach(var method in type.GetMethods(BindingFlags.Public|BindingFlags.Instance))
            {
                if(method.Name!="TryCast") continue;
                named=true;
                if(!method.IsGenericMethodDefinition || method.GetParameters().Length!=0) continue;
                var generic=method.GetGenericArguments();
                if(generic.Length!=1 || method.ReturnType!=generic[0]) continue;
                if(selected!=null) return null;
                selected=method;
            }
            return selected;
        }

        // True means the answer is readable; typed==null is then a known nonmatch.
        // No instance or result is cached, and a failed invocation is never retried.
        private static bool TryNativeCast(object value,Type wanted,out object typed,out string errorCode)
        {
            typed=null;errorCode=null;
            if(wanted==null || wanted.ContainsGenericParameters || wanted.IsByRef || wanted.IsPointer || wanted.IsValueType)
            {errorCode="CAST_TARGET_UNAVAILABLE";return false;}
            if(value==null) return true;
            if(wanted.IsInstanceOfType(value)) {typed=value;return true;}
            try
            {
                bool named;var method=UniqueNativeCastMethod(value.GetType(),out named);
                bool nativeSource=HasNativeIdentity(value.GetType());
                if(method==null)
                {
                    if(!named && !nativeSource) return true; // Managed, concretely different fixture/data type.
                    errorCode="CAST_METHOD_UNAVAILABLE";return false;
                }
                var closed=method.MakeGenericMethod(wanted);
                if(closed.ContainsGenericParameters || closed.ReturnType!=wanted)
                {errorCode="CAST_METHOD_UNAVAILABLE";return false;}
                IntPtr sourcePointer=IntPtr.Zero;
                if(nativeSource)
                {
                    object pointer=MemberWithAccessor(value,"Pointer");
                    if(!(pointer is IntPtr) || (IntPtr)pointer==IntPtr.Zero)
                    {errorCode="CAST_IDENTITY_UNAVAILABLE";return false;}
                    sourcePointer=(IntPtr)pointer;
                }
                object result=closed.Invoke(value,null);
                if(result==null) return true;
                if(!wanted.IsInstanceOfType(result)) {errorCode="CAST_RESULT_TYPE_MISMATCH";return false;}
                if(nativeSource || HasNativeIdentity(result.GetType()))
                {
                    object pointer=MemberWithAccessor(result,"Pointer");
                    if(!nativeSource || !(pointer is IntPtr) || (IntPtr)pointer!=sourcePointer)
                    {errorCode="CAST_IDENTITY_MISMATCH";return false;}
                }
                typed=result;return true;
            }
            catch(Exception error)
            { RecordNativeReadFailure(value.GetType(), "TryCast", error); errorCode="CAST_UNAVAILABLE";return false; }
        }

        private static object CRTryNativeCast(object value,Type wanted)
        {
            object typed;string error;
            return TryNativeCast(value,wanted,out typed,out error)?typed:null;
        }

        private static bool? NativeCastIs(object value,Type wanted)
        {
            if(value==null) return false;
            object typed;string error;
            return TryNativeCast(value,wanted,out typed,out error)?(bool?)(typed!=null):null;
        }
    }
}
