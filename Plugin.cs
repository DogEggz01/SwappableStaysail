using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace SwappableStaysail
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency(
        ShipyardExpansionGuid,
        BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class SwappableStaysailPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "DogEggz";
        public const string PluginName = "Swappable Staysail";
        public const string PluginVersion = "1.1.4";
        public const string ShipyardExpansionGuid = "com.nandbrew.shipyardexpansion";

        private Harmony harmony;

        internal static SwappableStaysailPlugin Instance { get; private set; }
        internal static ManualLogSource Log { get; private set; }

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            harmony = new Harmony(PluginGuid);
            harmony.PatchAll();
            Logger.LogInfo(
                $"{PluginName} {PluginVersion} loaded. " +
                $"Sail package prefab indexes: legacy=" +
                $"{SailpackFactory.LegacyPrefabIndex}, standard=" +
                $"{SailpackFactory.StandardPrefabIndex}, small=" +
                $"{SailpackFactory.SmallPrefabIndex}.");
        }

        private void OnDestroy()
        {
            harmony?.UnpatchSelf();
            SailmakerSession.Dispose();
            StayInstallTargeting.Dispose();
            FurledStaysailButton.DisposeAll();
            SailpackFactory.Dispose();
            SailpackPersistence.Dispose();
            Instance = null;
            Log = null;
        }
    }
}
