using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace SwappableStaysail
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency(
        ShipyardExpansionGuid,
        BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(
        SailDamageBridge.SailDamageGuid,
        BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class SwappableStaysailPlugin : BaseUnityPlugin
    {
        // Preserve the identity of the installed 1.1.4 release.
        public const string PluginGuid = "DogEggz";
        public const string PluginName = "Swappable Staysail";
        public const string PluginVersion = "1.2.3";
        public const string ShipyardExpansionGuid = "com.nandbrew.shipyardexpansion";
        internal const string SaveDataKey = "DogEggz";

        private Harmony harmony;

        internal static SwappableStaysailPlugin Instance { get; private set; }
        internal static ManualLogSource Log { get; private set; }

        private void Awake()
        {
            Instance = this;
            Log = Logger;
            harmony = new Harmony(PluginGuid);
            harmony.PatchAll();
            SailDamageBridge.Initialize(harmony);
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
            SailDamageBridge.Dispose();
            Instance = null;
            Log = null;
        }
    }

    // Sail Damage (kap.saildamage) compatibility, all by reflection: a staysail
    // packed into a sail package keeps its damage, patches and hole pattern, and
    // Sail Maker mode stays apart from Sail Damage's Patch Sails order.
    internal static class SailDamageBridge
    {
        internal const string SailDamageGuid = "kap.saildamage";
        // Sail Damage's Patch Sails shipyard button uses this unnamed function.
        internal const ShipyardButton.ButtonFunction PatchSailsFunction =
            (ShipyardButton.ButtonFunction)1000;

        private static FieldInfo damageModelInstance;
        private static MethodInfo snapshotMethod;
        private static MethodInfo restoreMethod;
        private static MethodInfo saveKeyMethod;
        private static FieldInfo patchSailsOrdered;

        // Sail Damage seeds a sail's hole pattern with DamageModel.SaveKey, which
        // depends on where the sail is installed. A sail installed from a package
        // keeps the key of the place it was packed from, so its holes stay put.
        private static readonly Dictionary<Sail, string> HoleKeys =
            new Dictionary<Sail, string>();
        // Loaded installed staysails' keys, by their SaveKey, until the sail is
        // first drawn or packed.
        private static readonly Dictionary<string, string> PendingHoleKeys =
            new Dictionary<string, string>();
        // Carries a key from a sail to its rebuilt copy when the game rebuilds a
        // boat's sails from SaveableBoatCustomization.GetData (shipyard Cancel).
        private static readonly ConditionalWeakTable<SaveSailData, string> RebuildKeys =
            new ConditionalWeakTable<SaveSailData, string>();
        private static Sail drawingSail;

        private static bool DamageAvailable =>
            damageModelInstance != null && snapshotMethod != null && restoreMethod != null;

        private static bool HoleKeysAvailable { get; set; }

        internal static void Initialize(Harmony harmony)
        {
            if (!Chainloader.PluginInfos.TryGetValue(SailDamageGuid, out PluginInfo plugin) ||
                plugin?.Instance == null)
            {
                return;
            }

            Assembly assembly = plugin.Instance.GetType().Assembly;
            Type damageModelType = assembly.GetType("SailDamage.DamageModel");
            Type shipyardRepairType = assembly.GetType("SailDamage.ShipyardRepair");
            Type sailVisualsType = assembly.GetType("SailDamage.SailVisuals");
            Type holesType = sailVisualsType != null
                ? AccessTools.Inner(sailVisualsType, "Holes")
                : null;
            if (damageModelType != null)
            {
                damageModelInstance = AccessTools.Field(damageModelType, "Instance");
                snapshotMethod = AccessTools.Method(
                    damageModelType, "Snapshot", new[] { typeof(Sail) });
                restoreMethod = AccessTools.Method(
                    damageModelType, "Restore", new[] { typeof(Sail), typeof(float[]) });
                saveKeyMethod = AccessTools.Method(
                    damageModelType, "SaveKey", new[] { typeof(Sail) });
            }
            if (shipyardRepairType != null)
            {
                patchSailsOrdered = AccessTools.Field(shipyardRepairType, "Ordered");
            }

            ConstructorInfo holesConstructor = holesType == null
                ? null
                : AccessTools.GetDeclaredConstructors(holesType).FirstOrDefault(constructor =>
                {
                    ParameterInfo[] parameters = constructor.GetParameters();
                    return parameters.Length != 0 && parameters[0].ParameterType == typeof(Sail);
                });
            if (saveKeyMethod != null && holesConstructor != null)
            {
                try
                {
                    harmony.Patch(
                        holesConstructor,
                        prefix: Hook(nameof(HolesPrefix)),
                        finalizer: Hook(nameof(HolesFinalizer)));
                    harmony.Patch(saveKeyMethod, postfix: Hook(nameof(SaveKeyPostfix)));
                    harmony.Patch(
                        AccessTools.Method(typeof(SaveableBoatCustomization), "GetData"),
                        postfix: Hook(nameof(RebuildRecordPostfix)));
                    harmony.Patch(
                        AccessTools.Method(typeof(Mast), "LoadSail"),
                        prefix: Hook(nameof(LoadSailPrefix)),
                        postfix: Hook(nameof(LoadSailPostfix)));
                    HoleKeysAvailable = true;
                }
                catch (Exception exception)
                {
                    SwappableStaysailPlugin.Log?.LogWarning(
                        "Sail Damage found, but its hole positions cannot be kept " +
                        $"for packed staysails: {exception}");
                }
            }

            SwappableStaysailPlugin.Log?.LogInfo(
                $"Sail Damage {plugin.Metadata.Version} found: packed staysails keep " +
                "damage " + (DamageAvailable ? "on" : "OFF") +
                ", hole positions " + (HoleKeysAvailable ? "on" : "OFF") +
                ", Patch Sails guard " + (patchSailsOrdered != null ? "on" : "OFF") + ".");
        }

        internal static void Dispose()
        {
            HoleKeys.Clear();
            PendingHoleKeys.Clear();
            drawingSail = null;
        }

        internal static bool PatchSailsOrdered()
        {
            try
            {
                return patchSailsOrdered != null && (bool)patchSailsOrdered.GetValue(null);
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static void CaptureDamage(Sail sail, SailpackRecord record)
        {
            if (!DamageAvailable || sail == null || record == null)
            {
                return;
            }

            try
            {
                object model = damageModelInstance.GetValue(null);
                float[] snapshot = model != null
                    ? snapshotMethod.Invoke(model, new object[] { sail }) as float[]
                    : null;
                if (snapshot == null)
                {
                    return;
                }
                record.damage = (float[])snapshot.Clone();
                record.holeKey = HoleKeyFor(sail);
            }
            catch (Exception exception)
            {
                SwappableStaysailPlugin.Log?.LogWarning(
                    "Could not read Sail Damage data of " + sail.sailName + "; its " +
                    $"package does not keep the damage: {exception}");
            }
        }

        internal static void ApplyDamage(Sail sail, SailpackRecord record)
        {
            if (sail == null || record == null)
            {
                return;
            }

            try
            {
                if (HoleKeysAvailable && !string.IsNullOrEmpty(record.holeKey))
                {
                    HoleKeys[sail] = record.holeKey;
                }
                if (DamageAvailable && record.damage != null &&
                    record.damage.Length >= SailpackRecord.DamageValueCount)
                {
                    object model = damageModelInstance.GetValue(null);
                    if (model != null)
                    {
                        restoreMethod.Invoke(
                            model,
                            new object[] { sail, (float[])record.damage.Clone() });
                    }
                }
            }
            catch (Exception exception)
            {
                SwappableStaysailPlugin.Log?.LogWarning(
                    "Could not give " + sail.sailName + " its Sail Damage data back: " +
                    exception);
            }
        }

        internal static void Forget(Sail sail)
        {
            // A plain reference check: a destroyed sail's key goes too.
            if (sail is object)
            {
                HoleKeys.Remove(sail);
            }
        }

        internal static Dictionary<string, string> GetInstalledHoleKeys()
        {
            Dictionary<string, string> keys = new Dictionary<string, string>(PendingHoleKeys);
            PruneDestroyed();
            foreach (KeyValuePair<Sail, string> pair in HoleKeys)
            {
                string saveKey = SaveKey(pair.Key);
                if (saveKey != null)
                {
                    keys[saveKey] = pair.Value;
                }
            }
            return keys;
        }

        internal static void LoadInstalledHoleKeys(Dictionary<string, string> keys)
        {
            HoleKeys.Clear();
            PendingHoleKeys.Clear();
            if (keys == null)
            {
                return;
            }

            foreach (KeyValuePair<string, string> pair in keys)
            {
                PendingHoleKeys[pair.Key] = pair.Value;
            }
        }

        private static HarmonyMethod Hook(string methodName)
        {
            return new HarmonyMethod(typeof(SailDamageBridge), methodName);
        }

        private static string SaveKey(Sail sail)
        {
            if (saveKeyMethod == null || sail == null)
            {
                return null;
            }

            try
            {
                return saveKeyMethod.Invoke(null, new object[] { sail }) as string;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string HoleKeyFor(Sail sail)
        {
            Claim(sail);
            if (HoleKeys.TryGetValue(sail, out string key))
            {
                return key;
            }
            return SaveKey(sail) ?? sail.sailName;
        }

        private static void Claim(Sail sail)
        {
            if (sail == null || PendingHoleKeys.Count == 0 || HoleKeys.ContainsKey(sail))
            {
                return;
            }

            string saveKey = SaveKey(sail);
            if (saveKey != null && PendingHoleKeys.TryGetValue(saveKey, out string key))
            {
                PendingHoleKeys.Remove(saveKey);
                HoleKeys[sail] = key;
            }
        }

        private static void PruneDestroyed()
        {
            foreach (Sail sail in HoleKeys.Keys.Where(sail => sail == null).ToList())
            {
                HoleKeys.Remove(sail);
            }
        }

        // SailVisuals.Holes(Sail, ...): the hole pattern is being drawn.
        private static void HolesPrefix(Sail __0)
        {
            Claim(__0);
            drawingSail = __0;
        }

        private static Exception HolesFinalizer(Exception __exception)
        {
            drawingSail = null;
            return __exception;
        }

        // Only while the holes are drawn: Sail Damage's own save data keeps the
        // sail's real key.
        private static void SaveKeyPostfix(Sail __0, ref string __result)
        {
            if (drawingSail != null && __0 == drawingSail &&
                HoleKeys.TryGetValue(__0, out string key))
            {
                __result = key;
            }
        }

        private static void RebuildRecordPostfix(
            SaveableBoatCustomization __instance,
            SaveBoatCustomizationData __result)
        {
            PruneDestroyed();
            if (HoleKeys.Count == 0 || __result?.sails == null)
            {
                return;
            }

            BoatRefs boatRefs = __instance.GetComponent<BoatRefs>();
            if (boatRefs?.masts == null)
            {
                return;
            }

            bool[] matched = new bool[__result.sails.Count];
            foreach (Mast mast in boatRefs.masts)
            {
                if (mast == null || !mast.gameObject.activeSelf || mast.sails == null)
                {
                    continue;
                }

                foreach (GameObject sailObject in mast.sails)
                {
                    Sail sail = sailObject != null ? sailObject.GetComponent<Sail>() : null;
                    if (sail == null || !HoleKeys.TryGetValue(sail, out string key))
                    {
                        continue;
                    }

                    for (int i = 0; i < __result.sails.Count; i++)
                    {
                        SaveSailData data = __result.sails[i];
                        if (!matched[i] && SameSail(data, mast, sail))
                        {
                            matched[i] = true;
                            RebuildKeys.Remove(data);
                            RebuildKeys.Add(data, key);
                            break;
                        }
                    }
                }
            }
        }

        private static bool SameSail(SaveSailData data, Mast mast, Sail sail)
        {
            return data != null &&
                   data.mastIndex == mast.orderIndex &&
                   data.prefabIndex == sail.prefabIndex &&
                   Mathf.Abs(data.installHeight - sail.GetCurrentInstallHeight()) < 0.01f &&
                   Mathf.Abs(data.scaleY - sail.GetScaleY()) < 0.001f &&
                   Mathf.Abs(data.scaleZ - sail.GetScaleZ()) < 0.001f;
        }

        private static void LoadSailPrefix(SaveSailData sailData, out string __state)
        {
            __state = null;
            if (sailData != null)
            {
                RebuildKeys.TryGetValue(sailData, out __state);
            }
        }

        private static void LoadSailPostfix(Mast __instance, string __state)
        {
            if (__state == null || __instance.sails == null || __instance.sails.Count == 0)
            {
                return;
            }

            GameObject sailObject = __instance.sails[__instance.sails.Count - 1];
            Sail sail = sailObject != null ? sailObject.GetComponent<Sail>() : null;
            if (sail != null)
            {
                HoleKeys[sail] = __state;
            }
        }
    }

    // The Sail Damage data of sail packages and installed staysails, in its own
    // save key so the SSP3 package records stay readable by older releases.
    internal static class SailDamageSaveData
    {
        private const string SaveKey = "DogEggz.SailDamage";
        private const string Prefix = "SSD1:";
        private const int FormatVersion = 1;
        private const int MaximumEntryCount = 10000;

        internal static void Save(Dictionary<int, SailpackRecord> records)
        {
            try
            {
                List<KeyValuePair<int, SailpackRecord>> packages = records
                    .Where(pair => pair.Value != null &&
                                   (pair.Value.damage != null || pair.Value.holeKey != null))
                    .OrderBy(pair => pair.Key)
                    .ToList();
                Dictionary<string, string> installed = SailDamageBridge.GetInstalledHoleKeys();
                if (packages.Count == 0 && installed.Count == 0)
                {
                    GameState.modData?.Remove(SaveKey);
                    return;
                }

                string payload = Encode(packages, installed);
                // Verify the payload reads back before it replaces the old one.
                Decode(payload, out _, out _);
                if (GameState.modData == null)
                {
                    GameState.modData = new Dictionary<string, string>();
                }

                GameState.modData[SaveKey] = payload;
                SwappableStaysailPlugin.Log?.LogInfo(
                    $"Saved Sail Damage data of {packages.Count} sail package(s) " +
                    $"and {installed.Count} installed staysail(s).");
            }
            catch (Exception exception)
            {
                SwappableStaysailPlugin.Log?.LogError(
                    $"Could not save the Sail Damage data of sail packages: {exception}");
            }
        }

        internal static void Load(Dictionary<int, SailpackRecord> records)
        {
            SailDamageBridge.LoadInstalledHoleKeys(null);
            if (GameState.modData == null ||
                !GameState.modData.TryGetValue(SaveKey, out string payload) ||
                string.IsNullOrEmpty(payload))
            {
                return;
            }

            try
            {
                Decode(
                    payload,
                    out Dictionary<int, SailpackRecord> packages,
                    out Dictionary<string, string> installed);
                int applied = 0;
                foreach (KeyValuePair<int, SailpackRecord> pair in packages)
                {
                    if (records.TryGetValue(pair.Key, out SailpackRecord record))
                    {
                        record.damage = pair.Value.damage;
                        record.holeKey = pair.Value.holeKey;
                        applied++;
                    }
                }

                SailDamageBridge.LoadInstalledHoleKeys(installed);
                SwappableStaysailPlugin.Log?.LogInfo(
                    $"Loaded Sail Damage data of {applied} sail package(s) " +
                    $"and {installed.Count} installed staysail(s).");
            }
            catch (Exception exception)
            {
                SwappableStaysailPlugin.Log?.LogError(
                    $"Could not load the Sail Damage data of sail packages: {exception}");
            }
        }

        private static string Encode(
            List<KeyValuePair<int, SailpackRecord>> packages,
            Dictionary<string, string> installed)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                using (BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8, true))
                {
                    writer.Write(FormatVersion);
                    writer.Write(packages.Count);
                    foreach (KeyValuePair<int, SailpackRecord> pair in packages)
                    {
                        float[] damage = pair.Value.damage;
                        writer.Write(pair.Key);
                        writer.Write(damage != null ? damage.Length : 0);
                        if (damage != null)
                        {
                            foreach (float value in damage)
                            {
                                writer.Write(value);
                            }
                        }
                        writer.Write(pair.Value.holeKey != null);
                        writer.Write(pair.Value.holeKey ?? string.Empty);
                    }

                    writer.Write(installed.Count);
                    foreach (KeyValuePair<string, string> pair in installed)
                    {
                        writer.Write(pair.Key);
                        writer.Write(pair.Value ?? string.Empty);
                    }
                }
                return Prefix + Convert.ToBase64String(stream.ToArray());
            }
        }

        private static void Decode(
            string payload,
            out Dictionary<int, SailpackRecord> packages,
            out Dictionary<string, string> installed)
        {
            if (payload == null || !payload.StartsWith(Prefix, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Sail Damage data does not use the SSD1 format.");
            }

            using (MemoryStream stream = new MemoryStream(
                       Convert.FromBase64String(payload.Substring(Prefix.Length)),
                       false))
            using (BinaryReader reader = new BinaryReader(stream, Encoding.UTF8, true))
            {
                int version = reader.ReadInt32();
                if (version != FormatVersion)
                {
                    throw new InvalidDataException(
                        $"Unsupported Sail Damage data version {version}.");
                }

                int packageCount = ReadCount(reader);
                packages = new Dictionary<int, SailpackRecord>(packageCount);
                for (int i = 0; i < packageCount; i++)
                {
                    int instanceId = reader.ReadInt32();
                    int damageLength = reader.ReadInt32();
                    if (instanceId <= 0 || packages.ContainsKey(instanceId) ||
                        (damageLength != 0 &&
                         damageLength != SailpackRecord.DamageValueCount))
                    {
                        throw new InvalidDataException(
                            $"Invalid Sail Damage entry for sail package {instanceId}.");
                    }

                    float[] damage = null;
                    if (damageLength > 0)
                    {
                        damage = new float[damageLength];
                        for (int j = 0; j < damageLength; j++)
                        {
                            float value = reader.ReadSingle();
                            damage[j] = float.IsNaN(value) || float.IsInfinity(value)
                                ? 0f
                                : Mathf.Clamp01(value);
                        }
                    }
                    bool hasHoleKey = reader.ReadBoolean();
                    string holeKey = reader.ReadString();
                    packages.Add(instanceId, new SailpackRecord
                    {
                        damage = damage,
                        holeKey = hasHoleKey ? holeKey : null
                    });
                }

                int installedCount = ReadCount(reader);
                installed = new Dictionary<string, string>(installedCount);
                for (int i = 0; i < installedCount; i++)
                {
                    installed[reader.ReadString()] = reader.ReadString();
                }

                if (stream.Position != stream.Length)
                {
                    throw new InvalidDataException(
                        "Sail Damage data contains unexpected trailing data.");
                }
            }
        }

        private static int ReadCount(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > MaximumEntryCount)
            {
                throw new InvalidDataException($"Invalid Sail Damage entry count {count}.");
            }
            return count;
        }
    }
}
