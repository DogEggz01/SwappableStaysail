using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace SwappableStaysail
{
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
}

