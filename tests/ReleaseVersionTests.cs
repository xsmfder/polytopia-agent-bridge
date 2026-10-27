using System;
using System.Reflection;
using PolytopiaAgent;
public static class ReleaseVersionTests {
    public static void Main() {
        var asm=typeof(BridgeMod).Assembly;
        if(BridgeMod.BridgeVersion!=BridgeRelease.Version)throw new Exception("ready version mismatch");
        if(asm.GetName().Version.ToString()!=BridgeRelease.AssemblyVersion)throw new Exception("assembly version mismatch");
        var info=(AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(asm,typeof(AssemblyInformationalVersionAttribute));
        if(info==null || info.InformationalVersion!=BridgeRelease.Version)throw new Exception("informational version mismatch");
        bool found=false;
        foreach(var attribute in asm.GetCustomAttributesData())
            if(attribute.AttributeType.Name=="MelonInfoAttribute") {
                found=true;
                if((string)attribute.ConstructorArguments[2].Value!=BridgeRelease.Version)throw new Exception("Melon display version mismatch");
            }
        if(!found)throw new Exception("Melon metadata missing");
        Console.WriteLine("ReleaseVersionTests passed=4 failed=0");
    }
}
