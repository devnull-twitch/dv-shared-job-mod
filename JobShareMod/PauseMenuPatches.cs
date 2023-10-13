using HarmonyLib;
using System;
using System.Net.WebSockets;
using System.Threading.Tasks;

namespace JobShareMod
{
    [HarmonyPatch(typeof(PauseMenu), "OnExitLevelRequested")]
    static class PauseMenuOnExitLevelRequestedPatch
    {
        static void Postfix()
        {
            PauseMenuHelper.CloseWebsocket();
        }
    }

    [HarmonyPatch(typeof(PauseMenu), "OnQuitRequested")]
    static class PauseMenuOnQuitRequestedPatch
    {
        static void Postfix()
        {
            PauseMenuHelper.CloseWebsocket();
        }
    }

    static class PauseMenuHelper
    {
        public static void CloseWebsocket()
        {
            try
            {
                Task closeTask = JobSaveManagerPatch.clientWebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "game quit", default);
                if (!closeTask.Wait(TimeSpan.FromSeconds(1)))
                {
                    FileLog.Log("unable to close websocket in 1 sec");
                }
            }
            catch (Exception)
            {
                FileLog.Log("u know ... life is tough sometimes.");
            }

            JobSaveManagerPatch.clientWebSocket = null;
        }
    }
}
