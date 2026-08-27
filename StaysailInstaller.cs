using System;
using UnityEngine;

namespace SwappableStaysail
{
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
}
