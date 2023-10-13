using HarmonyLib;

namespace JobShareMod
{
    [HarmonyPatch(typeof(CarsSaveManager), "Load")]
    static class CarsSaveManagerPatch
    {
        static bool Prefix(ref bool __result)
        {
            __result = true;
            return false;
        }
    }
}
