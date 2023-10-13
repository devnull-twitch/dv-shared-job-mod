using DV.UserManagement;
using DV.Utils;
using HarmonyLib;
using System.Collections;
using System.Collections.Generic;

namespace JobShareMod
{
    [HarmonyPatch(typeof(StationProceduralJobsController), "GenerateProceduralJobsCoro")]
    static class StationProceduralJobsControllerPatch
    {
        static bool Prefix(
            StationProceduralJobsController __instance,
            ref StationController ___stationController,
            ref IEnumerator __result
        )
        {
            string userName = SingletonBehaviour<UserManager>.Instance.CurrentUser.Name;

            // move this into so its out of the main game loop thread
            List<JobPayload> payloads = ShareClient.Instance.GetStationJobs(___stationController.logicStation.ID, userName);
            FileLog.Log($"Loaded {payloads.Count} jobs for station {___stationController.logicStation.ID} because of player movement");

            __result = GeneratePayloadJobs(payloads, ___stationController, __instance);
            return false;
        }

        static private IEnumerator GeneratePayloadJobs(
            List<JobPayload> payloads,
            StationController stationController,
            StationProceduralJobsController jobsController
        )
        {
            yield return WaitFor.FixedUpdate;

            StationPaylodJobsController payloadJobController = new StationPaylodJobsController(stationController);
            foreach (JobPayload payload in payloads)
            {
                FileLog.Log($"job generation");

                string controllerName = $"ChainJob[{payload.Type}]: {stationController.logicStation.ID} - {payload.ID}";
                JobChainController controller = payloadJobController.MakeJobController(controllerName);
                StaticJobDefinition definition = payloadJobController.CreateJobController(payload, controller);
                payloadJobController.SpawnJobController(controller, definition);

                FileLog.Log($"added job to logicStation");
                yield return null;
            }

            jobsController.StopJobGeneration();
            FileLog.Log($"Done loading jobs for station {stationController.logicStation.ID}");
        }
    }
}
