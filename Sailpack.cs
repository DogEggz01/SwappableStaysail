using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx.Logging;
using HarmonyLib;
using cakeslice;
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
        // Every package given a record, including one still staged and
        // inactive (no Awake, so no OnDestroy when it goes). The save reads
        // this instead of searching every loaded object, about 3 ms a search.
        private static readonly HashSet<SailpackData> Tracked =
            new HashSet<SailpackData>();

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

        internal static IEnumerable<SailpackData> GetTracked()
        {
            Tracked.RemoveWhere(data => data == null);
            return Tracked;
        }

        internal static void ClearTracked()
        {
            Tracked.Clear();
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

            Tracked.Add(this);
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
            LoadRecords();
            SailDamageSaveData.Load(Records);
        }

        private static void LoadRecords()
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
            SailpackData.ClearTracked();
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
                SailDamageSaveData.Save(snapshot);
            }
            catch (Exception exception)
            {
                SwappableStaysailPlugin.Log?.LogError(
                    $"Could not save sail package data: {exception}");
            }
        }

        private static void ReconcileLiveSailpacks()
        {
            HashSet<int> liveIds = new HashSet<int>();
            foreach (SailpackData data in SailpackData.GetTracked())
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

            // The ships' cargo caches can only prove more IDs alive, so read
            // them only while a record is still unproven: first the game's own
            // ship list, then every loaded ship. During the save Shipwright and
            // Storage swaps in a list without its copies (its SaveFilterPatch),
            // so only the search sees a far copy's cache.
            if (AllProven(liveIds))
            {
                return;
            }
            AddCachedIds(manager.GetCurrentObjects(), liveIds);
            if (!AllProven(liveIds))
            {
                AddCachedIds(
                    Resources.FindObjectsOfTypeAll<SaveableObject>(),
                    liveIds);
            }

            List<int> stale = Records.Keys
                .Where(id => !liveIds.Contains(id))
                .ToList();
            foreach (int id in stale)
            {
                Records.Remove(id);
            }
        }

        private static bool AllProven(HashSet<int> liveIds)
        {
            foreach (int id in Records.Keys)
            {
                if (!liveIds.Contains(id))
                {
                    return false;
                }
            }
            return true;
        }

        private static void AddCachedIds(
            IEnumerable<SaveableObject> saveableObjects,
            HashSet<int> liveIds)
        {
            if (saveableObjects == null)
            {
                return;
            }

            foreach (SaveableObject saveableObject in saveableObjects)
            {
                if (saveableObject == null || saveableObject.localItems == null)
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

        // Sail Damage's state of the packed sail (its DamageModel.Snapshot) and
        // the key its hole pattern is drawn from. Not part of the SSP3 record:
        // SailDamageSaveData keeps them under their own save key.
        internal const int DamageValueCount = 6;
        public float[] damage;
        public string holeKey;

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
                return $"{stay} {sail}{GetDamageSuffix()}";
            }

            int scaleYPercentage = Mathf.RoundToInt(scaleY * 100f);
            int scaleZPercentage = Mathf.RoundToInt(scaleZ * 100f);
            return $"{stay} {sail} " +
                   $"({scaleYPercentage}%x{scaleZPercentage}%)" +
                   GetDamageSuffix();
        }

        private string GetDamageSuffix()
        {
            if (damage == null || damage.Length < DamageValueCount)
            {
                return "";
            }

            int damagePercentage = Mathf.RoundToInt(damage[0] * 100f);
            if (damagePercentage > 0)
            {
                return $", {damagePercentage}% damaged";
            }
            return damage[1] > 0f ? ", patched" : "";
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
            SailpackRecord copy = (SailpackRecord)MemberwiseClone();
            copy.damage = damage != null ? (float[])damage.Clone() : null;
            return copy;
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

    internal static class StaysailInteractionVisuals
    {
        private static readonly FieldInfo PointedAtButtonField =
            AccessTools.Field(typeof(GoPointer), "pointedAtButton");

        internal static void ResetAfterInteraction()
        {
            Reset();
        }

        internal static void ResetForShipyard()
        {
            Reset();
        }

        private static void Reset()
        {
            // Temporary install colliders and vanilla pointer/button state still
            // need explicit cleanup even though the custom stay outline is gone.
            StayInstallTargeting.Deactivate();
            FurledStaysailButton.ResetAllInteractionVisuals();
            ClearPointerTargets();
        }

        private static void ClearPointerTargets()
        {
            if (PointedAtButtonField == null)
            {
                return;
            }

            foreach (GoPointer pointer in
                     UnityEngine.Object.FindObjectsOfType<GoPointer>())
            {
                GoPointerButton target =
                    PointedAtButtonField.GetValue(pointer) as GoPointerButton;
                if (target == null ||
                    (!(target is StayInstallTargetButton) &&
                     !(target is FurledStaysailButton)))
                {
                    continue;
                }
                target.ForceUnlook();
                target.Unclick();
                PointedAtButtonField.SetValue(pointer, null);
            }
        }
    }

    [HarmonyPatch(typeof(GoPointer), "DoRaycast")]
    internal static class StayInstallRaycastPatch
    {
        [HarmonyPrefix]
        private static void Prefix(GoPointer __instance)
        {
            StayInstallTargeting.PrepareRaycast(__instance);
        }

    }

    [HarmonyPatch(typeof(GoPointer), "LateUpdate")]
    internal static class StaysailInstallActivatePatch
    {
        [HarmonyPrefix]
        private static void Prefix(
            GoPointer __instance,
            GoPointerButton ___pointedAtButton)
        {
            if (!StaysailInput.ActivateDown(__instance) ||
                !(___pointedAtButton is StayInstallTargetButton installTarget) ||
                !installTarget.IsAvailable)
            {
                return;
            }

            // Vanilla sends Activate to the held item instead of the pointed
            // button. Bridge that input only after vanilla chose our target.
            installTarget.TryInstall(__instance.GetHeldItem());
        }
    }

    [HarmonyPatch(typeof(Mast), "Start")]
    internal static class MastStartPatch
    {
        [HarmonyPostfix]
        private static void Postfix(Mast __instance)
        {
            StaysailTargets.EnsureRuntimeButtons(__instance);
        }
    }

    [HarmonyPatch(typeof(Mast), nameof(Mast.AttachSailToMast))]
    internal static class MastAttachPatch
    {
        [HarmonyPostfix]
        private static void Postfix(Mast __instance, GameObject sailObject)
        {
            StaysailTargets.EnsureRuntimeButtons(__instance);
            StaysailTargets.EnsureRemovalButton(__instance, sailObject);
        }
    }

    internal static class StaysailInstaller
    {
        internal static bool TryInstall(
            SailpackData data,
            Mast mast,
            out string error)
        {
            error = null;
            SailpackRecord record = data?.Record;
            GameObject sailPrefab = data?.GetSailPrefab();
            Sail prefabSail = sailPrefab != null ? sailPrefab.GetComponent<Sail>() : null;
            if (!StaysailTargets.IsEligible(mast))
            {
                error = "That stay cannot accept swappable staysails.";
                return false;
            }
            if (record == null || prefabSail == null ||
                prefabSail.category != SailCategory.staysail)
            {
                error = "The sail model in this sail package is unavailable.";
                return false;
            }
            if (mast.sails == null || mast.sails.Count >= mast.maxSails)
            {
                error = "That stay has no free sail slot.";
                return false;
            }

            GameObject sailObject = null;
            bool attachmentCommitted = false;
            try
            {
                sailObject = UnityEngine.Object.Instantiate(
                    sailPrefab,
                    mast.transform.position,
                    mast.transform.rotation);
                Sail sail = sailObject.GetComponent<Sail>();
                sail.enabled = false;
                Rigidbody rigidbody = sailObject.GetComponent<Rigidbody>();
                if (rigidbody != null)
                {
                    rigidbody.isKinematic = true;
                }
                sailObject.transform.SetParent(mast.transform, true);
                sail.ChangeInstallHeight(record.installHeight);
                sail.LoadScale(record.scaleY, record.scaleZ);
                sail.ChangeSailColor(record.sailColor);

                SailConnections connections = sailObject.GetComponent<SailConnections>();
                if (connections == null || connections.colChecker == null)
                {
                    throw new InvalidOperationException("Sail connection data is missing.");
                }
                if (!HasControllerCapacity(mast, mast.sails.Count, connections))
                {
                    error = "That stay has no free winch connection.";
                    UnityEngine.Object.Destroy(sailObject);
                    return false;
                }
                connections.colChecker.colAngleMin = record.minAngle;
                connections.colChecker.colAngleMax = record.maxAngle;
                sail.UpdateInstallPosition();

                float bottom = sail.GetCurrentInstallHeight() - sail.GetScaledHeight();
                if (sail.GetScaledHeight() > mast.mastHeight || bottom < -0.1f)
                {
                    error = "The packed sail does not fit this stay.";
                    UnityEngine.Object.Destroy(sailObject);
                    return false;
                }
                foreach (GameObject existingObject in mast.sails)
                {
                    Sail existing = existingObject != null
                        ? existingObject.GetComponent<Sail>()
                        : null;
                    if (existing == null)
                    {
                        continue;
                    }
                    float existingBottom =
                        existing.GetCurrentInstallHeight() - existing.GetScaledHeight();
                    if (Mathf.Max(bottom, existingBottom) <=
                        Mathf.Min(sail.GetCurrentInstallHeight(),
                                  existing.GetCurrentInstallHeight()))
                    {
                        error = "The packed sail overlaps another sail.";
                        UnityEngine.Object.Destroy(sailObject);
                        return false;
                    }
                }

                mast.AttachSailToMast(sailObject);
                if (!sail.IsInstalled() || mast.sails == null ||
                    !mast.sails.Contains(sailObject))
                {
                    throw new InvalidOperationException(
                        "The stay did not retain the installed sail.");
                }
                attachmentCommitted = true;
                SailDamageBridge.ApplyDamage(sail, record);

                try
                {
                    StaysailTargets.EnsureRemovalButton(mast, sailObject);
                }
                catch (Exception buttonException)
                {
                    SwappableStaysailPlugin.Log?.LogWarning(
                        "Staysail installed, but its removal target could not be " +
                        $"created immediately: {buttonException}");
                }

                ConsumePackage(data);
                try
                {
                    StaysailInteractionVisuals.ResetAfterInteraction();
                }
                catch (Exception visualException)
                {
                    SwappableStaysailPlugin.Log?.LogWarning(
                        $"Installed staysail interaction cleanup failed: " +
                        visualException);
                }
                SwappableStaysailPlugin.Log?.LogInfo(
                    $"Installed {record.GetDisplayName()} on " +
                    $"boat {record.targetBoatSceneIndex}, stay " +
                    $"{record.targetMastOrderIndex}.");
                return true;
            }
            catch (Exception exception)
            {
                if (!attachmentCommitted && sailObject != null)
                {
                    Sail partialSail = sailObject.GetComponent<Sail>();
                    if (mast?.sails != null && mast.sails.Contains(sailObject) &&
                        partialSail != null)
                    {
                        StaysailDetacher.TryDetach(
                            mast,
                            partialSail,
                            out string rollbackError);
                        if (!string.IsNullOrEmpty(rollbackError))
                        {
                            SwappableStaysailPlugin.Log?.LogWarning(
                                $"Partial install rollback: {rollbackError}");
                        }
                    }
                    else
                    {
                        UnityEngine.Object.Destroy(sailObject);
                    }
                }
                error = "Could not install the packed sail.";
                SwappableStaysailPlugin.Log?.LogError(
                    $"Sail package installation failed: {exception}");
                return false;
            }
        }

        private static void ConsumePackage(SailpackData data)
        {
            if (data == null)
            {
                return;
            }

            try
            {
                ConsumePackageCore(data);
            }
            catch (Exception exception)
            {
                ShipItem item = data.GetComponent<ShipItem>();
                GoPointer pointer = item != null ? item.held : null;
                try
                {
                    pointer?.DropItem();
                }
                catch (Exception pointerException)
                {
                    if (item != null)
                    {
                        item.held = null;
                    }
                    SwappableStaysailPlugin.Log?.LogWarning(
                        "Fallback pointer cleanup failed after install: " +
                        pointerException);
                }
                SailpackFactory.DiscardStaged(data, true);
                SwappableStaysailPlugin.Log?.LogWarning(
                    "Used fallback sail-package cleanup after a committed " +
                    $"install: {exception}");
            }
        }

        private static void ConsumePackageCore(SailpackData data)
        {

            ShipItem item = data.GetComponent<ShipItem>();
            SaveablePrefab saveable = data.GetComponent<SaveablePrefab>();
            GoPointer pointer = item != null ? item.held : null;
            if (saveable != null && saveable.instanceId > 0)
            {
                SailpackPersistence.Remove(saveable.instanceId);
            }

            if (pointer != null)
            {
                try
                {
                    item.OnDrop();
                }
                catch (Exception dropException)
                {
                    SwappableStaysailPlugin.Log?.LogWarning(
                        $"Sail package drop callback failed during install: " +
                        dropException);
                }
                try
                {
                    pointer.DropItem();
                }
                catch (Exception pointerException)
                {
                    item.held = null;
                    SwappableStaysailPlugin.Log?.LogWarning(
                        $"Sail package pointer release failed during install: " +
                        pointerException);
                }
            }

            data.gameObject.SetActive(false);
            try
            {
                if (item != null)
                {
                    item.DestroyItem();
                }
                else
                {
                    saveable?.Unregister();
                    UnityEngine.Object.Destroy(data.gameObject);
                }
            }
            catch (Exception destroyException)
            {
                try
                {
                    saveable?.Unregister();
                }
                catch (Exception unregisterException)
                {
                    SwappableStaysailPlugin.Log?.LogWarning(
                        $"Sail package save cleanup failed after install: " +
                        unregisterException);
                }
                UnityEngine.Object.Destroy(data.gameObject);
                SwappableStaysailPlugin.Log?.LogWarning(
                    $"Sail package used fallback destruction after install: " +
                    destroyException);
            }
        }

        private static bool HasControllerCapacity(
            Mast mast,
            int index,
            SailConnections connections)
        {
            if (connections.reefController != null &&
                (mast.reefWinch == null || index >= mast.reefWinch.Length))
            {
                return false;
            }
            if (connections.angleControllerMid != null &&
                (mast.midAngleWinch == null || index >= mast.midAngleWinch.Length))
            {
                return false;
            }
            if ((connections.angleControllerLeft != null ||
                 connections.angleControllerRight != null) &&
                (mast.leftAngleWinch == null || index >= mast.leftAngleWinch.Length ||
                 mast.rightAngleWinch == null || index >= mast.rightAngleWinch.Length))
            {
                return false;
            }
            if (connections.mastReefAttachment != null &&
                (mast.mastReefAtt == null || index >= mast.mastReefAtt.Length))
            {
                return false;
            }
            return true;
        }
    }

    internal sealed class StayInstallTargetButton : GoPointerButton
    {
        private Mast target;
        private SailpackData data;
        private Collider interactionCollider;
        private string interactionLabel;

        internal bool IsAvailable =>
            !unclickable && data != null && data.HasRecord &&
            interactionCollider != null && interactionCollider.enabled;

        internal void Initialize(Mast newTarget, Collider collider)
        {
            target = newTarget;
            interactionCollider = collider;
        }

        internal void Configure(
            Mast newTarget,
            SailpackData newData,
            string activateLabel)
        {
            target = newTarget;
            data = newData;
            interactionLabel = activateLabel;
            lookText = data != null && data.HasRecord
                ? $"{interactionLabel}: install " +
                  data.Record.GetDisplayName()
                : $"{interactionLabel}: install staysail";
        }

        internal void SetAvailable(bool available)
        {
            unclickable = !available;
            if (available && !gameObject.activeSelf)
            {
                gameObject.SetActive(true);
            }
            if (interactionCollider != null)
            {
                interactionCollider.enabled = available;
            }
            if (!available && gameObject.activeSelf)
            {
                gameObject.SetActive(false);
            }
        }

        internal void ResetInteractionVisuals()
        {
            SetAvailable(false);
            ForceUnlook();
            Unclick();
            overrideEnableOutline = false;
            foreach (Outline outline in GetComponents<Outline>())
            {
                outline.enabled = false;
            }
        }

        internal void DisposeInteractionVolume()
        {
            if (interactionCollider != null)
            {
                UnityEngine.Object.Destroy(interactionCollider.gameObject);
                interactionCollider = null;
            }
        }

        internal void TryInstall(PickupableItem heldItem)
        {
            SailpackData heldData = heldItem != null
                ? heldItem.GetComponent<SailpackData>()
                : null;
            if (heldData == null || heldData != data || target == null)
            {
                return;
            }
            if (!StaysailInstaller.TryInstall(heldData, target, out string error))
            {
                NotificationUi.instance?.ShowNotification(error);
            }
        }

        public override bool OnItemClick(PickupableItem heldItem)
        {
            // F remains exclusively the normal pickup/drop control. Installing
            // a sail package is handled by the separate Activate action.
            return false;
        }

        public override void OnAltActivate(GoPointer activatingPointer)
        {
            if (activatingPointer != null && IsAvailable)
            {
                TryInstall(activatingPointer.GetHeldItem());
            }
        }
    }

    internal sealed class StayInstallTargetProxy : MonoBehaviour
    {
    }

    internal static class StayInstallTargeting
    {
        private static readonly Dictionary<Mast, List<StayInstallTargetButton>>
            Buttons = new Dictionary<Mast, List<StayInstallTargetButton>>();
        private static SailpackData cachedData;
        private static Mast cachedTarget;
        private static List<StayInstallTargetButton> availableButtons;
        private static GoPointer activePointer;
        private static SailpackData activeData;
        private static Mast activeTarget;
        private static string activeLabel;

        internal static void PrepareRaycast(GoPointer pointer)
        {
            if (pointer == null || GameState.sleeping || GameState.inBed ||
                BoatCamera.on || GameState.currentShipyard != null)
            {
                Deactivate(pointer);
                return;
            }

            PickupableItem held = pointer.GetHeldItem();
            SailpackData data = held != null
                ? held.GetComponent<SailpackData>()
                : null;
            if (data == null)
            {
                Deactivate(pointer);
                return;
            }
            Mast target = ResolveTarget(data);
            if (target == null)
            {
                Deactivate(pointer);
                return;
            }

            List<StayInstallTargetButton> buttons = GetOrCreateButtons(target);
            if (buttons == null || buttons.Count == 0)
            {
                Deactivate(pointer);
                return;
            }

            string label = StaysailInput.ActivateLabel(pointer);
            bool changed = activePointer != pointer ||
                           availableButtons != buttons ||
                           activeData != data || activeTarget != target ||
                           activeLabel != label;
            if (!changed)
            {
                return;
            }

            if (availableButtons != buttons || activePointer != pointer)
            {
                DisableAvailableButtons();
                availableButtons = buttons;
            }
            activePointer = pointer;
            activeData = data;
            activeTarget = target;
            activeLabel = label;

            foreach (StayInstallTargetButton button in availableButtons)
            {
                if (button != null)
                {
                    button.Configure(target, data, label);
                    button.SetAvailable(true);
                }
            }
        }

        private static Mast ResolveTarget(SailpackData data)
        {
            if (data == null || data.Record == null)
            {
                cachedData = null;
                cachedTarget = null;
                return null;
            }
            if (cachedData != data || cachedTarget == null ||
                !cachedTarget.gameObject.activeInHierarchy)
            {
                bool changedPackage = cachedData != data;
                cachedData = data;
                cachedTarget = StaysailTargets.Resolve(data);
                if (changedPackage && cachedTarget == null)
                {
                    SwappableStaysailPlugin.Log?.LogWarning(
                        $"Could not resolve target stay for " +
                        $"{data.Record.GetDisplayName()} (boat " +
                        $"{data.Record.targetBoatSceneIndex}, mast " +
                        $"{data.Record.targetMastOrderIndex}).");
                }
            }
            return cachedTarget;
        }

        internal static List<StayInstallTargetButton> GetOrCreateButtons(Mast mast)
        {
            PruneButtons();
            if (mast == null)
            {
                return null;
            }
            if (Buttons.TryGetValue(
                    mast,
                    out List<StayInstallTargetButton> existing) &&
                existing != null && existing.Count > 0)
            {
                return existing;
            }

            List<Collider> interactionColliders =
                CreateInteractionColliders(mast);
            if (interactionColliders.Count == 0)
            {
                return null;
            }

            List<StayInstallTargetButton> buttons =
                new List<StayInstallTargetButton>(interactionColliders.Count);
            foreach (Collider collider in interactionColliders)
            {
                if (collider == null)
                {
                    continue;
                }
                MeshRenderer renderer =
                    collider.gameObject.AddComponent<MeshRenderer>();
                renderer.enabled = false;
                StayInstallTargetButton button =
                    collider.gameObject.AddComponent<StayInstallTargetButton>();
                button.Initialize(mast, collider);
                button.Configure(mast, null, StaysailInput.DefaultActivateLabel);
                button.SetAvailable(false);
                buttons.Add(button);
            }
            Buttons[mast] = buttons;
            return buttons;
        }

        private static List<Collider> CreateInteractionColliders(Mast mast)
        {
            List<Collider> colliders = new List<Collider>();
            HashSet<GameObject> coveredObjects = new HashSet<GameObject>();

            foreach (Collider source in mast.GetComponentsInChildren<Collider>(true))
            {
                if (!IsInteractionSource(source))
                {
                    continue;
                }
                Collider copy = CopyCollider(source, mast);
                if (copy != null)
                {
                    colliders.Add(copy);
                    coveredObjects.Add(source.gameObject);
                }
            }

            // Some stays render long rope/rail sections without colliders. Add
            // lightweight boxes for those renderers so every visible part of
            // the stay can be pointed at, as promised by the interaction text.
            foreach (Renderer source in mast.GetComponentsInChildren<Renderer>(true))
            {
                if (!IsInteractionSource(source) ||
                    coveredObjects.Contains(source.gameObject))
                {
                    continue;
                }
                Collider box = CreateRendererCollider(source, mast);
                if (box != null)
                {
                    colliders.Add(box);
                }
            }
            return colliders;
        }

        private static bool IsInteractionSource(Component component)
        {
            return component != null &&
                   component.GetComponentInParent<Sail>() == null &&
                   component.GetComponentInParent<StayInstallTargetProxy>() == null &&
                   component.GetComponentInParent<PickupableItem>() == null;
        }

        private static Collider CopyCollider(Collider source, Mast mast)
        {
            GameObject volume = CreateVolume(source.transform);
            Collider copy = null;
            if (source is CapsuleCollider sourceCapsule)
            {
                CapsuleCollider capsule = volume.AddComponent<CapsuleCollider>();
                capsule.center = sourceCapsule.center;
                capsule.direction = sourceCapsule.direction;
                capsule.height = sourceCapsule.height + 0.16f;
                capsule.radius = sourceCapsule.radius + 0.08f;
                copy = capsule;
            }
            else if (source is BoxCollider sourceBox)
            {
                BoxCollider box = volume.AddComponent<BoxCollider>();
                box.center = sourceBox.center;
                box.size = sourceBox.size + Vector3.one * 0.16f;
                copy = box;
            }
            else if (source is SphereCollider sourceSphere)
            {
                SphereCollider sphere = volume.AddComponent<SphereCollider>();
                sphere.center = sourceSphere.center;
                sphere.radius = sourceSphere.radius + 0.08f;
                copy = sphere;
            }
            else if (source is MeshCollider sourceMesh &&
                     sourceMesh.sharedMesh != null)
            {
                BoxCollider box = volume.AddComponent<BoxCollider>();
                box.center = sourceMesh.sharedMesh.bounds.center;
                box.size = sourceMesh.sharedMesh.bounds.size + Vector3.one * 0.16f;
                copy = box;
            }
            else
            {
                UnityEngine.Object.Destroy(volume);
                return CreateWorldBoundsCollider(source.bounds, mast);
            }
            PrepareInteractionCollider(copy);
            return copy;
        }

        private static Collider CreateRendererCollider(Renderer source, Mast mast)
        {
            Bounds localBounds;
            MeshFilter filter = source.GetComponent<MeshFilter>();
            if (filter != null && filter.sharedMesh != null)
            {
                localBounds = filter.sharedMesh.bounds;
            }
            else if (source is SkinnedMeshRenderer skinned)
            {
                localBounds = skinned.localBounds;
            }
            else
            {
                return CreateWorldBoundsCollider(source.bounds, mast);
            }

            GameObject volume = CreateVolume(source.transform);
            BoxCollider box = volume.AddComponent<BoxCollider>();
            box.center = localBounds.center;
            box.size = localBounds.size + Vector3.one * 0.16f;
            PrepareInteractionCollider(box);
            return box;
        }

        private static Collider CreateWorldBoundsCollider(Bounds bounds, Mast mast)
        {
            if (bounds.size.sqrMagnitude <= 0.0001f)
            {
                return null;
            }
            GameObject volume = CreateVolume(mast.transform);
            volume.transform.position = bounds.center;
            volume.transform.rotation = Quaternion.identity;
            Vector3 scale = volume.transform.lossyScale;
            BoxCollider box = volume.AddComponent<BoxCollider>();
            box.center = Vector3.zero;
            box.size = new Vector3(
                bounds.size.x / Mathf.Max(Mathf.Abs(scale.x), 0.0001f),
                bounds.size.y / Mathf.Max(Mathf.Abs(scale.y), 0.0001f),
                bounds.size.z / Mathf.Max(Mathf.Abs(scale.z), 0.0001f)) +
                Vector3.one * 0.16f;
            PrepareInteractionCollider(box);
            return box;
        }

        private static GameObject CreateVolume(Transform sourceTransform)
        {
            GameObject volume = new GameObject(
                "Swappable Staysail install target volume");
            // Vanilla GoPointer ignores layer 2 and requires its button on the
            // exact collider object. These temporary layer-0 trigger volumes are
            // enabled only while holding a package for this specific stay.
            volume.layer = 0;
            volume.transform.SetParent(sourceTransform, false);
            volume.transform.localPosition = Vector3.zero;
            volume.transform.localRotation = Quaternion.identity;
            volume.transform.localScale = Vector3.one;
            volume.AddComponent<StayInstallTargetProxy>();
            return volume;
        }

        private static void PrepareInteractionCollider(Collider collider)
        {
            collider.isTrigger = true;
            collider.enabled = false;
        }

        internal static void Deactivate()
        {
            DisableAvailableButtons();
            cachedData = null;
            cachedTarget = null;
            activePointer = null;
            activeData = null;
            activeTarget = null;
            activeLabel = null;
        }

        private static void Deactivate(GoPointer pointer)
        {
            if (pointer == null || pointer == activePointer)
            {
                Deactivate();
            }
        }

        private static void DisableAvailableButtons()
        {
            if (availableButtons == null)
            {
                return;
            }
            foreach (StayInstallTargetButton button in availableButtons)
            {
                button?.ResetInteractionVisuals();
            }
            availableButtons = null;
        }

        private static void PruneButtons()
        {
            List<Mast> staleMasts = new List<Mast>();
            foreach (KeyValuePair<Mast, List<StayInstallTargetButton>> pair in Buttons)
            {
                if (pair.Key == null || pair.Value == null)
                {
                    staleMasts.Add(pair.Key);
                    continue;
                }
                pair.Value.RemoveAll(button => button == null);
                if (pair.Value.Count == 0)
                {
                    staleMasts.Add(pair.Key);
                }
            }
            foreach (Mast stale in staleMasts)
            {
                Buttons.Remove(stale);
            }
        }

        internal static void Dispose()
        {
            Deactivate();
            foreach (List<StayInstallTargetButton> buttons in Buttons.Values)
            {
                if (buttons == null)
                {
                    continue;
                }
                foreach (StayInstallTargetButton button in buttons)
                {
                    button?.DisposeInteractionVolume();
                }
            }
            Buttons.Clear();
        }
    }

    internal static class StaysailInput
    {
        internal static bool ActivateDown(GoPointer pointer)
        {
            if (pointer == null)
            {
                return false;
            }
            switch (pointer.type)
            {
                case GoPointer.PointerType.leftTouch:
                    return OVRInput.GetDown(
                        OVRInput.Button.Three,
                        OVRInput.Controller.Active);
                case GoPointer.PointerType.rightTouch:
                    return OVRInput.GetDown(
                        OVRInput.Button.One,
                        OVRInput.Controller.Active);
                default:
                    return !GameState.inCursorMenu &&
                           GameInput.GetKeyDown(InputName.Activate);
            }
        }

        internal static string ActivateLabel(GoPointer pointer)
        {
            if (pointer?.type == GoPointer.PointerType.leftTouch)
            {
                return "X";
            }
            if (pointer?.type == GoPointer.PointerType.rightTouch)
            {
                return "A";
            }
            return DefaultActivateLabel;
        }

        internal static string DefaultActivateLabel =>
            GameInput.GetKeyCode(InputName.Activate, false, false).ToString();
    }

    internal static class StaysailDetacher
    {
        internal static bool TryDetach(
            Mast mast,
            Sail sail,
            out string error)
        {
            error = null;
            if (mast == null || sail == null || mast.sails == null ||
                !mast.sails.Contains(sail.gameObject))
            {
                error = "That staysail is no longer attached to this stay.";
                return false;
            }

            GameObject sailObject = sail.gameObject;
            SailConnections connections =
                sailObject.GetComponent<SailConnections>();
            HashSet<GameObject> connectionObjects =
                CollectConnectionObjects(connections);

            // Disable the complete sail root immediately. This also disables the
            // furled renderer and prevents a second activation in the same frame.
            sailObject.SetActive(false);

            Exception vanillaFailure = null;
            try
            {
                mast.DetachSailFromMast(sailObject);
            }
            catch (Exception exception)
            {
                vanillaFailure = exception;
            }

            // Mast.DetachSailFromMast assumes every connection reference exists.
            // Finish the same cleanup deterministically if it stopped partway.
            mast.sails.Remove(sailObject);
            SailDamageBridge.Forget(sail);
            foreach (GameObject connectionObject in connectionObjects)
            {
                if (connectionObject != null && connectionObject != sailObject)
                {
                    UnityEngine.Object.Destroy(connectionObject);
                }
            }
            UnityEngine.Object.Destroy(sailObject);

            try
            {
                mast.UpdateSailOrder();
                mast.UpdateControllerAttachments();
            }
            catch (Exception refreshException)
            {
                SwappableStaysailPlugin.Log?.LogWarning(
                    $"Staysail was detached, but rig refresh failed: " +
                    refreshException);
            }

            if (vanillaFailure != null)
            {
                SwappableStaysailPlugin.Log?.LogWarning(
                    "Vanilla staysail detachment stopped early; completed " +
                    $"cleanup with the safe fallback: {vanillaFailure}");
            }
            return !mast.sails.Contains(sailObject);
        }

        private static HashSet<GameObject> CollectConnectionObjects(
            SailConnections connections)
        {
            HashSet<GameObject> objects = new HashSet<GameObject>();
            if (connections == null)
            {
                return objects;
            }
            Add(objects, connections.colChecker);
            Add(objects, connections.reefController);
            Add(objects, connections.angleControllerMid);
            Add(objects, connections.angleControllerLeft);
            Add(objects, connections.angleControllerRight);
            Add(objects, connections.midRopeAttachment);
            Add(objects, connections.mastReefAttachment);
            Add(objects, connections.mastReefAttExtension);
            return objects;
        }

        private static void Add(HashSet<GameObject> objects, Component component)
        {
            if (component != null)
            {
                objects.Add(component.gameObject);
            }
        }
    }

    internal sealed class FurledStaysailButton : GoPointerButton
    {
        private static readonly HashSet<FurledStaysailButton> Instances =
            new HashSet<FurledStaysailButton>();
        private Mast mast;
        private Sail sail;
        private Collider targetCollider;
        private bool removalInProgress;

        internal void Initialize(Mast newMast, Sail newSail)
        {
            Instances.Add(this);
            mast = newMast;
            sail = newSail;
            if (targetCollider == null)
            {
                FurledStaysailHitbox marker = GetComponent<FurledStaysailHitbox>();
                targetCollider = marker != null ? marker.Collider : null;
            }
        }

        internal void Initialize(
            Mast newMast,
            Sail newSail,
            Collider newTargetCollider)
        {
            targetCollider = newTargetCollider;
            Initialize(newMast, newSail);
        }

        public override void ExtraLateUpdate()
        {
            bool canRemove = !removalInProgress && CanRemove();
            if (targetCollider != null)
            {
                targetCollider.enabled = canRemove;
            }
            lookText = canRemove
                ? $"{StaysailInput.ActivateLabel(pointedAtBy)}: " +
                  "remove staysail to sail package"
                : "";
        }

        internal static void ResetAllInteractionVisuals()
        {
            Instances.RemoveWhere(button => button == null);
            foreach (FurledStaysailButton button in Instances)
            {
                button.ResetInteractionVisuals();
            }
        }

        internal static void DisposeAll()
        {
            ResetAllInteractionVisuals();
            Instances.Clear();
        }

        internal void ResetInteractionVisuals()
        {
            ForceUnlook();
            Unclick();
            overrideEnableOutline = false;
            if (targetCollider != null)
            {
                targetCollider.enabled = false;
            }
            foreach (Outline outline in GetComponents<Outline>())
            {
                outline.enabled = false;
            }
        }

        public override void OnAltActivate(GoPointer activatingPointer)
        {
            if (removalInProgress || !CanRemove() || activatingPointer == null ||
                activatingPointer.GetHeldItem() != null)
            {
                return;
            }

            removalInProgress = true;
            if (targetCollider != null)
            {
                targetCollider.enabled = false;
            }
            SailpackRecord record = SailpackRecord.FromSail(sail, mast);
            SailDamageBridge.CaptureDamage(sail, record);
            Vector3 packagePosition = SailpackPickupHandoff.GetHeldPosition(
                activatingPointer,
                1.25f,
                0f);
            Quaternion packageRotation = SailpackPickupHandoff.GetHeldRotation(
                activatingPointer,
                180f);
            SailpackData data = SailpackFactory.CreateStaged(
                record,
                packagePosition,
                packageRotation,
                true,
                mast);
            if (data == null)
            {
                removalInProgress = false;
                NotificationUi.instance?.ShowNotification(
                    "Could not create a sail package; the sail was not removed.");
                return;
            }

            ShipItem item = data.GetComponent<ShipItem>();
            if (item == null)
            {
                SailpackFactory.DiscardStaged(data, true);
                removalInProgress = false;
                NotificationUi.instance?.ShowNotification(
                    "Could not create a usable sail package; the sail was not removed.");
                return;
            }

            if (!StaysailDetacher.TryDetach(mast, sail, out string error))
            {
                SailpackFactory.DiscardStaged(data, true);
                removalInProgress = false;
                NotificationUi.instance?.ShowNotification(error);
                return;
            }

            SailpackFactory.ActivateStaged(data);
            StaysailInteractionVisuals.ResetAfterInteraction();
            SailpackPickupHandoff.Schedule(
                activatingPointer,
                item,
                record);
            SwappableStaysailPlugin.Log?.LogInfo(
                $"Removed {record.GetDisplayName()} into a saved sail package; " +
                "player handoff scheduled.");
        }

        private bool CanRemove()
        {
            return mast != null && sail != null && sail.IsInstalled() &&
                   GameState.currentShipyard == null &&
                   !GameState.sleeping && !GameState.inBed && !BoatCamera.on &&
                   StaysailTargets.IsEligible(mast) &&
                   sail.category == SailCategory.staysail &&
                   sail.currentUnroll <= 0.001f;
        }

        private void OnDisable()
        {
            ResetInteractionVisuals();
        }

        private void OnDestroy()
        {
            Instances.Remove(this);
        }
    }

    internal sealed class FurledStaysailHitbox : MonoBehaviour
    {
        internal Collider Collider { get; private set; }

        internal void Set(Collider collider)
        {
            Collider = collider;
        }
    }

    internal static class StaysailTargets
    {
        internal static bool IsEligible(Mast mast)
        {
            return mast != null && mast.onlyStaysails && !IsBermudaMast(mast);
        }

        internal static bool IsBermudaMast(Mast mast)
        {
            if (mast == null)
            {
                return false;
            }

            BoatPartOption option = mast.GetComponent<BoatPartOption>();
            string identity = option != null && !string.IsNullOrEmpty(option.optionName)
                ? option.optionName
                : mast.gameObject.name;
            identity = identity.ToLowerInvariant().Replace('_', ' ');
            return identity.Contains("bermuda") &&
                   identity.Contains("mast") &&
                   !identity.Contains("stay");
        }

        internal static int GetBoatSceneIndex(Mast mast)
        {
            if (mast == null)
            {
                return -1;
            }

            SaveableObject saveable = null;
            if (mast.shipRigidbody != null)
            {
                saveable = mast.shipRigidbody.GetComponent<SaveableObject>() ??
                           mast.shipRigidbody.GetComponentInParent<SaveableObject>();
            }
            saveable = saveable ?? mast.GetComponentInParent<SaveableObject>();
            return saveable != null ? saveable.sceneIndex : -1;
        }

        internal static string GetStayName(Mast mast)
        {
            if (mast == null)
            {
                return "Staysail stay";
            }

            BoatPartOption option = mast.GetComponent<BoatPartOption>();
            string name = option != null && !string.IsNullOrEmpty(option.optionName)
                ? option.optionName
                : mast.gameObject.name;
            name = name.Replace('_', ' ').Trim();
            return string.IsNullOrEmpty(name) ? "Staysail stay" : name;
        }

        internal static Mast Resolve(SailpackData data)
        {
            SailpackRecord record = data?.Record;
            if (record == null)
            {
                return null;
            }

            if (Matches(data.RuntimeTarget, record))
            {
                return data.RuntimeTarget;
            }

            Mast[] candidates = Array.Empty<Mast>();
            SaveLoadManager manager = SaveLoadManager.instance;
            if (manager != null)
            {
                SaveableObject[] objects = manager.GetCurrentObjects();
                int boatIndex = record.targetBoatSceneIndex;
                if (objects != null && boatIndex >= 0 &&
                    boatIndex < objects.Length && objects[boatIndex] != null)
                {
                    candidates = objects[boatIndex]
                        .GetComponentsInChildren<Mast>(false)
                        .Where(mast => Matches(mast, record))
                        .ToArray();
                }
            }

            if (candidates.Length == 0)
            {
                candidates = UnityEngine.Object.FindObjectsOfType<Mast>()
                    .Where(mast => Matches(mast, record))
                    .ToArray();
            }

            Mast resolved = candidates.FirstOrDefault(mast =>
                string.Equals(
                    GetStayName(mast),
                    record.targetStayName,
                    StringComparison.OrdinalIgnoreCase));
            if (resolved == null && candidates.Length == 1)
            {
                resolved = candidates[0];
            }
            if (resolved != null)
            {
                data.BindTarget(resolved);
            }
            return resolved;
        }

        private static bool Matches(Mast mast, SailpackRecord record)
        {
            return mast != null && mast.gameObject.activeInHierarchy &&
                   IsEligible(mast) &&
                   GetBoatSceneIndex(mast) == record.targetBoatSceneIndex &&
                   mast.orderIndex == record.targetMastOrderIndex;
        }

        internal static void EnsureRuntimeButtons(Mast mast)
        {
            if (!IsEligible(mast))
            {
                return;
            }

            foreach (GameObject sailObject in mast.sails ?? new List<GameObject>())
            {
                EnsureRemovalButton(mast, sailObject);
            }
        }

        internal static void EnsureRemovalButton(Mast mast, GameObject sailObject)
        {
            Sail sail = sailObject != null ? sailObject.GetComponent<Sail>() : null;
            if (!IsEligible(mast) || sail == null ||
                sail.category != SailCategory.staysail)
            {
                return;
            }

            ReefEffectAnimUniversal reef =
                sail.GetComponentInChildren<ReefEffectAnimUniversal>(true);
            Renderer renderer = reef != null ? reef.furledSail : null;
            if (renderer == null)
            {
                return;
            }

            FurledStaysailButton button =
                renderer.GetComponent<FurledStaysailButton>();
            if (button == null)
            {
                BoxCollider collider = renderer.gameObject.AddComponent<BoxCollider>();
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                if (filter != null && filter.sharedMesh != null)
                {
                    collider.center = filter.sharedMesh.bounds.center;
                    collider.size = filter.sharedMesh.bounds.size;
                }
                collider.isTrigger = true;
                FurledStaysailHitbox marker =
                    renderer.gameObject.AddComponent<FurledStaysailHitbox>();
                marker.Set(collider);
                button = renderer.gameObject.AddComponent<FurledStaysailButton>();
                button.Initialize(mast, sail, collider);
                return;
            }
            button.Initialize(mast, sail);
        }
    }

    internal static class StaysailWindShadow
    {
        private static readonly FieldInfo CheckingField =
            AccessTools.Field(typeof(SailShadowCol), "checking") ??
            throw new MissingFieldException(typeof(SailShadowCol).FullName, "checking");

        internal static bool AppliesTo(Sail sail)
        {
            return sail != null && sail.IsInstalled() &&
                   sail.category == SailCategory.staysail &&
                   StaysailTargets.IsEligible(sail.GetComponentInParent<Mast>());
        }

        internal static void RecoverAfterHingeReset(Sail sail)
        {
            if (!AppliesTo(sail))
            {
                return;
            }

            SailShadowCol shadow = sail.GetComponentInChildren<SailShadowCol>(true);
            if (shadow != null)
            {
                // ResetHingeRestingRot has already deactivated the whole sail,
                // cancelling its coroutines. Release only the abandoned guard;
                // the normal Update starts the next check without duplicating it.
                CheckingField.SetValue(shadow, false);
            }
        }
    }

    [HarmonyPatch(typeof(JibAngleMaster), nameof(JibAngleMaster.ResetHingeRestingRot))]
    internal static class StaysailShadowHingeResetPatch
    {
        [HarmonyPostfix]
        private static void Postfix(JibAngleMaster __instance)
        {
            StaysailWindShadow.RecoverAfterHingeReset(__instance.GetComponent<Sail>());
        }
    }
}
