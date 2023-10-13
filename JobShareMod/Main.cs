using HarmonyLib;
using System;
using System.Reflection;
using UnityEngine;
using UnityModManagerNet;

namespace JobShareMod
{
    [EnableReloading]
    public static class Main
    {
        const string MODID = "JobShareMod";

        public static string baseURL = "http://localhost:8083";

        private static Harmony harmonyInstance;

        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            modEntry.OnGUI = OnGUI;
            modEntry.OnUnload = Unload;

            Harmony.DEBUG = true;
            harmonyInstance = new Harmony(MODID);
            harmonyInstance.PatchAll(Assembly.GetExecutingAssembly());

            return true;
        }

        static bool Unload(UnityModManager.ModEntry modEntry)
        {
            harmonyInstance.UnpatchAll();

            return true;
        }

        public static void OnGUI(UnityModManager.ModEntry modEntry)
        {
            GUILayout.Label("Server settings");

            GUILayout.Label("Base URL");
            baseURL = GUILayout.TextField(baseURL);
        }
    }
}
