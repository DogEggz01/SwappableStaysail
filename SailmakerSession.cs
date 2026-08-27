using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SwappableStaysail
{
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

}
