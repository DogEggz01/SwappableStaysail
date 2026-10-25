using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
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
    internal static class SailmakerDeliveryPointPatch
    {
        [HarmonyPostfix]
        private static void Postfix(ShipyardDocuments __instance)
        {
            if (__instance == null || __instance.shipyard == null || __instance.GetComponent<SailmakerDeliveryInstalled>() != null)
            {
                return;
            }
            __instance.gameObject.AddComponent<SailmakerDeliveryInstalled>();
            try
            {
                Transform transform = __instance.transform;
                GameObject gameObject = new GameObject("sail maker delivery anchor");
                gameObject.transform.SetParent(transform.parent, false);
                gameObject.transform.localScale = transform.localScale;
                gameObject.transform.rotation = transform.rotation;
                Vector3 position;
                Vector3 euler;
                if (SailmakerDeliveryPointPatch.TryGetAuthoredPose(__instance.gameObject.scene.name, out position, out euler))
                {
                    gameObject.transform.position = transform.TransformPoint(position);
                    gameObject.transform.rotation = transform.rotation * Quaternion.Euler(euler);
                }
                else
                {
                    gameObject.transform.position = transform.position + SailmakerDeliveryPointPatch.GetFallbackOffset(__instance.GetComponent<Collider>(), transform);
                }
                GameObject gameObject2 = new GameObject("sail package delivery point");
                gameObject2.transform.SetParent(gameObject.transform, false);
                gameObject2.transform.localPosition = new Vector3(0f, 0.55f, 0.8f);
                SailmakerSession.RegisterDeliveryPoint(__instance.shipyard, gameObject2.transform);
            }
            catch (Exception arg)
            {
                ManualLogSource log = SwappableStaysailPlugin.Log;
                if (log != null)
                {
                    log.LogError(string.Format("Could not create the Sail Maker delivery point: {0}", arg));
                }
            }
        }

        private static Vector3 GetFallbackOffset(Collider collider, Transform source)
        {
            Vector3 surfaceLateral = SailmakerDeliveryPointPatch.GetSurfaceLateral(collider, source);
            Vector3 vector = surfaceLateral * (SailmakerDeliveryPointPatch.GetProjectedHalfExtent(collider, surfaceLateral) * 2f + 0.45f);
            if (collider == null)
            {
                return vector;
            }
            Bounds bounds = collider.bounds;
            Bounds bounds2 = bounds;
            for (int i = 0; i < 8; i++)
            {
                bounds2.center = bounds.center + vector;
                if (!bounds.Intersects(bounds2))
                {
                    break;
                }
                vector += surfaceLateral * 0.25f;
            }
            return vector;
        }

        private static bool TryGetAuthoredPose(string sceneName, out Vector3 localPosition, out Vector3 localEulerAngles)
        {
            if (sceneName == "island 1 A Gold Rock")
            {
                localPosition = new Vector3(-0.646f, -0.66f, 0f);
                localEulerAngles = new Vector3(-1.716f, 0f, -27.634f);
                return true;
            }
            if (sceneName == "island 9 E Dragon Cliffs")
            {
                localPosition = new Vector3(-0.267f, -0.758f, 0f);
                localEulerAngles = new Vector3(0f, 0f, 20.456f);
                return true;
            }
            if (sceneName == "island 15 M (Fort)")
            {
                localPosition = new Vector3(-0.993f, -1.933f, 0f);
                localEulerAngles = new Vector3(0f, 0f, -15.067f);
                return true;
            }
            if (!(sceneName == "island 27 Lagoon SwampShipyard"))
            {
                localPosition = Vector3.zero;
                localEulerAngles = Vector3.zero;
                return false;
            }
            localPosition = new Vector3(-0.699f, -0.891f, 0f);
            localEulerAngles = new Vector3(0f, 0f, -157.151f);
            return true;
        }

        private static float GetProjectedHalfExtent(Collider collider, Vector3 direction)
        {
            if (collider == null)
            {
                return 0.6f;
            }
            Vector3 extents = collider.bounds.extents;
            Vector3 normalized = direction.normalized;
            return Mathf.Abs(normalized.x) * extents.x + Mathf.Abs(normalized.y) * extents.y + Mathf.Abs(normalized.z) * extents.z;
        }

        private static Vector3 GetSurfaceLateral(Collider collider, Transform source)
        {
            Vector3 rhs = source.forward;
            BoxCollider boxCollider = collider as BoxCollider;
            if (boxCollider != null)
            {
                Vector3 size = boxCollider.size;
                if (size.x <= size.y && size.x <= size.z)
                {
                    rhs = source.right;
                }
                else if (size.y <= size.x && size.y <= size.z)
                {
                    rhs = source.up;
                }
            }
            Vector3 vector = Vector3.Cross(Vector3.up, rhs);
            if (vector.sqrMagnitude < 0.01f)
            {
                vector = Vector3.ProjectOnPlane(source.right, Vector3.up);
            }
            if (vector.sqrMagnitude < 0.01f)
            {
                vector = source.right;
            }
            vector.Normalize();
            if (Vector3.Dot(vector, source.right) < 0f)
            {
                vector = -vector;
            }
            return vector;
        }
    }

    internal sealed class SailmakerDeliveryInstalled : MonoBehaviour
    {
    }

    [HarmonyPatch(typeof(ShipyardUI), "Awake")]
    internal static class SailmakerModeButtonInstallPatch
    {
        [HarmonyPostfix]
        private static void Postfix(ShipyardUI __instance)
        {
            try
            {
                ShipyardButton shipyardButton = __instance.GetComponentsInChildren<ShipyardButton>(true).FirstOrDefault((ShipyardButton button) => button.function == ShipyardButton.ButtonFunction.cleanHull);
                if (shipyardButton == null)
                {
                    ManualLogSource log = SwappableStaysailPlugin.Log;
                    if (log != null)
                    {
                        log.LogError("Could not find the shipyard's Clean Ship button; the Sail Maker button was not added.");
                    }
                }
                else
                {
                    Transform transform = shipyardButton.transform;
                    GameObject gameObject = UnityEngine.Object.Instantiate<GameObject>(shipyardButton.gameObject, transform.parent);
                    gameObject.name = "shipyard ui button sail maker";
                    ShipyardButton[] componentsInChildren = gameObject.GetComponentsInChildren<ShipyardButton>(true);
                    for (int i = 0; i < componentsInChildren.Length; i++)
                    {
                        UnityEngine.Object.DestroyImmediate(componentsInChildren[i]);
                    }
                    Outline[] componentsInChildren2 = gameObject.GetComponentsInChildren<Outline>(true);
                    for (int i = 0; i < componentsInChildren2.Length; i++)
                    {
                        UnityEngine.Object.DestroyImmediate(componentsInChildren2[i]);
                    }
                    gameObject.transform.localRotation = transform.localRotation;
                    gameObject.transform.localScale = transform.localScale;
                    gameObject.transform.localPosition = transform.localPosition + Vector3.up * (transform.localScale.y + 0.3f);
                    TextMesh text = (gameObject.transform.childCount > 0) ? gameObject.transform.GetChild(0).GetComponent<TextMesh>() : null;
                    gameObject.AddComponent<SailmakerModeButton>().Initialize(text);
                }
            }
            catch (Exception arg)
            {
                ManualLogSource log2 = SwappableStaysailPlugin.Log;
                if (log2 != null)
                {
                    log2.LogError(string.Format("Could not add the Sail Maker button: {0}", arg));
                }
            }
        }

        private const float GapAboveCleanShip = 0.3f;
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
        private static void Prefix(Shipyard __instance)
        {
            SailmakerCollisionChecks.Cancel(__instance.sailInstaller);
            SailmakerSession.End();
        }

        [HarmonyPostfix]
        private static void Postfix(Shipyard __instance)
        {
            SailmakerCollisionChecks.Cancel(__instance.sailInstaller);
        }
    }

    [HarmonyPatch(typeof(ShipyardSailInstaller), nameof(ShipyardSailInstaller.RecheckAllSailsCols))]
    internal static class ShipyardCollisionRecheckPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(ShipyardSailInstaller __instance, Shipyard ___shipyard)
        {
            // Keep the native delays and sweep, but own one cancellable request.
            // Vanilla queues overlapping iterators over the live mast.sails lists.
            SailmakerCollisionChecks.Request(__instance, ___shipyard);
            return false;
        }
    }

    internal sealed class SailmakerCollisionChecks : MonoBehaviour
    {
        private Shipyard shipyard;
        private GameObject boat;
        private Coroutine worker;
        private int revision;
        private bool ready;

        internal static void Request(ShipyardSailInstaller installer, Shipyard yard)
        {
            if (installer == null) return;
            SailmakerCollisionChecks state = installer.GetComponent<SailmakerCollisionChecks>();
            if (yard == null || GameState.currentShipyard != yard || yard.GetCurrentBoat() == null)
            {
                if (state != null) state.Stop();
                return;
            }
            if (state == null) state = installer.gameObject.AddComponent<SailmakerCollisionChecks>();
            state.Stop();
            state.shipyard = yard;
            state.boat = yard.GetCurrentBoat();
            // Invalidate synchronously, before the first coroutine yield.
            state.worker = state.StartCoroutine(state.Check(state.revision));
        }

        internal static bool IsReady(Shipyard yard)
        {
            SailmakerCollisionChecks state = yard != null && yard.sailInstaller != null
                ? yard.sailInstaller.GetComponent<SailmakerCollisionChecks>() : null;
            return state != null && state.ready && state.IsCurrent(state.revision);
        }

        internal static void Cancel(ShipyardSailInstaller installer)
        {
            SailmakerCollisionChecks state = installer != null
                ? installer.GetComponent<SailmakerCollisionChecks>() : null;
            if (state != null) state.Stop();
        }

        private void Stop()
        {
            revision++;
            ready = false;
            if (worker != null) StopCoroutine(worker);
            worker = null;
            shipyard = null;
            boat = null;
        }

        private bool IsCurrent(int request)
        {
            return request == revision && shipyard != null && boat != null &&
                   GameState.currentShipyard == shipyard && shipyard.GetCurrentBoat() == boat;
        }

        private IEnumerator Check(int request)
        {
            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame();
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();
            if (!IsCurrent(request)) yield break;
            BoatRefs refs = boat.GetComponent<BoatRefs>();
            if (refs == null || refs.masts == null) yield break;

            // Snapshot active sails so adding/removing previews between physics
            // steps cannot invalidate a List enumerator or check a destroyed sail.
            Sail[] sails = refs.masts.Where(m => m != null && m.gameObject.activeInHierarchy && m.sails != null)
                .SelectMany(m => m.sails.ToArray())
                .Where(s => s != null && s.activeInHierarchy)
                .Select(s => s.GetComponent<Sail>()).Where(s => s != null).ToArray();
            List<ShipyardSailColChecker> checkers = new List<ShipyardSailColChecker>();
            foreach (Sail sail in sails)
            {
                if (!IsCurrent(request) || sail == null) yield break;
                SailConnections connections = sail.GetComponent<SailConnections>();
                ShipyardSailColChecker checker = connections != null ? connections.colChecker : null;
                if (checker == null) yield break;
                checkers.Add(checker);
                checker.RunColCheck();
                yield return new WaitForFixedUpdate();
                yield return new WaitForEndOfFrame();
            }
            while (IsCurrent(request) && checkers.Any(c => c != null && c.checking))
                yield return new WaitForFixedUpdate();
            // Returning the final checker to angle zero can produce a trigger
            // event in the next simulation. Let that event settle before Buy.
            yield return new WaitForFixedUpdate();
            yield return new WaitForEndOfFrame();
            if (!IsCurrent(request) || checkers.Any(c => c == null || c.checking || !c.readyToInstall))
                yield break;
            ready = true;
            worker = null;
            shipyard.UpdateOrder();
            if (ShipyardUI.instance != null) ShipyardUI.instance.RefreshButtons();
        }

        private void OnDisable() { Stop(); }
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

    [HarmonyPatch(typeof(WindCloth), "Update")]
    internal static class SailmakerHiddenClothPatch
    {
        [HarmonyPostfix]
        private static void Postfix(Sail ___sail, Renderer ___renderer)
        {
            if (___renderer != null && SailmakerSession.IsHiddenSail(___sail))
            {
                ___renderer.enabled = false;
            }
        }
    }

    [HarmonyPatch(typeof(ShipyardSailColChecker), "OnTriggerEnter")]
    internal static class SailmakerPreviewCollisionPatch
    {
        [HarmonyPostfix]
        private static void Postfix(ShipyardSailColChecker __instance, Collider other, Sail ___sail)
        {
            if (!SailmakerSession.Active || other == null || __instance.currentAngle != 0 || __instance.transform.position.y >= 900f || !SailmakerSession.IsPreview(___sail))
            {
                return;
            }
            GameObject gameObject = other.gameObject;
            if (gameObject.CompareTag("SailColCheckerSub") && gameObject.transform.parent != null)
            {
                gameObject = gameObject.transform.parent.gameObject;
            }
            if (gameObject == __instance.gameObject || !gameObject.CompareTag("SailColChecker"))
            {
                return;
            }
            ShipyardSailColChecker component = gameObject.GetComponent<ShipyardSailColChecker>();
            Sail sail;
            if (!(component != null))
            {
                sail = null;
            }
            else
            {
                FieldInfo sailField = SailmakerPreviewCollisionPatch.SailField;
                sail = (((sailField != null) ? sailField.GetValue(component) : null) as Sail);
            }
            Sail sail2 = sail;
            if (component == null || component.currentAngle != 0 || sail2 == null || sail2.category == SailCategory.staysail)
            {
                return;
            }
            __instance.collisionsWithSails++;
            SailmakerSession.RefreshOrder(GameState.currentShipyard);
        }

        private static readonly FieldInfo SailField = AccessTools.Field(typeof(ShipyardSailColChecker), "sail");
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

        private static readonly HashSet<Sail> HiddenSails = new HashSet<Sail>();
        private static readonly Dictionary<Renderer, bool> HiddenClothRenderers = new Dictionary<Renderer, bool>();

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
            HideInstalledSails(shipyard);
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
                ShowInstalledSails();
                ActiveShipyard = null;
                SailmakerUiPatch.RestoreUi();
            }
        }

        internal static bool IsHiddenSail(Sail sail)
        {
            return SailmakerSession.HiddenSails.Count > 0 && sail != null && SailmakerSession.HiddenSails.Contains(sail);
        }

        private static void HideInstalledSails(Shipyard shipyard)
        {
            SailmakerSession.ShowInstalledSails();
            GameObject currentBoat = shipyard.GetCurrentBoat();
            BoatRefs boatRefs = (currentBoat != null) ? currentBoat.GetComponent<BoatRefs>() : null;
            if (((boatRefs != null) ? boatRefs.masts : null) == null)
            {
                return;
            }
            foreach (Mast mast in boatRefs.masts)
            {
                if (!(mast == null) && mast.sails != null)
                {
                    foreach (GameObject gameObject in mast.sails)
                    {
                        Sail sail = (gameObject != null) ? gameObject.GetComponent<Sail>() : null;
                        if (!(sail == null) && sail.category == SailCategory.staysail && !SailmakerSession.IsPreview(sail))
                        {
                            SailmakerSession.HiddenSails.Add(sail);
                            WindCloth[] componentsInChildren = gameObject.GetComponentsInChildren<WindCloth>(true);
                            for (int j = 0; j < componentsInChildren.Length; j++)
                            {
                                Renderer component = componentsInChildren[j].GetComponent<Renderer>();
                                if (component != null && !SailmakerSession.HiddenClothRenderers.ContainsKey(component))
                                {
                                    SailmakerSession.HiddenClothRenderers[component] = component.enabled;
                                    component.enabled = false;
                                }
                            }
                        }
                    }
                }
            }
        }

        private static void ShowInstalledSails()
        {
            foreach (KeyValuePair<Renderer, bool> keyValuePair in SailmakerSession.HiddenClothRenderers)
            {
                if (keyValuePair.Key != null)
                {
                    keyValuePair.Key.enabled = keyValuePair.Value;
                }
            }
            SailmakerSession.HiddenClothRenderers.Clear();
            SailmakerSession.HiddenSails.Clear();
        }

        internal static void ToggleFromShipyardMenu()
        {
            Shipyard currentShipyard = GameState.currentShipyard;
            if (currentShipyard == null || currentShipyard.GetCurrentBoat() == null)
            {
                return;
            }
            if (SailmakerSession.Active)
            {
                if (SailmakerSession.GetPreviews().Count > 0)
                {
                    SailmakerSession.Notify("Buy or cancel the\nstaysails first.");
                    return;
                }
                SailmakerSession.End();
                SailmakerSession.ShowSailPage(currentShipyard);
                currentShipyard.UpdateOrder();
                ManualLogSource log = SwappableStaysailPlugin.Log;
                if (log == null)
                {
                    return;
                }
                log.LogInfo("Switched from Sail Maker back to the shipyard.");
                return;
            }
            else
            {
                if (SailmakerSession.HasUnconfirmedShipyardOrder(currentShipyard))
                {
                    SailmakerSession.Notify("Confirm or cancel the\ncurrent order first.");
                    return;
                }
                if (!SailmakerSession.HasSupportedStay(currentShipyard.GetCurrentBoat()))
                {
                    SailmakerSession.Notify("This ship has no\nsupported staysail stay.");
                    return;
                }
                currentShipyard.sailInstaller.SelectMast(null);
                SailmakerSession.Request(currentShipyard);
                SailmakerSession.TryBegin(currentShipyard);
                return;
            }
        }

        private static void ShowSailPage(Shipyard shipyard)
        {
            shipyard.sailInstaller.SelectMast(null);
            ShipyardUI instance = ShipyardUI.instance;
            if (instance != null)
            {
                instance.ChangeMenuCategory(-1);
                instance.CloseSailCategoryMenu();
                instance.HideNewSailButtons();
            }
        }

        private static bool HasUnconfirmedShipyardOrder(Shipyard shipyard)
        {
            if (shipyard.IncludeHullCleaning() || shipyard.IncludeRepair())
            {
                return true;
            }
            List<ShipyardRefund> currentRefunds = shipyard.GetCurrentRefunds();
            if (currentRefunds != null && currentRefunds.Count > 0)
            {
                return true;
            }
            GameObject currentBoat = shipyard.GetCurrentBoat();
            BoatRefs component = currentBoat.GetComponent<BoatRefs>();
            if (((component != null) ? component.masts : null) != null)
            {
                foreach (Mast mast in component.masts)
                {
                    if (!(mast == null) && mast.sails != null)
                    {
                        foreach (GameObject gameObject in mast.sails)
                        {
                            Sail sail = (gameObject != null) ? gameObject.GetComponent<Sail>() : null;
                            if (sail != null && !sail.IsInstalled())
                            {
                                return true;
                            }
                        }
                    }
                }
            }
            BoatCustomParts component2 = currentBoat.GetComponent<BoatCustomParts>();
            BoatPartsOrder boatPartsOrder = (shipyard.partsInstaller != null) ? shipyard.partsInstaller.GetCurrentOrder() : null;
            if (((component2 != null) ? component2.availableParts : null) != null && ((boatPartsOrder != null) ? boatPartsOrder.orderedOptions : null) != null)
            {
                int num = Math.Min(component2.availableParts.Count, boatPartsOrder.orderedOptions.Length);
                for (int j = 0; j < num; j++)
                {
                    if (component2.availableParts[j].activeOption != boatPartsOrder.orderedOptions[j])
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool HasSupportedStay(GameObject boat)
        {
            BoatRefs component = boat.GetComponent<BoatRefs>();
            if (((component != null) ? component.masts : null) != null)
            {
                return component.masts.Any((Mast mast) => mast != null && mast.gameObject.activeInHierarchy && StaysailTargets.IsEligible(mast));
            }
            return false;
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
            GameObject[] selectionButtons = ShipyardUI.instance != null
                ? SelectSailButtonsField?.GetValue(ShipyardUI.instance) as GameObject[] : null;
            int capacity = selectionButtons?.Length ?? 0;
            if (GetPreviews(mast).Count >= capacity)
            {
                Notify(capacity > 0
                    ? $"Order at most {capacity} staysails per stay.\nBuy or remove one first."
                    : "The staysail selection menu is unavailable.");
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
                ActiveShipyard.sailInstaller.RecheckAllSailsCols();
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

        private static string ValidatePreview(Mast mast, Sail sail, ref bool error)
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
            SailConnections connections = sail.GetComponent<SailConnections>();
            ShipyardSailColChecker checker = connections != null ? connections.colChecker : null;
            if (checker == null || !checker.readyToInstall || checker.checking ||
                !SailmakerCollisionChecks.IsReady(ActiveShipyard))
            {
                error = true;
                return " (checking collision...)";
            }
            if (checker.IsCollidingWithSail())
            {
                error = true;
                return " (SAILS COLLISION)";
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
                if (refresh) shipyard.sailInstaller.RecheckAllSailsCols();
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

    internal sealed class SailmakerModeButton : GoPointerButton
    {
        internal void Initialize(TextMesh text)
        {
            this.label = text;
            this.buttonRenderer = base.GetComponent<Renderer>();
            this.hasShownState = false;
            this.RefreshAppearance();
        }

        public override void ExtraLateUpdate()
        {
            this.RefreshAppearance();
        }

        public override void OnActivate()
        {
            UISoundPlayer instance = UISoundPlayer.instance;
            if (instance != null)
            {
                instance.PlayUISound(UISounds.buttonHover, 0.33f, 3f);
            }
            ShipyardUI instance2 = ShipyardUI.instance;
            if (instance2 != null)
            {
                instance2.HideNewSailButtons();
                instance2.CloseSailCategoryMenu();
            }
            SailmakerSession.ToggleFromShipyardMenu();
            this.RefreshAppearance();
            if (instance2 != null)
            {
                instance2.RefreshButtons();
            }
        }

        private void RefreshAppearance()
        {
            bool active = SailmakerSession.Active;
            if (this.hasShownState && active == this.shownActive)
            {
                return;
            }
            ShipyardUI instance = ShipyardUI.instance;
            if (this.label != null)
            {
                this.label.text = (active ? "X Sail Maker" : "Sail Maker");
            }
            if (this.buttonRenderer != null && instance != null)
            {
                this.buttonRenderer.sharedMaterial = (active ? instance.darkParchmentMaterial : instance.parchmentMaterial);
            }
            this.hasShownState = (instance != null);
            this.shownActive = active;
        }

        private TextMesh label;

        private Renderer buttonRenderer;

        private bool hasShownState;

        private bool shownActive;
    }
}
