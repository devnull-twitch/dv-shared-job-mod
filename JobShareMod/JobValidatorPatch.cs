using DV.CabControls;
using DV.Printers;
using DV.UserManagement;
using DV.Utils;
using DV.ThingTypes;
using HarmonyLib;
using System;

namespace JobShareMod
{
    [HarmonyPatch(typeof(JobValidator), "ProcessJobOverview")]
    static class JobValidatorPatchProcessJobOverview
    {
        static bool Prefix(JobOverview jobOverview, ref PrinterController ___bookletPrinter)
        {
            string userName = SingletonBehaviour<UserManager>.Instance.CurrentUser.Name;
            if (!ShareClient.Instance.ReserveJob(jobOverview.job, userName))
            {
                ___bookletPrinter.PlayErrorSound();
                return false;
            }

            return true;
        }

        static void Postfix(JobOverview jobOverview)
        {
            string userName = SingletonBehaviour<UserManager>.Instance.CurrentUser.Name;
            if (!ShareClient.Instance.TakeJob(jobOverview.job, userName))
            {
                throw new Exception("Unable to take job. Buts it reserved. Halp!!");
            }
        }
    }

    [HarmonyPatch(typeof(JobValidator), "ValidateJob")]
    static class JobValidatorPatchValidateJob
    {
        static void Postfix(JobBooklet jobBooklet)
        {
            if (jobBooklet.job.State != JobState.Completed)
            {
                FileLog.Log($"job incomplete. not telling webserver");
                return;
            }

            string userName = SingletonBehaviour<UserManager>.Instance.CurrentUser.Name;
            if (!ShareClient.Instance.FinishJob(jobBooklet.job, userName))
            {
                throw new Exception("Unable to finish job. Welp.");
            }
            FileLog.Log($"marked job as finish in webserver");
        }
    }
}
