using System;
using System.Collections;
using System.Collections.Generic;
using cakeslice;
using UnityEngine;

namespace SwappableStaysail
{
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
}

