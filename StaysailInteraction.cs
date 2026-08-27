using System.Collections.Generic;
using cakeslice;
using UnityEngine;

namespace SwappableStaysail
{

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
}
