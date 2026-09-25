using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using cakeslice;
using UnityEngine;

namespace SwappableStaysail
{
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

    [HarmonyPatch(typeof(ShipyardDocuments), "Update")]
    internal static class SailmakerEntryPatch
    {
        private static bool cloning;

        [HarmonyPostfix]
        private static void Postfix(ShipyardDocuments __instance)
        {
            if (cloning || __instance == null || __instance.shipyard == null ||
                __instance.GetComponent<SailmakerEntryInstalled>() != null)
            {
                return;
            }

            cloning = true;
            try
            {
                GameObject clone = UnityEngine.Object.Instantiate(
                    __instance.gameObject,
                    __instance.transform.parent);
                clone.SetActive(false);
                clone.name = "Sail Maker";
                ShipyardDocuments copiedDocuments =
                    clone.GetComponent<ShipyardDocuments>();
                GameObject copiedKeeper = copiedDocuments != null
                    ? copiedDocuments.keeper
                    : null;

                // A live ShipyardDocuments clone carries its existing Outline
                // and interaction state. Strip all interaction components before
                // the clone is activated, then give it one clean Sail Maker button.
                foreach (GoPointerButton oldButton in
                         clone.GetComponentsInChildren<GoPointerButton>(true))
                {
                    UnityEngine.Object.DestroyImmediate(oldButton);
                }
                foreach (Outline oldOutline in
                         clone.GetComponentsInChildren<Outline>(true))
                {
                    UnityEngine.Object.DestroyImmediate(oldOutline);
                }
                foreach (SailmakerEntryInstalled copiedMarker in
                         clone.GetComponentsInChildren<SailmakerEntryInstalled>(true))
                {
                    UnityEngine.Object.DestroyImmediate(copiedMarker);
                }

                Collider sourceCollider = __instance.GetComponent<Collider>();
                Collider cloneCollider = clone.GetComponent<Collider>();
                foreach (Collider copiedCollider in
                         clone.GetComponentsInChildren<Collider>(true))
                {
                    if (copiedCollider != cloneCollider)
                    {
                        UnityEngine.Object.DestroyImmediate(copiedCollider);
                    }
                }

                Vector3 lateral = Vector3.zero;
                bool hasAuthoredPose = TryGetAuthoredPose(
                    __instance.gameObject.scene.name,
                    out Vector3 localPosition,
                    out Vector3 localEulerAngles);
                if (hasAuthoredPose)
                {
                    clone.transform.position =
                        __instance.transform.TransformPoint(localPosition);
                    clone.transform.rotation =
                        __instance.transform.rotation *
                        Quaternion.Euler(localEulerAngles);
                }
                else
                {
                    // Retain a safe fallback for modded or future shipyards that
                    // do not have a hand-authored pose.
                    lateral = GetSurfaceLateral(
                        sourceCollider,
                        __instance.transform);
                    float separation =
                        GetProjectedHalfExtent(sourceCollider, lateral) * 2f +
                        0.45f;
                    clone.transform.position = __instance.transform.position +
                                               lateral * separation;
                }

                SailmakerOrderButton button =
                    clone.AddComponent<SailmakerOrderButton>();
                button.Initialize(__instance.shipyard);
                SailmakerEntryStateMirror stateMirror =
                    clone.AddComponent<SailmakerEntryStateMirror>();
                stateMirror.Initialize(__instance.keeper, copiedKeeper);

                GameObject deliveryObject =
                    new GameObject("sail package delivery point");
                deliveryObject.transform.SetParent(clone.transform, false);
                deliveryObject.transform.localPosition = new Vector3(0f, 0.55f, 0.8f);
                SailmakerSession.RegisterDeliveryPoint(
                    __instance.shipyard,
                    deliveryObject.transform);

                __instance.gameObject.AddComponent<SailmakerEntryInstalled>();
                clone.SetActive(true);
                for (int attempt = 0;
                     !hasAuthoredPose &&
                     sourceCollider != null && cloneCollider != null &&
                     sourceCollider.bounds.Intersects(cloneCollider.bounds) &&
                     attempt < 8;
                     attempt++)
                {
                    clone.transform.position += lateral * 0.25f;
                }
            }
            catch (Exception exception)
            {
                SwappableStaysailPlugin.Log?.LogError(
                    $"Could not create sailmaker entry: {exception}");
            }
            finally
            {
                cloning = false;
            }
        }

        private static bool TryGetAuthoredPose(
            string sceneName,
            out Vector3 localPosition,
            out Vector3 localEulerAngles)
        {
            switch (sceneName)
            {
                case "island 1 A Gold Rock":
                    localPosition = new Vector3(-0.646f, -0.66f, 0f);
                    localEulerAngles = new Vector3(-1.716f, 0f, -27.634f);
                    return true;
                case "island 9 E Dragon Cliffs":
                    localPosition = new Vector3(-0.267f, -0.758f, 0f);
                    localEulerAngles = new Vector3(0f, 0f, 20.456f);
                    return true;
                case "island 15 M (Fort)":
                    localPosition = new Vector3(-0.993f, -1.933f, 0f);
                    localEulerAngles = new Vector3(0f, 0f, -15.067f);
                    return true;
                case "island 27 Lagoon SwampShipyard":
                    localPosition = new Vector3(-0.699f, -0.891f, 0f);
                    localEulerAngles = new Vector3(0f, 0f, -157.151f);
                    return true;
                default:
                    localPosition = Vector3.zero;
                    localEulerAngles = Vector3.zero;
                    return false;
            }
        }

        private static float GetProjectedHalfExtent(
            Collider collider,
            Vector3 direction)
        {
            if (collider == null)
            {
                return 0.6f;
            }
            Vector3 extent = collider.bounds.extents;
            Vector3 axis = direction.normalized;
            return Mathf.Abs(axis.x) * extent.x +
                   Mathf.Abs(axis.y) * extent.y +
                   Mathf.Abs(axis.z) * extent.z;
        }

        private static Vector3 GetSurfaceLateral(
            Collider collider,
            Transform source)
        {
            Vector3 normal = source.forward;
            BoxCollider box = collider as BoxCollider;
            if (box != null)
            {
                Vector3 size = box.size;
                if (size.x <= size.y && size.x <= size.z)
                {
                    normal = source.right;
                }
                else if (size.y <= size.x && size.y <= size.z)
                {
                    normal = source.up;
                }
            }

            Vector3 lateral = Vector3.Cross(Vector3.up, normal);
            if (lateral.sqrMagnitude < 0.01f)
            {
                lateral = Vector3.ProjectOnPlane(source.right, Vector3.up);
            }
            if (lateral.sqrMagnitude < 0.01f)
            {
                lateral = source.right;
            }
            lateral.Normalize();
            if (Vector3.Dot(lateral, source.right) < 0f)
            {
                lateral = -lateral;
            }
            return lateral;
        }
    }

    internal sealed class SailmakerEntryInstalled : MonoBehaviour
    {
    }

    internal sealed class SailmakerEntryStateMirror : MonoBehaviour
    {
        private GameObject sourceKeeper;
        private GameObject sailmakerKeeper;

        internal void Initialize(GameObject source, GameObject copy)
        {
            sourceKeeper = source;
            sailmakerKeeper = copy;
            Synchronize();
        }

        private void LateUpdate()
        {
            Synchronize();
        }

        private void Synchronize()
        {
            if (sourceKeeper == null || sailmakerKeeper == null)
            {
                return;
            }
            bool visible = sourceKeeper.activeSelf;
            if (sailmakerKeeper.activeSelf != visible)
            {
                sailmakerKeeper.SetActive(visible);
            }
        }
    }

    [HarmonyPatch(typeof(ShipyardDocuments), nameof(ShipyardDocuments.OnActivate))]
    internal static class NormalShipyardEntryPatch
    {
        [HarmonyPrefix]
        private static void Prefix()
        {
            SailmakerSession.End();
        }
    }

    [HarmonyPatch(typeof(Shipyard), nameof(Shipyard.ActivateDocuments))]
    internal static class ShipyardActivatePatch
    {
        [HarmonyPrefix]
        private static void Prefix()
        {
            StaysailInteractionVisuals.ResetForShipyard();
        }

        [HarmonyPostfix]
        private static void Postfix(Shipyard __instance)
        {
            SailmakerSession.TryBegin(__instance);
        }
    }

    [HarmonyPatch(typeof(Shipyard), nameof(Shipyard.DischargeShip))]
    internal static class ShipyardDischargePatch
    {
        [HarmonyPrefix]
        private static void Prefix()
        {
            SailmakerSession.End();
        }
    }

    [HarmonyPatch(typeof(Shipyard), nameof(Shipyard.CancelOrder))]
    internal static class ShipyardCancelPatch
    {
        [HarmonyPrefix]
        private static bool Prefix()
        {
            if (SailmakerSession.Active)
            {
                SailmakerSession.Cancel();
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(Shipyard), nameof(Shipyard.SelectMast))]
    internal static class ShipyardSelectMastPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(Shipyard __instance, int index)
        {
            if (!SailmakerSession.Active)
            {
                return true;
            }
            BoatRefs refs = __instance.GetCurrentBoat()?.GetComponent<BoatRefs>();
            Mast mast = refs?.masts != null && index >= 0 && index < refs.masts.Length
                ? refs.masts[index]
                : null;
            if (!SailmakerSession.IsAllowedMast(mast))
            {
                NotificationUi.instance?.ShowNotification(
                    "Select a supported staysail stay.");
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(Shipyard), nameof(Shipyard.SelectSail))]
    internal static class ShipyardSelectSailPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(Shipyard __instance, int index)
        {
            if (!SailmakerSession.Active)
            {
                return true;
            }
            SailmakerSession.SelectPreview(index);
            return false;
        }
    }

    [HarmonyPatch(typeof(Shipyard), nameof(Shipyard.AddNewSail))]
    internal static class ShipyardAddSailPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(Shipyard __instance, GameObject prefab)
        {
            if (!SailmakerSession.Active)
            {
                return true;
            }
            Mast mast = __instance.sailInstaller.GetCurrentMast();
            if (!SailmakerSession.IsAllowedMast(mast) ||
                !SailmakerSession.IsAllowedSailPrefab(prefab))
            {
                NotificationUi.instance?.ShowNotification(
                    "Swappable Staysail only accepts staysails on supported stays.");
                return false;
            }
            SailmakerSession.AddPreview(prefab);
            return false;
        }
    }

    [HarmonyPatch(typeof(Shipyard), nameof(Shipyard.RemoveSelectedSail))]
    internal static class ShipyardRemoveSailPatch
    {
        [HarmonyPrefix]
        private static bool Prefix()
        {
            if (!SailmakerSession.Active)
            {
                return true;
            }
            SailmakerSession.RemoveSelectedPreview();
            return false;
        }
    }

    [HarmonyPatch(typeof(ShipyardSailInstaller), "ApplySailPosition")]
    internal static class SailmakerApplyPositionSafetyPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(ShipyardSailInstaller __instance)
        {
            // The vanilla add-sail coroutine can finish after any shipyard sail
            // was removed, bought, or cancelled. Never let its delayed callback
            // dereference a cleared selection, mast, or shipyard.
            return GameState.currentShipyard != null &&
                   __instance.GetCurrentMast() != null &&
                   __instance.GetCurrentSail() != null;
        }
    }

    [HarmonyPatch(typeof(Shipyard), nameof(Shipyard.UpdateOrder), new[] { typeof(bool) })]
    internal static class ShipyardOrderPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(Shipyard __instance)
        {
            return !SailmakerSession.RefreshOrder(__instance);
        }
    }

    [HarmonyPatch(typeof(Shipyard), nameof(Shipyard.ConfirmOrder))]
    internal static class ShipyardConfirmPatch
    {
        [HarmonyPrefix]
        private static bool Prefix()
        {
            if (!SailmakerSession.Active)
            {
                return true;
            }
            SailmakerSession.Buy();
            return false;
        }
    }

    [HarmonyPatch(typeof(ShipyardUI), "SailMastCompatible")]
    internal static class SailCompatibilityPatch
    {
        [HarmonyPostfix]
        private static void Postfix(ref bool __result, GameObject sailPrefab)
        {
            if (SailmakerSession.Active)
            {
                Mast mast = GameState.currentShipyard?.sailInstaller.GetCurrentMast();
                __result = SailmakerSession.IsAllowedMast(mast) &&
                           SailmakerSession.IsAllowedSailPrefab(sailPrefab);
            }
        }
    }

    [HarmonyPatch(typeof(ShipyardButton), nameof(ShipyardButton.OnActivate))]
    internal static class SailmakerButtonGuardPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(ShipyardButton __instance)
        {
            if (!SailmakerSession.Active)
            {
                return true;
            }
            switch (__instance.function)
            {
                case ShipyardButton.ButtonFunction.cleanHull:
                case ShipyardButton.ButtonFunction.repair:
                case ShipyardButton.ButtonFunction.changeCategory:
                case ShipyardButton.ButtonFunction.previousPart:
                case ShipyardButton.ButtonFunction.nextPart:
                    return false;
                case ShipyardButton.ButtonFunction.selectSailCategory:
                    return __instance.index == (int)SailCategory.staysail;
                default:
                    return true;
            }
        }
    }

    [HarmonyPatch(typeof(ShipyardUI), nameof(ShipyardUI.UpdateMastSailsList))]
    internal static class SailmakerSailListPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(ShipyardUI __instance)
        {
            if (!SailmakerSession.Active)
            {
                return true;
            }
            SailmakerSession.RenderPreviewList(__instance);
            return false;
        }
    }

    [HarmonyPatch(typeof(ShipyardUI), nameof(ShipyardUI.UpdateButtonSelections))]
    internal static class SailmakerSelectionPatch
    {
        [HarmonyPostfix]
        private static void Postfix(ShipyardUI __instance)
        {
            if (SailmakerSession.Active)
            {
                SailmakerSession.RenderPreviewSelections(__instance);
            }
        }
    }

    [HarmonyPatch(typeof(ShipyardUI), nameof(ShipyardUI.UpdateSailsCountText))]
    internal static class SailmakerSailCountPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(ShipyardUI __instance)
        {
            if (!SailmakerSession.Active)
            {
                return true;
            }
            SailmakerSession.HideSailCount(__instance);
            return false;
        }
    }

    [HarmonyPatch(typeof(ShipyardUI), nameof(ShipyardUI.RefreshButtons))]
    internal static class SailmakerUiPatch
    {
        private static readonly Dictionary<Collider, bool> ColliderStates =
            new Dictionary<Collider, bool>();
        private static readonly Dictionary<Renderer, Material> RendererStates =
            new Dictionary<Renderer, Material>();
        private static readonly Dictionary<ShipyardButton, string> ButtonTexts =
            new Dictionary<ShipyardButton, string>();
        private static readonly Dictionary<ShipyardButton, ButtonLayoutState>
            ButtonLayouts =
                new Dictionary<ShipyardButton, ButtonLayoutState>();

        [HarmonyPostfix]
        private static void Postfix(ShipyardUI __instance)
        {
            if (!SailmakerSession.Active)
            {
                RestoreUi();
                return;
            }
            int count = SailmakerSession.GetPreviews().Count;
            foreach (ShipyardButton button in
                     __instance.GetComponentsInChildren<ShipyardButton>(true))
            {
                bool disable = button.function == ShipyardButton.ButtonFunction.cleanHull ||
                               button.function == ShipyardButton.ButtonFunction.repair ||
                               button.function == ShipyardButton.ButtonFunction.changeCategory ||
                               button.function == ShipyardButton.ButtonFunction.previousPart ||
                               button.function == ShipyardButton.ButtonFunction.nextPart ||
                               (button.function == ShipyardButton.ButtonFunction.selectSailCategory &&
                                button.index != (int)SailCategory.staysail);
                Collider collider = button.GetComponent<Collider>();
                Renderer renderer = button.GetComponent<Renderer>();
                if (disable)
                {
                    if (collider != null)
                    {
                        if (!ColliderStates.ContainsKey(collider))
                        {
                            ColliderStates[collider] = collider.enabled;
                        }
                        collider.enabled = false;
                    }
                    if (renderer != null)
                    {
                        if (!RendererStates.ContainsKey(renderer))
                        {
                            RendererStates[renderer] = renderer.sharedMaterial;
                        }
                        renderer.sharedMaterial = __instance.darkParchmentMaterial;
                    }
                }
                if (button.function == ShipyardButton.ButtonFunction.confirmOrder)
                {
                    if (!ButtonTexts.ContainsKey(button) &&
                        button.transform.childCount > 0)
                    {
                        TextMesh label = button.transform.GetChild(0)
                            .GetComponent<TextMesh>();
                        if (label != null)
                        {
                            ButtonTexts[button] = label.text;
                        }
                    }
                    button.SetText($"Buy Staysails ({count})");
                    GetLayout(button).ApplyConfirm();
                }
                else if (button.function == ShipyardButton.ButtonFunction.cancelOrder)
                {
                    GetLayout(button).ApplyCancel();
                }
            }
        }

        private static ButtonLayoutState GetLayout(ShipyardButton button)
        {
            if (!ButtonLayouts.TryGetValue(
                    button,
                    out ButtonLayoutState state))
            {
                state = new ButtonLayoutState(button);
                ButtonLayouts[button] = state;
            }
            return state;
        }

        internal static void RestoreUi()
        {
            foreach (KeyValuePair<Collider, bool> pair in ColliderStates)
            {
                if (pair.Key != null)
                {
                    pair.Key.enabled = pair.Value;
                }
            }
            foreach (KeyValuePair<Renderer, Material> pair in RendererStates)
            {
                if (pair.Key != null)
                {
                    pair.Key.sharedMaterial = pair.Value;
                }
            }
            foreach (KeyValuePair<ShipyardButton, string> pair in ButtonTexts)
            {
                if (pair.Key != null)
                {
                    pair.Key.SetText(pair.Value);
                }
            }
            foreach (ButtonLayoutState state in ButtonLayouts.Values)
            {
                state.Restore();
            }
            ColliderStates.Clear();
            RendererStates.Clear();
            ButtonTexts.Clear();
            ButtonLayouts.Clear();
        }

        private sealed class ButtonLayoutState
        {
            private const float ConfirmWidthMultiplier = 1.45f;
            private const float CancelLeftOffset = 0.55f;

            private readonly Transform button;
            private readonly Transform label;
            private readonly Vector3 buttonLocalPosition;
            private readonly Vector3 buttonLocalScale;
            private readonly Vector3 labelLocalScale;

            internal ButtonLayoutState(ShipyardButton source)
            {
                button = source != null ? source.transform : null;
                label = button != null && button.childCount > 0
                    ? button.GetChild(0)
                    : null;
                buttonLocalPosition = button != null
                    ? button.localPosition
                    : Vector3.zero;
                buttonLocalScale = button != null
                    ? button.localScale
                    : Vector3.one;
                labelLocalScale = label != null
                    ? label.localScale
                    : Vector3.one;
            }

            internal void ApplyConfirm()
            {
                if (button == null)
                {
                    return;
                }
                button.localScale = new Vector3(
                    buttonLocalScale.x * ConfirmWidthMultiplier,
                    buttonLocalScale.y,
                    buttonLocalScale.z);
                if (label != null)
                {
                    label.localScale = new Vector3(
                        labelLocalScale.x / ConfirmWidthMultiplier,
                        labelLocalScale.y,
                        labelLocalScale.z);
                }
            }

            internal void ApplyCancel()
            {
                if (button != null)
                {
                    button.localPosition = buttonLocalPosition +
                                           Vector3.left * CancelLeftOffset;
                }
            }

            internal void Restore()
            {
                if (button != null)
                {
                    button.localPosition = buttonLocalPosition;
                    button.localScale = buttonLocalScale;
                }
                if (label != null)
                {
                    label.localScale = labelLocalScale;
                }
            }
        }
    }

    internal static class SailmakerSession
    {
        private static readonly FieldInfo CurrentOrderTotalField =
            AccessTools.Field(typeof(Shipyard), "currentOrderTotal");
        private static readonly FieldInfo CurrentOrderTextField =
            AccessTools.Field(typeof(Shipyard), "currentOrderText");
        private static readonly FieldInfo InstallErrorField =
            AccessTools.Field(typeof(Shipyard), "installError");
        private static readonly FieldInfo SelectSailButtonsField =
            AccessTools.Field(typeof(ShipyardUI), "selectSailButtons");
        private static readonly FieldInfo SailCountTextField =
            AccessTools.Field(typeof(ShipyardUI), "sailCountText");
        private static readonly Dictionary<Shipyard, Transform> DeliveryPoints =
            new Dictionary<Shipyard, Transform>();
        private static readonly List<SailmakerPreview> Previews =
            new List<SailmakerPreview>();
        private static readonly Dictionary<Mast, SailmakerMastRigState> RigStates =
            new Dictionary<Mast, SailmakerMastRigState>();

        internal static bool Pending { get; private set; }
        internal static Shipyard ActiveShipyard { get; private set; }
        internal static bool Active => ActiveShipyard != null &&
                                       GameState.currentShipyard == ActiveShipyard;

        internal static void RegisterDeliveryPoint(Shipyard shipyard, Transform point)
        {
            PruneDeliveryPoints();
            if (shipyard != null && point != null)
            {
                DeliveryPoints[shipyard] = point;
            }
        }

        internal static void Request(Shipyard shipyard)
        {
            Pending = shipyard != null;
        }

        internal static void TryBegin(Shipyard shipyard)
        {
            if (!Pending || shipyard == null ||
                GameState.currentShipyard != shipyard)
            {
                return;
            }
            StayInstallTargeting.Deactivate();
            Pending = false;
            ActiveShipyard = shipyard;
            ClearPreviews(false);
            SwappableStaysailPlugin.Log?.LogInfo(
                $"Opened Sail Maker at shipyard region {shipyard.region}.");
            RefreshOrder(shipyard);
            ShipyardUI ui = ShipyardUI.instance;
            if (ui != null)
            {
                // ShipyardUI remembers the last parts tab. Sail Maker only sells
                // sails, so always restore the sail workflow on entry.
                ui.ChangeMenuCategory(-1);
                ui.CloseSailCategoryMenu();
                ui.HideNewSailButtons();
                ui.RefreshButtons();
            }
        }

        internal static void End()
        {
            Pending = false;
            StayInstallTargeting.Deactivate();
            try
            {
                ClearPreviews(false);
            }
            catch (Exception exception)
            {
                SwappableStaysailPlugin.Log?.LogError(
                    $"Sail Maker cleanup failed during exit: {exception}");
            }
            finally
            {
                ActiveShipyard = null;
                SailmakerUiPatch.RestoreUi();
            }
        }

        internal static bool IsAllowedMast(Mast mast)
        {
            return Active && StaysailTargets.IsEligible(mast);
        }

        internal static bool IsAllowedSailPrefab(GameObject prefab)
        {
            Sail sail = prefab != null ? prefab.GetComponent<Sail>() : null;
            return sail != null && sail.category == SailCategory.staysail;
        }

        internal static IReadOnlyList<SailmakerPreview> GetPreviews()
        {
            Previews.RemoveAll(preview =>
                preview == null || preview.Mast == null || preview.Sail == null);
            return Previews;
        }

        internal static List<SailmakerPreview> GetPreviews(Mast mast)
        {
            return GetPreviews()
                .Where(preview => preview.Mast == mast)
                .ToList();
        }

        internal static bool IsPreview(Sail sail)
        {
            return sail != null && GetPreviews().Any(preview => preview.Sail == sail);
        }

        internal static bool AddPreview(GameObject prefab)
        {
            if (!Active || !IsAllowedSailPrefab(prefab))
            {
                return false;
            }
            Mast mast = ActiveShipyard.sailInstaller.GetCurrentMast();
            if (!IsAllowedMast(mast))
            {
                Notify("Select a supported staysail stay.");
                return false;
            }

            GameObject sailObject = null;
            try
            {
                sailObject = UnityEngine.Object.Instantiate(prefab);
                Sail sail = sailObject.GetComponent<Sail>();
                SailConnections connections = sailObject.GetComponent<SailConnections>();
                if (!CanSupplyPreviewControllers(mast, connections))
                {
                    UnityEngine.Object.Destroy(sailObject);
                    Notify("That stay does not have the required sail controllers.");
                    return false;
                }
                EnsurePreviewRigCapacity(mast, (mast.sails?.Count ?? 0) + 1);
                // Use the vanilla shipyard preview lifecycle. This registers the
                // temporary sail with the mast so every rope/controller gets the
                // same ownership and attachment updates as a normal order.
                ActiveShipyard.sailInstaller.AddNewSail(sailObject);
                Previews.Add(new SailmakerPreview(mast, sail));
                RefreshPreviewUi();
                SwappableStaysailPlugin.Log?.LogInfo(
                    $"Added registered Sail Maker preview '{sail.sailName}' for " +
                    $"{StaysailTargets.GetStayName(mast)}.");
                return true;
            }
            catch (Exception exception)
            {
                if (sailObject != null)
                {
                    if (mast.sails != null && mast.sails.Contains(sailObject))
                    {
                        mast.DetachSailFromMast(sailObject);
                    }
                    else
                    {
                        UnityEngine.Object.Destroy(sailObject);
                    }
                }
                RestorePreviewRigIfUnused(mast);
                SwappableStaysailPlugin.Log?.LogError(
                    $"Could not add Sail Maker preview: {exception}");
                Notify("Could not add that staysail.");
                return false;
            }
        }

        internal static bool SelectPreview(int index)
        {
            if (!Active)
            {
                return false;
            }
            Mast mast = ActiveShipyard.sailInstaller.GetCurrentMast();
            List<SailmakerPreview> previews = GetPreviews(mast);
            if (index < 0 || index >= previews.Count)
            {
                return false;
            }
            ActiveShipyard.sailInstaller.SelectSail(
                previews[index].Sail.gameObject);
            RefreshPreviewUi();
            return true;
        }

        internal static bool RemoveSelectedPreview()
        {
            if (!Active)
            {
                return false;
            }
            Sail selected = ActiveShipyard.sailInstaller.GetCurrentSail();
            SailmakerPreview preview = Previews.FirstOrDefault(
                candidate => candidate.Sail == selected);
            if (preview == null)
            {
                return false;
            }
            Previews.Remove(preview);
            ActiveShipyard.sailInstaller.SelectSail(null);
            DestroyPreview(preview);
            RestorePreviewRigIfUnused(preview.Mast);
            ActiveShipyard.sailInstaller.RecheckAllSailsCols();
            RefreshPreviewUi();
            return true;
        }

        internal static bool Cancel()
        {
            if (!Active)
            {
                return false;
            }
            ClearPreviews(true);
            return true;
        }

        internal static bool RefreshOrder(Shipyard shipyard)
        {
            if (!Active || shipyard != ActiveShipyard || ShipyardUI.instance == null)
            {
                return false;
            }
            IReadOnlyList<SailmakerPreview> previews = GetPreviews();
            ShipyardUIOrderText text = ShipyardUI.instance.shipyardOrderText;
            text.ResetOrderText();
            int total = 0;
            bool hasError = false;
            foreach (SailmakerPreview preview in previews)
            {
                bool error = false;
                string validation = ValidatePreview(
                    preview.Mast,
                    preview.Sail,
                    ref error);
                int price = Mathf.RoundToInt(
                    preview.Sail.GetSailPrice() * shipyard.GetCurrencyRate());
                total += price;
                hasError |= error;
                text.AddLine($"{price}: {preview.Sail.sailName}{validation}");
            }
            if (previews.Count > 0)
            {
                text.AddLine(" ");
            }
            text.SetTotalLine(
                $"TOTAL: {total} {PlayerGold.GetCurrencyName(shipyard.region)}");
            text.DisplayCurrentText();

            CurrentOrderTotalField?.SetValue(shipyard, total);
            CurrentOrderTextField?.SetValue(
                shipyard,
                $"{previews.Count} spare staysail(s), total {total}");
            InstallErrorField?.SetValue(shipyard, hasError);
            return true;
        }

        internal static bool Buy()
        {
            if (!Active)
            {
                return false;
            }
            List<SailmakerPreview> previews = GetPreviews().ToList();
            if (previews.Count == 0)
            {
                Notify("Add at least one staysail first.");
                return false;
            }

            int total = 0;
            List<SailpackRecord> records = new List<SailpackRecord>();
            List<Mast> targets = new List<Mast>();
            foreach (SailmakerPreview preview in previews)
            {
                bool error = false;
                string validation = ValidatePreview(
                    preview.Mast,
                    preview.Sail,
                    ref error);
                if (error)
                {
                    Notify("A staysail cannot be packed: " + validation.Trim());
                    return false;
                }
                records.Add(SailpackRecord.FromSail(preview.Sail, preview.Mast));
                targets.Add(preview.Mast);
                total += Mathf.RoundToInt(
                    preview.Sail.GetSailPrice() * ActiveShipyard.GetCurrencyRate());
            }
            if (PlayerGold.currency[ActiveShipyard.region] < total)
            {
                Notify("Not enough money.");
                return false;
            }

            Transform delivery = GetDeliveryPoint(ActiveShipyard) ??
                                 ActiveShipyard.editedShipPosition;
            if (delivery == null)
            {
                Notify("Could not find the Sail Maker delivery point.");
                return false;
            }
            List<SailpackData> staged = new List<SailpackData>();
            bool committed = false;
            try
            {
                for (int i = 0; i < records.Count; i++)
                {
                    Vector3 position = delivery.position +
                        delivery.right * (0.45f * (i % 3)) +
                        delivery.forward * (0.4f * (i / 3));
                    SailpackData pack = SailpackFactory.CreateStaged(
                        records[i],
                        position,
                        delivery.rotation,
                        false,
                        targets[i]);
                    if (pack == null)
                    {
                        throw new InvalidOperationException(
                            "A staged sail package could not be created.");
                    }
                    staged.Add(pack);
                }

                foreach (SailpackData pack in staged)
                {
                    SaveablePrefab saveable = pack.GetComponent<SaveablePrefab>();
                    if (saveable == null)
                    {
                        throw new InvalidOperationException(
                            "A staged sail package has no save component.");
                    }
                    saveable.RegisterToSave();
                    SailpackPersistence.Register(saveable.instanceId, pack.Record);
                }

                // Finish all preview/UI work before charging. If any of this
                // fails, the staged packs can still be rolled back cleanly.
                ClearPreviews(true);
                foreach (SailpackData pack in staged)
                {
                    SailpackFactory.ActivateStaged(pack);
                }
                PlayerGold.currency[ActiveShipyard.region] -= total;
                committed = true;
            }
            catch (Exception exception)
            {
                if (!committed)
                {
                    foreach (SailpackData pack in staged)
                    {
                        if (pack == null)
                        {
                            continue;
                        }
                        SailpackFactory.DiscardStaged(pack, true);
                    }
                }
                SwappableStaysailPlugin.Log?.LogError(
                    $"Sail package purchase failed before commit: {exception}");
                Notify("Could not create the sail packages; nothing was charged.");
                return false;
            }

            UISoundPlayer.instance?.PlayGoldSound();
            try
            {
                DayLogs.instance?.dayLogs[ActiveShipyard.region]
                    .LogTransaction(-total, TransactionCategory.boat);
            }
            catch (Exception logException)
            {
                SwappableStaysailPlugin.Log?.LogWarning(
                    $"Sail package purchase succeeded but day-log entry failed: " +
                    logException);
            }
            Notify($"Created {staged.Count} sail package(s).");
            SwappableStaysailPlugin.Log?.LogInfo(
                $"Purchased {staged.Count} spare sail package(s) for {total}; " +
                "installed staysails were unchanged.");
            return true;
        }

        internal static void RenderPreviewList(ShipyardUI ui)
        {
            GameObject[] buttons =
                SelectSailButtonsField?.GetValue(ui) as GameObject[];
            if (buttons == null)
            {
                return;
            }
            Mast mast = ActiveShipyard?.sailInstaller.GetCurrentMast();
            List<SailmakerPreview> previews = GetPreviews(mast);
            for (int i = 0; i < buttons.Length; i++)
            {
                bool visible = i < previews.Count && previews[i].Sail != null;
                buttons[i].SetActive(visible);
                ShipyardButton button = buttons[i].GetComponent<ShipyardButton>();
                button.SetText(visible ? previews[i].Sail.sailName : "-");
            }
        }

        internal static void RenderPreviewSelections(ShipyardUI ui)
        {
            GameObject[] buttons =
                SelectSailButtonsField?.GetValue(ui) as GameObject[];
            if (buttons == null)
            {
                return;
            }
            Mast mast = ActiveShipyard?.sailInstaller.GetCurrentMast();
            List<SailmakerPreview> previews = GetPreviews(mast);
            Sail selected = ActiveShipyard?.sailInstaller.GetCurrentSail();
            for (int i = 0; i < buttons.Length; i++)
            {
                bool isSelected = i < previews.Count &&
                                  previews[i].Sail == selected;
                buttons[i].GetComponent<Renderer>().sharedMaterial = isSelected
                    ? ui.selectedButtonMaterial
                    : ui.defaultButtonMaterial;
            }
        }

        internal static void HideSailCount(ShipyardUI ui)
        {
            TextMesh text = SailCountTextField?.GetValue(ui) as TextMesh;
            if (text != null)
            {
                text.text = "";
                text.color = Color.black;
            }
        }

        internal static void Dispose()
        {
            try
            {
                End();
            }
            finally
            {
                DeliveryPoints.Clear();
                Previews.Clear();
                RigStates.Clear();
            }
        }

        private static Transform GetDeliveryPoint(Shipyard shipyard)
        {
            PruneDeliveryPoints();
            return shipyard != null &&
                   DeliveryPoints.TryGetValue(shipyard, out Transform point) &&
                   point != null
                ? point
                : null;
        }

        private static void PruneDeliveryPoints()
        {
            foreach (Shipyard stale in DeliveryPoints
                         .Where(pair => pair.Key == null || pair.Value == null)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                DeliveryPoints.Remove(stale);
            }
        }

        private static string ValidatePreview(
            Mast mast,
            Sail sail,
            ref bool error)
        {
            if (!StaysailTargets.IsEligible(mast))
            {
                error = true;
                return " (NOT A SUPPORTED STAY)";
            }
            if (sail == null || sail.category != SailCategory.staysail)
            {
                error = true;
                return " (NOT A STAYSAIL)";
            }
            float bottom = sail.GetCurrentInstallHeight() - sail.GetScaledHeight();
            if (sail.GetScaledHeight() > mast.mastHeight || bottom < -0.1f)
            {
                error = true;
                return " (DOES NOT FIT STAY)";
            }
            return "";
        }

        private static void ClearPreviews(bool refresh)
        {
            Shipyard shipyard = ActiveShipyard;
            Sail selected = shipyard?.sailInstaller != null
                ? shipyard.sailInstaller.GetCurrentSail()
                : null;
            bool selectedWasPreview = Previews.Any(
                preview => preview.Sail == selected);
            SailmakerPreview[] previews = Previews.ToArray();
            Previews.Clear();
            foreach (SailmakerPreview preview in previews)
            {
                DestroyPreview(preview);
            }
            foreach (Mast mast in RigStates.Keys.ToArray())
            {
                RestorePreviewRig(mast);
            }
            if (shipyard?.sailInstaller != null)
            {
                if (selected == null || selectedWasPreview)
                {
                    shipyard.sailInstaller.SelectSail(null);
                }
                shipyard.sailInstaller.RecheckAllSailsCols();
            }
            if (refresh && Active)
            {
                RefreshPreviewUi();
            }
        }

        private static void DestroyPreview(SailmakerPreview preview)
        {
            if (preview?.Sail == null)
            {
                return;
            }

            GameObject sailObject = preview.Sail.gameObject;
            Mast mast = preview.Mast;
            if (mast != null && mast.sails != null &&
                mast.sails.Contains(sailObject))
            {
                if (!StaysailDetacher.TryDetach(
                        mast,
                        preview.Sail,
                        out string error))
                {
                    SwappableStaysailPlugin.Log?.LogWarning(
                        $"Could not fully detach Sail Maker preview: {error}");
                    mast.sails.Remove(sailObject);
                    UnityEngine.Object.Destroy(sailObject);
                }
            }
            else
            {
                UnityEngine.Object.Destroy(sailObject);
            }
        }

        private static bool CanSupplyPreviewControllers(
            Mast mast,
            SailConnections connections)
        {
            if (mast == null || connections == null)
            {
                return false;
            }
            if (connections.reefController != null &&
                (mast.reefWinch == null || mast.reefWinch.Length == 0))
            {
                return false;
            }
            if (connections.angleControllerMid != null &&
                (mast.midAngleWinch == null || mast.midAngleWinch.Length == 0))
            {
                return false;
            }
            if ((connections.angleControllerLeft != null ||
                 connections.angleControllerRight != null) &&
                (mast.leftAngleWinch == null || mast.leftAngleWinch.Length == 0 ||
                 mast.rightAngleWinch == null || mast.rightAngleWinch.Length == 0))
            {
                return false;
            }
            if (connections.mastReefAttachment != null &&
                (mast.mastReefAtt == null || mast.mastReefAtt.Length == 0))
            {
                return false;
            }
            if (connections.midRopeAttachment != null &&
                (mast.midRopeAtt == null || mast.midRopeAtt.Length == 0) &&
                (mast.midAngleWinch == null || mast.midAngleWinch.Length == 0))
            {
                return false;
            }
            return true;
        }

        private static void EnsurePreviewRigCapacity(Mast mast, int count)
        {
            if (!RigStates.TryGetValue(mast, out SailmakerMastRigState state))
            {
                state = new SailmakerMastRigState(mast);
                RigStates[mast] = state;
            }
            state.EnsureCapacity(mast, count);
        }

        private static void RestorePreviewRigIfUnused(Mast mast)
        {
            if (mast != null && !Previews.Any(preview => preview.Mast == mast))
            {
                RestorePreviewRig(mast);
            }
        }

        private static void RestorePreviewRig(Mast mast)
        {
            if (mast == null ||
                !RigStates.TryGetValue(mast, out SailmakerMastRigState state))
            {
                return;
            }
            state.Restore(mast);
            RigStates.Remove(mast);
            mast.UpdateSailOrder();
            mast.UpdateControllerAttachments();
        }

        private static void RefreshPreviewUi()
        {
            if (!Active)
            {
                return;
            }
            RefreshOrder(ActiveShipyard);
            ShipyardUI.instance?.RefreshButtons();
        }

        private static void Notify(string message)
        {
            NotificationUi.instance?.ShowNotification(message);
        }
    }

    internal sealed class SailmakerPreview
    {
        internal Mast Mast { get; }
        internal Sail Sail { get; }

        internal SailmakerPreview(Mast mast, Sail sail)
        {
            Mast = mast;
            Sail = sail;
        }
    }

    internal sealed class SailmakerMastRigState
    {
        private readonly GPButtonRopeWinch[] leftAngleWinch;
        private readonly GPButtonRopeWinch[] rightAngleWinch;
        private readonly GPButtonRopeWinch[] midAngleWinch;
        private readonly GPButtonRopeWinch[] reefWinch;
        private readonly Transform[] midRopeAtt;
        private readonly Transform[] mastReefAtt;
        private readonly Transform[] mastReefAttExtension;

        internal SailmakerMastRigState(Mast mast)
        {
            leftAngleWinch = mast.leftAngleWinch;
            rightAngleWinch = mast.rightAngleWinch;
            midAngleWinch = mast.midAngleWinch;
            reefWinch = mast.reefWinch;
            midRopeAtt = mast.midRopeAtt;
            mastReefAtt = mast.mastReefAtt;
            mastReefAttExtension = mast.mastReefAttExtension;
        }

        internal void EnsureCapacity(Mast mast, int count)
        {
            mast.leftAngleWinch = Expand(leftAngleWinch, count);
            mast.rightAngleWinch = Expand(rightAngleWinch, count);
            mast.midAngleWinch = Expand(midAngleWinch, count);
            mast.reefWinch = Expand(reefWinch, count);
            mast.midRopeAtt = Expand(midRopeAtt, count);
            mast.mastReefAtt = Expand(mastReefAtt, count);
            mast.mastReefAttExtension = Expand(mastReefAttExtension, count);
        }

        internal void Restore(Mast mast)
        {
            mast.leftAngleWinch = leftAngleWinch;
            mast.rightAngleWinch = rightAngleWinch;
            mast.midAngleWinch = midAngleWinch;
            mast.reefWinch = reefWinch;
            mast.midRopeAtt = midRopeAtt;
            mast.mastReefAtt = mastReefAtt;
            mast.mastReefAttExtension = mastReefAttExtension;
        }

        private static T[] Expand<T>(T[] source, int count)
        {
            if (source == null || source.Length == 0 || source.Length >= count)
            {
                return source;
            }
            T[] expanded = new T[count];
            Array.Copy(source, expanded, source.Length);
            T fallback = source[source.Length - 1];
            for (int i = source.Length; i < expanded.Length; i++)
            {
                expanded[i] = fallback;
            }
            return expanded;
        }
    }

    internal sealed class SailmakerOrderButton : GoPointerButton
    {
        private Shipyard shipyard;

        internal void Initialize(Shipyard target)
        {
            shipyard = target;
            lookText = "Sail Maker";
        }

        public override void ExtraLateUpdate()
        {
            if (shipyard == null)
            {
                lookText = "";
            }
            else if (shipyard.GetBoatsInTriggerCount() < 1)
            {
                lookText = "(no boat in the Sail Maker area)";
            }
            else if (shipyard.GetBoatsInTriggerCount() > 1)
            {
                lookText = "(too many boats in the Sail Maker area)";
            }
            else
            {
                lookText = "Sail Maker";
            }
        }

        public override void OnActivate()
        {
            if (shipyard == null || GameState.currentShipyard != null ||
                shipyard.GetBoatsInTriggerCount() != 1)
            {
                return;
            }
            SailmakerSession.Request(shipyard);
            shipyard.ActivateDocuments();
        }
    }
}
