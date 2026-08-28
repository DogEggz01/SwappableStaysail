using System;
using UnityEngine;

namespace SwappableStaysail
{
    internal sealed class SailpackRecord
    {
        public int sailPrefabIndex;
        public int sailColor;
        public float scaleY = 1f;
        public float scaleZ = 1f;
        public float packageMass = SailpackWeight.MinimumPackageMass;
        public float installHeight;
        public float minAngle;
        public float maxAngle;
        public int targetBoatSceneIndex;
        public int targetMastOrderIndex;
        public string targetStayName;
        public string sailName;

        internal static SailpackRecord FromSail(Sail sail, Mast mast)
        {
            return new SailpackRecord
            {
                sailPrefabIndex = sail.prefabIndex,
                sailColor = sail.activeColor,
                scaleY = sail.GetScaleY(),
                scaleZ = sail.GetScaleZ(),
                packageMass = SailpackWeight.FromSail(sail),
                installHeight = sail.GetCurrentInstallHeight(),
                minAngle = sail.minAngle,
                maxAngle = sail.maxAngle,
                targetBoatSceneIndex = StaysailTargets.GetBoatSceneIndex(mast),
                targetMastOrderIndex = mast.orderIndex,
                targetStayName = StaysailTargets.GetStayName(mast),
                sailName = string.IsNullOrEmpty(sail.sailName)
                    ? sail.gameObject.name
                    : sail.sailName
            };
        }

        internal string GetDisplayName()
        {
            string stay = string.IsNullOrEmpty(targetStayName)
                ? "Staysail stay"
                : targetStayName;
            string sail = string.IsNullOrEmpty(sailName)
                ? "Staysail"
                : sailName;
            if (HasScalePercentageSuffix(sail))
            {
                return $"{stay} {sail}";
            }

            int scaleYPercentage = Mathf.RoundToInt(scaleY * 100f);
            int scaleZPercentage = Mathf.RoundToInt(scaleZ * 100f);
            return $"{stay} {sail} " +
                   $"({scaleYPercentage}%x{scaleZPercentage}%)";
        }

        private static bool HasScalePercentageSuffix(string value)
        {
            if (string.IsNullOrEmpty(value) || value[value.Length - 1] != ')')
            {
                return false;
            }

            int opening = value.LastIndexOf(" (", StringComparison.Ordinal);
            if (opening < 0)
            {
                return false;
            }

            string scale = value.Substring(
                opening + 2,
                value.Length - opening - 3);
            int separator = scale.IndexOf('x');
            if (separator < 0)
            {
                return IsWholePercentage(scale);
            }
            if (scale.IndexOf('x', separator + 1) >= 0)
            {
                return false;
            }

            return IsWholePercentage(scale.Substring(0, separator)) &&
                   IsWholePercentage(scale.Substring(separator + 1));
        }

        private static bool IsWholePercentage(string value)
        {
            if (string.IsNullOrEmpty(value) ||
                value[value.Length - 1] != '%')
            {
                return false;
            }

            return int.TryParse(
                value.Substring(0, value.Length - 1),
                out int percentage) &&
                percentage >= 0;
        }

        internal SailpackRecord Copy()
        {
            return (SailpackRecord)MemberwiseClone();
        }

        internal void Normalize()
        {
            if (float.IsNaN(packageMass) || float.IsInfinity(packageMass) ||
                packageMass <= 0f)
            {
                packageMass = SailpackWeight.MinimumPackageMass;
            }
        }
    }

    internal static class SailpackWeight
    {
        internal const float MinimumPackageMass = 0.1f;

        internal static float FromSail(Sail sail)
        {
            if (sail == null)
            {
                return MinimumPackageMass;
            }

            // Match NAND Tweaks' shipyard weight calculation. For staysails the
            // displayed weight is GetRealSailPower() * 20. Capturing it now keeps
            // the package stable even if sail balance settings change later.
            float baseMass = Mathf.Max(0f, sail.GetRealSailPower() * 20f);
            float categoryMass;
            if (sail.category == SailCategory.junk ||
                sail.category == SailCategory.gaff)
            {
                categoryMass = baseMass;
            }
            else if (sail.category == SailCategory.staysail)
            {
                categoryMass = 0f;
            }
            else
            {
                categoryMass = baseMass * 0.5f;
            }
            return Mathf.Max(MinimumPackageMass, baseMass + categoryMass);
        }
    }
}
