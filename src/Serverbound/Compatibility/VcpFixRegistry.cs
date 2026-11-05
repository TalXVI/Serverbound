using System;
using System.Reflection;
using HarmonyLib;

namespace Serverbound.Compatibility
{
    internal static class VcpFixRegistry
    {
        // Preserve VCP's diagnostic state when Serverbound activates a headless fix.
        internal static Action MarkApplied(Assembly vcp, Type group)
        {
            Type registry = Guard.Type(vcp, "ValheimCommunityPatch.FixRegistry");
            Type entryType = Guard.Type(vcp, "ValheimCommunityPatch.FixRegistry+Entry");
            object entry = Guard.Method(registry, "Get", entryType, typeof(Type)).Invoke(null, new object[] { group });
            FieldInfo outcome = AccessTools.Field(entryType, "Outcome");
            object gate = AccessTools.Field(registry, "Gate").GetValue(null);
            object previous;
            lock (gate) previous = outcome.GetValue(entry);
            Action restore = () => { lock (gate) outcome.SetValue(entry, previous); };

            try { Guard.Method(registry, "RecordPatched", typeof(void), typeof(Type), typeof(bool)).Invoke(null, new object[] { group, true }); }
            catch { restore(); throw; }
            return restore;
        }
    }
}
