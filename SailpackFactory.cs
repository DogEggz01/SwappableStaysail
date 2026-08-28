using System;
using System.Collections;
using UnityEngine;

namespace SwappableStaysail
{
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
}
