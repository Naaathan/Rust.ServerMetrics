using HarmonyLib;
using Network;

// ReSharper disable InconsistentNaming

namespace RustServerMetrics.HarmonyPatches;

[HarmonyPatch(typeof(NetWrite), nameof(NetWrite.Send))]
public class NetWrite_Send_Patch
{
    [HarmonyPrefix]
    public static void Prefix(NetWrite __instance, SendInfo info)
    {
        if (!MetricsLogger.IsReady) return;

        int num = __instance.PeekPacketID();
        if (num < 140) return;

        int type = num - 140;
        if (type >= 29) return;

        SingletonComponent<MetricsLogger>.Instance.OnNetWriteSend(__instance, info, type);
    }
}