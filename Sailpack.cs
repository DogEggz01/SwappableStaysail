using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace SwappableStaysail
{
    [HarmonyPatch(typeof(PrefabsDirectory), "Start")]
    internal static class PrefabsDirectoryPatch
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        private static void Prefix(PrefabsDirectory __instance)
        {
            SailpackFactory.EnsureRegistered(__instance);
        }

        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(PrefabsDirectory __instance)
        {
            SailpackFactory.PopulateItemCache(__instance);
        }
    }

    [HarmonyPatch(typeof(SaveablePrefab), nameof(SaveablePrefab.Load))]
    internal static class ActivateLoadedSailPackagePatch
    {
        [HarmonyPostfix]
        private static void Postfix(SaveablePrefab __instance)
        {
            if (__instance == null || __instance.gameObject.activeSelf ||
                __instance.GetComponent<SailpackData>() == null)
            {
                return;
            }

            // Sail package directory entries are inactive runtime templates.
            // Vanilla save loading and BoatLocalItems clone those templates
            // without activating the resulting item.
            __instance.gameObject.SetActive(true);
            SwappableStaysailPlugin.Log?.LogInfo(
                $"Activated loaded sail package {__instance.instanceId} " +
                $"from prefab {__instance.prefabIndex}.");
        }
    }

    [HarmonyPatch(typeof(SaveLoadManager), nameof(SaveLoadManager.SaveModData))]
    internal static class SaveModDataPatch
    {
        [HarmonyPrefix]
        private static void Prefix()
        {
            SailpackPersistence.Save();
        }
    }

    [HarmonyPatch(typeof(SaveLoadManager), nameof(SaveLoadManager.LoadModData))]
    internal static class LoadModDataPatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            SailpackPersistence.Load();
        }
    }

    [HarmonyPatch(typeof(ShipItem), nameof(ShipItem.UpdateLookText))]
    internal static class SailpackLookTextPatch
    {
        [HarmonyPostfix]
        private static void Postfix(ShipItem __instance)
        {
            SailpackData data = __instance.GetComponent<SailpackData>();
            if (data != null && data.HasRecord)
            {
                __instance.lookText = data.Record.GetDisplayName();
            }
        }
    }

    [HarmonyPatch(
        typeof(PickupableItemCollisionChecker),
        nameof(PickupableItemCollisionChecker.GetDecollision))]
    internal static class SailPackageBigItemDecollisionPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(
            ShipItem ___item,
            ref Vector3 __result)
        {
            SailpackData data = ___item != null
                ? ___item.GetComponent<SailpackData>()
                : null;
            if (data == null || !data.SuppressBigItemDecollision)
            {
                return true;
            }

            __result = Vector3.zero;
            return false;
        }
    }

    [HarmonyPatch(typeof(GoPointer), nameof(GoPointer.PickUpItem))]
    internal static class SailPackageHeldLayerPatch
    {
        [HarmonyPostfix]
        private static void Postfix(PickupableItem item)
        {
            SailpackData data = item != null
                ? item.GetComponent<SailpackData>()
                : null;
            if (data == null)
            {
                return;
            }

            foreach (Transform child in
                     data.GetComponentsInChildren<Transform>(true))
            {
                child.gameObject.layer = 2;
            }

            ShipItem shipItem = item as ShipItem;
            if (shipItem?.itemRigidbodyC != null)
            {
                shipItem.itemRigidbodyC.gameObject.layer = 2;
            }
        }
    }

    [HarmonyPatch(typeof(GoPointer), nameof(GoPointer.DropItem))]
    internal static class SailPackageDroppedLayerPatch
    {
        [HarmonyPrefix]
        private static void Prefix(
            GoPointer __instance,
            out SailpackData __state)
        {
            PickupableItem held = __instance.GetHeldItem();
            __state = held != null
                ? held.GetComponent<SailpackData>()
                : null;
        }

        [HarmonyPostfix]
        private static void Postfix(SailpackData __state)
        {
            if (__state == null)
            {
                return;
            }

            foreach (Transform child in
                     __state.GetComponentsInChildren<Transform>(true))
            {
                child.gameObject.layer = 0;
            }

            ShipItem shipItem = __state.GetComponent<ShipItem>();
            if (shipItem?.itemRigidbodyC != null)
            {
                // The solid physics proxy has no GoPointerButton. Vanilla keeps
                // it on Ignore Raycast so it cannot hide the package's root
                // interaction collider from the pointer.
                shipItem.itemRigidbodyC.gameObject.layer = 2;
            }
        }
    }

    internal static class SailpackFactory
    {
        internal const int StandardDonorPrefabIndex = 219;
        internal const int SmallDonorPrefabIndex = 221;
        internal const int LegacyPrefabIndex = 390;
        internal const int StandardPrefabIndex = 600;
        internal const int SmallPrefabIndex = 601;
        internal const float SmallPackageMaximumMass = 25f;

        private static GameObject legacyPrefab;
        private static GameObject standardPrefab;
        private static GameObject smallPrefab;
        private static bool registrationLogged;

        internal static bool EnsureRegistered(PrefabsDirectory directory)
        {
            if (directory == null || directory.directory == null)
            {
                SwappableStaysailPlugin.Log?.LogError(
                    "Cannot register sail packages: the prefab directory is unavailable.");
                return false;
            }

            if (!HasDonor(directory, StandardDonorPrefabIndex, "standard") ||
                !HasDonor(directory, SmallDonorPrefabIndex, "small"))
            {
                return false;
            }

            if (directory.directory.Length <= SmallPrefabIndex)
            {
                Array.Resize(ref directory.directory, SmallPrefabIndex + 1);
            }

            if (!CanClaimSlot(directory, LegacyPrefabIndex, legacyPrefab) ||
                !CanClaimSlot(directory, StandardPrefabIndex, standardPrefab) ||
                !CanClaimSlot(directory, SmallPrefabIndex, smallPrefab))
            {
                return false;
            }

            if (legacyPrefab == null)
            {
                legacyPrefab = BuildPrefab(
                    directory.directory[StandardDonorPrefabIndex],
                    LegacyPrefabIndex);
            }
            if (standardPrefab == null)
            {
                standardPrefab = BuildPrefab(
                    directory.directory[StandardDonorPrefabIndex],
                    StandardPrefabIndex);
            }
            if (smallPrefab == null)
            {
                smallPrefab = BuildPrefab(
                    directory.directory[SmallDonorPrefabIndex],
                    SmallPrefabIndex);
            }

            directory.directory[LegacyPrefabIndex] = legacyPrefab;
            directory.directory[StandardPrefabIndex] = standardPrefab;
            directory.directory[SmallPrefabIndex] = smallPrefab;
            if (!registrationLogged)
            {
                registrationLogged = true;
                SwappableStaysailPlugin.Log?.LogInfo(
                    "Registered sail package prefabs: " +
                    $"legacy loader={LegacyPrefabIndex}, " +
                    $"standard={StandardPrefabIndex} (donor {StandardDonorPrefabIndex}), " +
                    $"small={SmallPrefabIndex} (donor {SmallDonorPrefabIndex}).");
            }
            return true;
        }

        internal static void PopulateItemCache(PrefabsDirectory directory)
        {
            if (!EnsureRegistered(directory))
            {
                return;
            }
            if (directory.shipItems == null)
            {
                directory.shipItems = new ShipItem[directory.directory.Length];
            }
            else if (directory.shipItems.Length < directory.directory.Length)
            {
                Array.Resize(ref directory.shipItems, directory.directory.Length);
            }
            directory.shipItems[LegacyPrefabIndex] =
                legacyPrefab.GetComponent<ShipItem>();
            directory.shipItems[StandardPrefabIndex] =
                standardPrefab.GetComponent<ShipItem>();
            directory.shipItems[SmallPrefabIndex] =
                smallPrefab.GetComponent<ShipItem>();
        }

        internal static SailpackData CreateStaged(
            SailpackRecord record,
            Vector3 position,
            Quaternion rotation,
            bool registerToSave,
            Mast runtimeTarget = null)
        {
            if (record == null || PrefabsDirectory.instance == null ||
                !EnsureRegistered(PrefabsDirectory.instance))
            {
                return null;
            }

            GameObject instance = null;
            SaveablePrefab saveable = null;
            try
            {
                GameObject selectedPrefab = UsesSmallPackage(record.packageMass)
                    ? smallPrefab
                    : standardPrefab;
                instance = UnityEngine.Object.Instantiate(
                    selectedPrefab,
                    position,
                    rotation);
                instance.SetActive(false);
                SailpackData data = instance.GetComponent<SailpackData>();
                data.Initialize(record, runtimeTarget);

                if (registerToSave)
                {
                    saveable = instance.GetComponent<SaveablePrefab>();
                    saveable.RegisterToSave();
                    SailpackPersistence.Register(saveable.instanceId, record);
                }
                return data;
            }
            catch (Exception exception)
            {
                if (saveable != null && saveable.instanceId > 0)
                {
                    SailpackPersistence.Remove(saveable.instanceId);
                    TryUnregister(saveable, "staging failure");
                }
                if (instance != null)
                {
                    UnityEngine.Object.Destroy(instance);
                }
                SwappableStaysailPlugin.Log?.LogError(
                    $"Could not stage a sail package: {exception}");
                return null;
            }
        }

        internal static void ActivateStaged(SailpackData data)
        {
            if (data != null)
            {
                data.gameObject.SetActive(true);
            }
        }

        internal static void DiscardStaged(
            SailpackData data,
            bool unregisterFromSave)
        {
            if (data == null)
            {
                return;
            }
            if (unregisterFromSave)
            {
                SaveablePrefab saveable = data.GetComponent<SaveablePrefab>();
                if (saveable != null && saveable.instanceId > 0)
                {
                    SailpackPersistence.Remove(saveable.instanceId);
                    TryUnregister(saveable, "staged-package rollback");
                }
            }
            UnityEngine.Object.Destroy(data.gameObject);
        }

        private static void TryUnregister(
            SaveablePrefab saveable,
            string context)
        {
            try
            {
                saveable.Unregister();
            }
            catch (Exception exception)
            {
                SwappableStaysailPlugin.Log?.LogWarning(
                    $"Could not unregister a sail package during {context}: " +
                    exception);
            }
        }

        internal static void Dispose()
        {
            PrefabsDirectory directory = PrefabsDirectory.instance;
            if (directory != null)
            {
                UnregisterPrefab(directory, LegacyPrefabIndex, legacyPrefab);
                UnregisterPrefab(directory, StandardPrefabIndex, standardPrefab);
                UnregisterPrefab(directory, SmallPrefabIndex, smallPrefab);
            }
            DestroyPrefab(legacyPrefab);
            DestroyPrefab(standardPrefab);
            DestroyPrefab(smallPrefab);
            legacyPrefab = null;
            standardPrefab = null;
            smallPrefab = null;
            registrationLogged = false;
        }

        private static bool HasDonor(
            PrefabsDirectory directory,
            int donorIndex,
            string packageKind)
        {
            if (directory.directory.Length > donorIndex &&
                directory.directory[donorIndex] != null)
            {
                return true;
            }

            SwappableStaysailPlugin.Log?.LogError(
                $"Cannot register sail packages: {packageKind} package donor " +
                $"{donorIndex} is unavailable.");
            return false;
        }

        private static bool CanClaimSlot(
            PrefabsDirectory directory,
            int prefabIndex,
            GameObject ownedPrefab)
        {
            GameObject occupied = directory.directory[prefabIndex];
            if (occupied == null || occupied == ownedPrefab ||
                occupied.GetComponent<SailpackData>() != null)
            {
                return true;
            }

            SwappableStaysailPlugin.Log?.LogError(
                $"Cannot register sail package: prefab index {prefabIndex} is " +
                $"occupied by '{occupied.name}'.");
            return false;
        }

        private static bool UsesSmallPackage(float packageMass)
        {
            return !float.IsNaN(packageMass) &&
                   !float.IsInfinity(packageMass) &&
                   packageMass <= SmallPackageMaximumMass;
        }

        private static void UnregisterPrefab(
            PrefabsDirectory directory,
            int prefabIndex,
            GameObject registeredPrefab)
        {
            if (registeredPrefab == null)
            {
                return;
            }

            if (directory.directory != null &&
                directory.directory.Length > prefabIndex &&
                directory.directory[prefabIndex] == registeredPrefab)
            {
                directory.directory[prefabIndex] = null;
            }
            if (directory.shipItems != null &&
                directory.shipItems.Length > prefabIndex &&
                directory.shipItems[prefabIndex] ==
                registeredPrefab.GetComponent<ShipItem>())
            {
                directory.shipItems[prefabIndex] = null;
            }
        }

        private static void DestroyPrefab(GameObject registeredPrefab)
        {
            if (registeredPrefab != null)
            {
                UnityEngine.Object.Destroy(registeredPrefab);
            }
        }

        private static GameObject BuildPrefab(GameObject donor, int prefabIndex)
        {
            GameObject clone = UnityEngine.Object.Instantiate(donor);
            clone.SetActive(false);
            clone.name = "sail package";
            clone.transform.localScale = Vector3.one;

            if (prefabIndex == SmallPrefabIndex)
            {
                Transform label = clone.transform.Find("label");
                if (label != null)
                {
                    UnityEngine.Object.DestroyImmediate(label.gameObject);
                }
            }

            SaveablePrefab saveable = clone.GetComponent<SaveablePrefab>();
            ShipItem item = clone.GetComponent<ShipItem>();
            saveable.prefabIndex = prefabIndex;
            item.name = "sail package";
            item.lookText = "sail package";
            item.mass = SailpackWeight.MinimumPackageMass;
            // Preserve vanilla package/cargo behavior. Automatic uninstall
            // handoff stabilizes only the first moments of big-item pickup.
            item.big = true;
            item.holdDistance = 1.25f;
            item.holdHeight = 0f;
            item.heldRotationOffset = 180f;
            item.wallAttachment = false;
            item.sold = true;
            item.value = 0;
            clone.tag = "Untagged";

            Good good = clone.GetComponent<Good>();
            if (good != null)
            {
                UnityEngine.Object.DestroyImmediate(good);
            }

            if (item.itemRigidbodyC != null)
            {
                // ShipItem.Awake creates a proxy before this runtime directory
                // template can be disabled. Real instances create their own proxy.
                UnityEngine.Object.DestroyImmediate(item.itemRigidbodyC.gameObject);
                item.itemRigidbodyC = null;
                item.colChecker = null;
            }

            foreach (LODGroup lod in clone.GetComponents<LODGroup>())
            {
                UnityEngine.Object.DestroyImmediate(lod);
            }

            clone.AddComponent<SailpackData>();
            UnityEngine.Object.DontDestroyOnLoad(clone);
            return clone;
        }
    }

    internal sealed class SailpackData : MonoBehaviour
    {
        private SailpackRecord record;
        private Mast runtimeTarget;
        internal bool HasRecord => record != null;
        internal SailpackRecord Record => record;
        internal Mast RuntimeTarget => runtimeTarget;
        internal bool SuppressBigItemDecollision { get; set; }

        internal void Initialize(
            SailpackRecord newRecord,
            Mast newRuntimeTarget = null)
        {
            record = newRecord?.Copy();
            record?.Normalize();
            runtimeTarget = newRuntimeTarget;
            ApplyRecord();
        }

        internal void BindTarget(Mast target)
        {
            runtimeTarget = target;
        }

        private IEnumerator Start()
        {
            if (record != null)
            {
                yield break;
            }

            // SaveLoadManager loads prefab instances before mod metadata, but
            // marks itself loaded immediately after LoadModData completes.
            while (SaveLoadManager.instance != null &&
                   !SaveLoadManager.instance.loaded)
            {
                yield return new WaitForEndOfFrame();
            }
            TryHydrate();
        }

        private void TryHydrate()
        {
            SaveablePrefab saveable = GetComponent<SaveablePrefab>();
            if (saveable == null || saveable.instanceId <= 0)
            {
                return;
            }

            if (SailpackPersistence.TryGet(saveable.instanceId, out SailpackRecord saved))
            {
                record = saved.Copy();
                record.Normalize();
                ApplyRecord();
            }
            else if (SaveLoadManager.instance?.loaded == true)
            {
                SwappableStaysailPlugin.Log?.LogWarning(
                    $"Sail package {saveable.instanceId} has no mod metadata.");
            }
        }

        private void ApplyRecord()
        {
            if (record == null)
            {
                return;
            }

            MigrateLegacyPrefabIndex();

            string displayName = record.GetDisplayName();
            gameObject.name = displayName;
            ShipItem item = GetComponent<ShipItem>();
            if (item != null)
            {
                item.name = displayName;
                item.lookText = displayName;
                item.mass = record.packageMass;
                if (item.itemRigidbodyC != null &&
                    item.itemRigidbodyC.GetBody() != null)
                {
                    item.itemRigidbodyC.UpdateMass();
                    item.itemRigidbodyC.GetBody().angularDrag =
                        item.mass * 0.1f;
                }
            }
        }

        private void MigrateLegacyPrefabIndex()
        {
            SaveablePrefab saveable = GetComponent<SaveablePrefab>();
            if (saveable == null ||
                saveable.prefabIndex != SailpackFactory.LegacyPrefabIndex)
            {
                return;
            }

            saveable.prefabIndex = SailpackFactory.StandardPrefabIndex;
            SwappableStaysailPlugin.Log?.LogInfo(
                $"Migrated sail package {saveable.instanceId} from prefab index " +
                $"{SailpackFactory.LegacyPrefabIndex} to " +
                $"{SailpackFactory.StandardPrefabIndex}.");
        }

        internal GameObject GetSailPrefab()
        {
            if (record == null || PrefabsDirectory.instance == null ||
                PrefabsDirectory.instance.sails == null ||
                record.sailPrefabIndex < 0 ||
                record.sailPrefabIndex >= PrefabsDirectory.instance.sails.Length)
            {
                return null;
            }
            return PrefabsDirectory.instance.sails[record.sailPrefabIndex];
        }
    }

    internal static class SailpackPersistence
    {
        private static readonly Dictionary<int, SailpackRecord> Records =
            new Dictionary<int, SailpackRecord>();

        internal static bool TryGet(int instanceId, out SailpackRecord record)
        {
            return Records.TryGetValue(instanceId, out record);
        }

        internal static void Register(int instanceId, SailpackRecord record)
        {
            if (instanceId > 0 && record != null)
            {
                record.Normalize();
                Records[instanceId] = record.Copy();
            }
        }

        internal static void Remove(int instanceId)
        {
            if (instanceId > 0)
            {
                Records.Remove(instanceId);
            }
        }

        internal static void Load()
        {
            Records.Clear();
            if (GameState.modData == null ||
                !GameState.modData.TryGetValue(
                    SwappableStaysailPlugin.SaveDataKey,
                    out string payload) ||
                string.IsNullOrEmpty(payload))
            {
                return;
            }

            try
            {
                Dictionary<int, SailpackRecord> loaded =
                    SailpackPersistenceCodec.Decode(payload);
                foreach (KeyValuePair<int, SailpackRecord> pair in loaded)
                {
                    Records[pair.Key] = pair.Value.Copy();
                }

                SwappableStaysailPlugin.Log?.LogInfo(
                    $"Loaded {Records.Count} sail package record(s).");
            }
            catch (Exception exception)
            {
                SwappableStaysailPlugin.Log?.LogError(
                    $"Could not load sail package data: {exception}");
            }
        }

        internal static void Dispose()
        {
            Records.Clear();
        }

        internal static void Save()
        {
            try
            {
                ReconcileLiveSailpacks();
                Dictionary<int, SailpackRecord> snapshot = Records.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Copy());
                string payload = SailpackPersistenceCodec.Encode(snapshot);
                Dictionary<int, SailpackRecord> verified =
                    SailpackPersistenceCodec.Decode(payload);
                if (verified.Count != snapshot.Count ||
                    !verified.Keys.OrderBy(id => id)
                        .SequenceEqual(snapshot.Keys.OrderBy(id => id)))
                {
                    throw new InvalidDataException(
                        "Sail package metadata failed its save-time verification.");
                }

                if (GameState.modData == null)
                {
                    GameState.modData = new Dictionary<string, string>();
                }

                GameState.modData[SwappableStaysailPlugin.SaveDataKey] =
                    payload;
                SwappableStaysailPlugin.Log?.LogInfo(
                    $"Saved {snapshot.Count} sail package record(s).");
            }
            catch (Exception exception)
            {
                SwappableStaysailPlugin.Log?.LogError(
                    $"Could not save sail package data: {exception}");
            }
        }

        private static void ReconcileLiveSailpacks()
        {
            SailpackData[] live =
                Resources.FindObjectsOfTypeAll<SailpackData>();
            HashSet<int> liveIds = new HashSet<int>();
            foreach (SailpackData data in live)
            {
                SaveablePrefab saveable = data.GetComponent<SaveablePrefab>();
                if (saveable == null || saveable.instanceId <= 0 || !data.HasRecord)
                {
                    continue;
                }

                liveIds.Add(saveable.instanceId);
                Records[saveable.instanceId] = data.Record.Copy();
            }

            // Keep metadata for items cached inside unloaded boats. Only remove an
            // entry when vanilla's live saveable list proves the ID is gone.
            SaveLoadManager manager = SaveLoadManager.instance;
            if (manager == null)
            {
                return;
            }

            List<SaveablePrefab> currentPrefabs = manager.GetCurrentPrefabs();
            if (currentPrefabs != null)
            {
                foreach (SaveablePrefab saveable in currentPrefabs)
                {
                    if (saveable != null && saveable.instanceId > 0)
                    {
                        liveIds.Add(saveable.instanceId);
                    }
                }
            }

            foreach (SaveableObject saveableObject in
                     Resources.FindObjectsOfTypeAll<SaveableObject>())
            {
                if (saveableObject?.localItems == null)
                {
                    continue;
                }
                List<SavePrefabData> cachedItems =
                    saveableObject.localItems.GetCachedItems();
                if (cachedItems == null)
                {
                    continue;
                }
                foreach (SavePrefabData cached in cachedItems)
                {
                    if (cached != null && cached.instanceId > 0)
                    {
                        liveIds.Add(cached.instanceId);
                    }
                }
            }

            List<int> stale = Records.Keys
                .Where(id => !liveIds.Contains(id))
                .ToList();
            foreach (int id in stale)
            {
                Records.Remove(id);
            }
        }

    }

    internal static class SailpackPersistenceCodec
    {
        private const string Prefix = "SSP3:";
        private const int FormatVersion = 3;
        private const int MaximumRecordCount = 10000;
        private const int MaximumEncodedLength = 16 * 1024 * 1024;

        private static bool IsCurrentFormat(string payload)
        {
            return payload != null &&
                   payload.StartsWith(Prefix, StringComparison.Ordinal);
        }

        internal static string Encode(
            IEnumerable<KeyValuePair<int, SailpackRecord>> records)
        {
            List<KeyValuePair<int, SailpackRecord>> validRecords =
                (records ?? Enumerable.Empty<KeyValuePair<int, SailpackRecord>>())
                .Where(pair => pair.Key > 0 && pair.Value != null)
                .OrderBy(pair => pair.Key)
                .ToList();
            if (validRecords.Count > MaximumRecordCount)
            {
                throw new InvalidDataException(
                    $"Sail package record count {validRecords.Count} exceeds " +
                    $"the supported maximum of {MaximumRecordCount}.");
            }

            using (MemoryStream stream = new MemoryStream())
            {
                using (BinaryWriter writer = new BinaryWriter(
                           stream,
                           Encoding.UTF8,
                           true))
                {
                    writer.Write(FormatVersion);
                    writer.Write(validRecords.Count);
                    foreach (KeyValuePair<int, SailpackRecord> pair in validRecords)
                    {
                        WriteRecord(writer, pair.Key, pair.Value);
                    }
                }

                return Prefix + Convert.ToBase64String(stream.ToArray());
            }
        }

        internal static Dictionary<int, SailpackRecord> Decode(string payload)
        {
            if (!IsCurrentFormat(payload))
            {
                throw new InvalidDataException(
                    "Sail package metadata does not use the current format.");
            }
            if (payload.Length > MaximumEncodedLength)
            {
                throw new InvalidDataException(
                    "Sail package metadata exceeds the supported size.");
            }

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(payload.Substring(Prefix.Length));
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException(
                    "Sail package metadata contains invalid base64 data.",
                    exception);
            }

            using (MemoryStream stream = new MemoryStream(bytes, false))
            using (BinaryReader reader = new BinaryReader(
                       stream,
                       Encoding.UTF8,
                       true))
            {
                int version = reader.ReadInt32();
                if (version != FormatVersion)
                {
                    throw new InvalidDataException(
                        $"Unsupported sail package metadata version {version}.");
                }

                int count = reader.ReadInt32();
                if (count < 0 || count > MaximumRecordCount)
                {
                    throw new InvalidDataException(
                        $"Invalid sail package record count {count}.");
                }

                Dictionary<int, SailpackRecord> decoded =
                    new Dictionary<int, SailpackRecord>(count);
                for (int i = 0; i < count; i++)
                {
                    int instanceId = reader.ReadInt32();
                    if (instanceId <= 0 || decoded.ContainsKey(instanceId))
                    {
                        throw new InvalidDataException(
                            $"Invalid or duplicate sail package ID {instanceId}.");
                    }

                    SailpackRecord record = ReadRecord(reader);
                    record.Normalize();
                    decoded.Add(instanceId, record);
                }

                if (stream.Position != stream.Length)
                {
                    throw new InvalidDataException(
                        "Sail package metadata contains unexpected trailing data.");
                }
                return decoded;
            }
        }

        private static void WriteRecord(
            BinaryWriter writer,
            int instanceId,
            SailpackRecord record)
        {
            record.Normalize();
            writer.Write(instanceId);
            writer.Write(record.sailPrefabIndex);
            writer.Write(record.sailColor);
            writer.Write(record.scaleY);
            writer.Write(record.scaleZ);
            writer.Write(record.packageMass);
            writer.Write(record.installHeight);
            writer.Write(record.minAngle);
            writer.Write(record.maxAngle);
            writer.Write(record.targetBoatSceneIndex);
            writer.Write(record.targetMastOrderIndex);
            writer.Write(record.targetStayName ?? string.Empty);
            writer.Write(record.sailName ?? string.Empty);
        }

        private static SailpackRecord ReadRecord(BinaryReader reader)
        {
            return new SailpackRecord
            {
                sailPrefabIndex = reader.ReadInt32(),
                sailColor = reader.ReadInt32(),
                scaleY = reader.ReadSingle(),
                scaleZ = reader.ReadSingle(),
                packageMass = reader.ReadSingle(),
                installHeight = reader.ReadSingle(),
                minAngle = reader.ReadSingle(),
                maxAngle = reader.ReadSingle(),
                targetBoatSceneIndex = reader.ReadInt32(),
                targetMastOrderIndex = reader.ReadInt32(),
                targetStayName = reader.ReadString(),
                sailName = reader.ReadString()
            };
        }
    }

    internal sealed class SailpackRecord
    {
        public int sailPrefabIndex;
        public int sailColor;
        public float scaleY = 1f;
        public float scaleZ = 1f;
        public float packageMass = SailpackWeight.MinimumPackageMass;
        public float installHeight;
        public float minAngle;
        public float maxAngle;
        public int targetBoatSceneIndex;
        public int targetMastOrderIndex;
        public string targetStayName;
        public string sailName;

        internal static SailpackRecord FromSail(Sail sail, Mast mast)
        {
            return new SailpackRecord
            {
                sailPrefabIndex = sail.prefabIndex,
                sailColor = sail.activeColor,
                scaleY = sail.GetScaleY(),
                scaleZ = sail.GetScaleZ(),
                packageMass = SailpackWeight.FromSail(sail),
                installHeight = sail.GetCurrentInstallHeight(),
                minAngle = sail.minAngle,
                maxAngle = sail.maxAngle,
                targetBoatSceneIndex = StaysailTargets.GetBoatSceneIndex(mast),
                targetMastOrderIndex = mast.orderIndex,
                targetStayName = StaysailTargets.GetStayName(mast),
                sailName = string.IsNullOrEmpty(sail.sailName)
                    ? sail.gameObject.name
                    : sail.sailName
            };
        }

        internal string GetDisplayName()
        {
            string stay = string.IsNullOrEmpty(targetStayName)
                ? "Staysail stay"
                : targetStayName;
            string sail = string.IsNullOrEmpty(sailName)
                ? "Staysail"
                : sailName;
            if (HasScalePercentageSuffix(sail))
            {
                return $"{stay} {sail}";
            }

            int scaleYPercentage = Mathf.RoundToInt(scaleY * 100f);
            int scaleZPercentage = Mathf.RoundToInt(scaleZ * 100f);
            return $"{stay} {sail} " +
                   $"({scaleYPercentage}%x{scaleZPercentage}%)";
        }

        private static bool HasScalePercentageSuffix(string value)
        {
            if (string.IsNullOrEmpty(value) || value[value.Length - 1] != ')')
            {
                return false;
            }

            int opening = value.LastIndexOf(" (", StringComparison.Ordinal);
            if (opening < 0)
            {
                return false;
            }

            string scale = value.Substring(
                opening + 2,
                value.Length - opening - 3);
            int separator = scale.IndexOf('x');
            if (separator < 0)
            {
                return IsWholePercentage(scale);
            }
            if (scale.IndexOf('x', separator + 1) >= 0)
            {
                return false;
            }

            return IsWholePercentage(scale.Substring(0, separator)) &&
                   IsWholePercentage(scale.Substring(separator + 1));
        }

        private static bool IsWholePercentage(string value)
        {
            if (string.IsNullOrEmpty(value) ||
                value[value.Length - 1] != '%')
            {
                return false;
            }

            return int.TryParse(
                value.Substring(0, value.Length - 1),
                out int percentage) &&
                percentage >= 0;
        }

        internal SailpackRecord Copy()
        {
            return (SailpackRecord)MemberwiseClone();
        }

        internal void Normalize()
        {
            if (float.IsNaN(packageMass) || float.IsInfinity(packageMass) ||
                packageMass <= 0f)
            {
                packageMass = SailpackWeight.MinimumPackageMass;
            }
        }
    }

    internal static class SailpackWeight
    {
        internal const float MinimumPackageMass = 0.1f;

        internal static float FromSail(Sail sail)
        {
            if (sail == null)
            {
                return MinimumPackageMass;
            }

            // Match NAND Tweaks' shipyard weight calculation. For staysails the
            // displayed weight is GetRealSailPower() * 20. Capturing it now keeps
            // the package stable even if sail balance settings change later.
            float baseMass = Mathf.Max(0f, sail.GetRealSailPower() * 20f);
            float categoryMass;
            if (sail.category == SailCategory.junk ||
                sail.category == SailCategory.gaff)
            {
                categoryMass = baseMass;
            }
            else if (sail.category == SailCategory.staysail)
            {
                categoryMass = 0f;
            }
            else
            {
                categoryMass = baseMass * 0.5f;
            }
            return Mathf.Max(MinimumPackageMass, baseMass + categoryMass);
        }
    }

    internal static class SailpackPickupHandoff
    {
        internal static Vector3 GetHeldPosition(
            GoPointer pointer,
            float distance,
            float height)
        {
            return pointer.transform.position +
                   pointer.transform.forward * distance +
                   pointer.transform.up * height;
        }

        internal static Quaternion GetHeldRotation(
            GoPointer pointer,
            float rotationOffset)
        {
            return pointer.transform.rotation *
                   Quaternion.Euler(rotationOffset, 0f, 0f);
        }

        internal static void Schedule(
            GoPointer pointer,
            ShipItem item,
            SailpackRecord record)
        {
            SwappableStaysailPlugin runner = SwappableStaysailPlugin.Instance;
            if (runner == null || !runner.isActiveAndEnabled)
            {
                TryPickUp(pointer, item, record);
                SailpackData data = item != null
                    ? item.GetComponent<SailpackData>()
                    : null;
                if (data != null)
                {
                    data.SuppressBigItemDecollision = false;
                }
                return;
            }
            runner.StartCoroutine(PickUpAfterInitialization(pointer, item, record));
        }

        private static IEnumerator PickUpAfterInitialization(
            GoPointer pointer,
            ShipItem item,
            SailpackRecord record)
        {
            // Let the cloned ShipItem finish Awake/LoadAfterDelay and let the
            // removal input's current GoPointer.LateUpdate complete before the
            // package enters the held slot.
            yield return new WaitForEndOfFrame();
            const int initializationFrameLimit = 8;
            for (int frame = 0;
                 frame < initializationFrameLimit &&
                 item != null &&
                 (item.itemRigidbodyC == null || item.colChecker == null);
                 frame++)
            {
                yield return new WaitForEndOfFrame();
            }
            if (!TryPickUp(pointer, item, record))
            {
                yield break;
            }

            // Big packages use vanilla de-collision after a short, physics-based
            // settling window. This prevents stale spawn contacts from moving a
            // newly handed-off package onto the deck while preserving normal big
            // item behavior after the handoff has stabilized.
            yield return StabilizeBigItem(pointer, item);
            GoPointer holder = item != null ? item.held : null;
            if (item == null || holder == null ||
                holder.GetHeldItem() != item)
            {
                LeaveAccessible(pointer, item, record,
                    "the held state did not persist");
                yield break;
            }

            SwappableStaysailPlugin.Log?.LogInfo(
                $"Sail package is held after removing " +
                $"{record?.GetDisplayName() ?? "a staysail"}.");
        }

        private static bool TryPickUp(
            GoPointer pointer,
            ShipItem item,
            SailpackRecord record)
        {
            if (pointer == null || item == null || !item.gameObject.activeInHierarchy)
            {
                LeaveAccessible(pointer, item, record,
                    "the player pointer or package was unavailable");
                return false;
            }
            if (pointer.GetHeldItem() != null)
            {
                LeaveAccessible(pointer, item, record,
                    "the player was already holding another item");
                return false;
            }
            if (item.itemRigidbodyC == null || item.colChecker == null)
            {
                LeaveAccessible(pointer, item, record,
                    "the package collision components did not initialize");
                return false;
            }

            PrepareBigItemForPickup(pointer, item);
            pointer.PickUpItem(item);
            if (pointer.GetHeldItem() == item && item.held == pointer)
            {
                return true;
            }

            LeaveAccessible(pointer, item, record,
                "GoPointer.PickUpItem did not retain the package");
            return false;
        }

        private static void PrepareBigItemForPickup(
            GoPointer pointer,
            ShipItem item)
        {
            PositionForPickup(pointer, item);
            item.colChecker.Init(item);
            item.colChecker.collisions = 0;
            item.colChecker.allowObstructedDropping = true;
            SailpackData data = item.GetComponent<SailpackData>();
            if (data != null)
            {
                data.SuppressBigItemDecollision = true;
            }
        }

        private static IEnumerator StabilizeBigItem(
            GoPointer pointer,
            ShipItem item)
        {
            SailpackData data = item != null
                ? item.GetComponent<SailpackData>()
                : null;
            float minimumEnd = Time.realtimeSinceStartup + 0.25f;
            float timeout = Time.realtimeSinceStartup + 1f;
            int clearFixedSteps = 0;

            while (pointer != null && item != null &&
                   pointer.GetHeldItem() == item && item.held == pointer &&
                   Time.realtimeSinceStartup < timeout &&
                   (Time.realtimeSinceStartup < minimumEnd ||
                    clearFixedSteps < 3))
            {
                yield return new WaitForFixedUpdate();
                if (item.colChecker != null &&
                    item.colChecker.collisions <= 0)
                {
                    clearFixedSteps++;
                }
                else
                {
                    clearFixedSteps = 0;
                }
            }

            if (data != null)
            {
                data.SuppressBigItemDecollision = false;
            }
        }

        private static void PositionForPickup(GoPointer pointer, ShipItem item)
        {
            item.transform.position = GetHeldPosition(
                pointer,
                item.holdDistance,
                item.holdHeight);
            item.transform.rotation = GetHeldRotation(
                pointer,
                item.heldRotationOffset);
            if (item.itemRigidbodyC != null)
            {
                item.ResetRigidbody();
            }
        }

        private static void LeaveAccessible(
            GoPointer pointer,
            ShipItem item,
            SailpackRecord record,
            string reason)
        {
            if (item == null)
            {
                SwappableStaysailPlugin.Log?.LogError(
                    $"Removed {record?.GetDisplayName() ?? "a staysail"}, " +
                    $"but its package became unavailable because {reason}.");
                return;
            }

            SailpackData data = item.GetComponent<SailpackData>();
            if (data != null)
            {
                data.SuppressBigItemDecollision = false;
            }

            if (pointer != null && pointer.GetHeldItem() == item)
            {
                item.OnDrop();
                pointer.DropItem();
            }
            else if (item.held == pointer)
            {
                item.held = null;
            }
            if (pointer != null)
            {
                item.transform.position =
                    pointer.transform.position +
                    pointer.transform.forward * 0.9f -
                    pointer.transform.up * 0.35f;
                item.transform.rotation = GetHeldRotation(
                    pointer,
                    item.heldRotationOffset);
            }
            SetLayer(item.transform, 0);
            if (item.itemRigidbodyC != null)
            {
                item.ResetRigidbody();
            }

            NotificationUi.instance?.ShowNotification(
                "Staysail removed; the sail package was placed in front of you.");
            SwappableStaysailPlugin.Log?.LogWarning(
                $"Removed {record?.GetDisplayName() ?? "a staysail"}; " +
                $"left its package in the world because {reason}.");
        }

        private static void SetLayer(Transform root, int layer)
        {
            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
            {
                child.gameObject.layer = layer;
            }
        }
    }
}
