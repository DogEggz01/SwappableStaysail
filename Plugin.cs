using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using cakeslice;
using UnityEngine;

namespace SwappableStaysail
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency(
        ShipyardExpansionGuid,
        BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class SwappableStaysailPlugin : BaseUnityPlugin
    {
        // Preserve the identity of the installed 1.1.4 release.
        public const string PluginGuid = "DogEggz";
        public const string PluginName = "Swappable Staysail";
        public const string PluginVersion = "1.1.5";
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

    [HarmonyPatch(typeof(Sail), nameof(Sail.GetCurrentShadowMult))]
    internal static class StaysailWindReceptionPatch
    {
        [HarmonyPostfix]
        private static void Postfix(Sail __instance, ref float __result)
        {
            if (StaysailWindShadow.AppliesTo(__instance))
            {
                // Stable compatibility behavior for both loaded and swapped
                // staysails, independent of the load-time shadow cache. Keep
                // registration and colliders intact so other sails see them.
                __result = 1f;
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
