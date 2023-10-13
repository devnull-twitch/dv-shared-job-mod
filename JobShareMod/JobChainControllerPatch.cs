using DV.Utils;
using HarmonyLib;
using System;

namespace JobShareMod
{
    [HarmonyPatch(typeof(JobChainController), "OnJobCompleted")]
    static class JobChainControllerOnJobCompletedPatch
    {
        static void Postfix(JobChainController __instance)
        {
            SingletonBehaviour<CarSpawner>.Instance.DeleteTrainCars(__instance.trainCarsForJobChain);
        }
    }
}
