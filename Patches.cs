using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using cakeslice;
using UnityEngine;

namespace SwappableStaysail
{
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

    [HarmonyPatch(
        typeof(SaveableBoatCustomization),
        nameof(SaveableBoatCustomization.GetData))]
    internal static class ExcludeSailmakerPreviewsFromSavePatch
    {
        [HarmonyPostfix]
        private static void Postfix(
            SaveableBoatCustomization __instance,
            SaveBoatCustomizationData __result)
        {
            if (__result?.sails == null || SailmakerSession.GetPreviews().Count == 0)
            {
                return;
            }

            BoatRefs refs = __instance.GetComponent<BoatRefs>();
            if (refs?.masts == null)
            {
                return;
            }

            List<int> previewIndices = new List<int>();
            int dataIndex = 0;
            foreach (Mast mast in refs.masts)
            {
                if (mast == null || !mast.gameObject.activeSelf || mast.sails == null)
                {
                    continue;
                }
                foreach (GameObject sailObject in mast.sails)
                {
                    Sail sail = sailObject != null
                        ? sailObject.GetComponent<Sail>()
                        : null;
                    if (SailmakerSession.IsPreview(sail))
                    {
                        previewIndices.Add(dataIndex);
                    }
                    dataIndex++;
                }
            }

            for (int i = previewIndices.Count - 1; i >= 0; i--)
            {
                int index = previewIndices[i];
                if (index >= 0 && index < __result.sails.Count)
                {
                    __result.sails.RemoveAt(index);
                }
            }
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

    [HarmonyPatch(typeof(GoPointer), "DoRaycast")]
    internal static class StayInstallRaycastPatch
    {
        [HarmonyPrefix]
        private static void Prefix(GoPointer __instance)
        {
            StayInstallTargeting.PrepareRaycast(__instance);
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
                shipItem.itemRigidbodyC.gameObject.layer = 0;
            }
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


}
