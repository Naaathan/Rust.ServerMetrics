using HarmonyLib;
using Network;

namespace RustServerMetrics.HarmonyPatches
{
    [HarmonyPatch(typeof(PacketProfiler), nameof(PacketProfiler.LogDetailed))]
    public class PacketProfiler_LogDetailed_Patch
    {
        [HarmonyPostfix]
        public static void Prefix(
            PacketProfilingData.PacketRealm realm,
            PacketProfilingData.PacketProfilingDirection direction,
            Message.Type type,
            NetworkableId entityId,
            string entityName,
            int length,
            byte[] data,
            int timestamp,
            string info)
        {
            if (!PacketProfiler.detailedProfiling || direction != PacketProfilingData.PacketProfilingDirection.Outbound)
                return;

            SingletonComponent<MetricsLogger>.Instance.OnPacketProfilerLogDetailed(entityId);
        }
    }
}
