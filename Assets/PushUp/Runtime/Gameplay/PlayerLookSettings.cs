using System;
using UnityEngine;

namespace PushUp.Gameplay
{
    /// <summary>Local, persistent presentation settings shared by standalone and network player cameras.</summary>
    public static class PlayerLookSettings
    {
        public const float DefaultMouseSensitivity = 0.12f;
        public const float DefaultControllerSensitivity = PlayerPhysics.ControllerLookSpeed;
        public const float MinimumMouseSensitivity = 0.02f;
        public const float MaximumMouseSensitivity = 0.50f;
        public const float MinimumControllerSensitivity = 60f;
        public const float MaximumControllerSensitivity = 540f;

        private const string MouseSensitivityKey = "PushUp.MouseLookSensitivity";
        private const string ControllerSensitivityKey = "PushUp.ControllerLookSensitivity";

        private static bool _loaded;
        private static float _mouseSensitivity;
        private static float _controllerSensitivity;

        public static event Action Changed;

        public static float MouseSensitivity
        {
            get
            {
                EnsureLoaded();
                return _mouseSensitivity;
            }
        }

        public static float ControllerSensitivity
        {
            get
            {
                EnsureLoaded();
                return _controllerSensitivity;
            }
        }

        public static void SetMouseSensitivity(float value)
        {
            EnsureLoaded();
            Set(ref _mouseSensitivity, MouseSensitivityKey, value, MinimumMouseSensitivity,
                MaximumMouseSensitivity);
        }

        public static void SetControllerSensitivity(float value)
        {
            EnsureLoaded();
            Set(ref _controllerSensitivity, ControllerSensitivityKey, value, MinimumControllerSensitivity,
                MaximumControllerSensitivity);
        }

        public static void ResetToDefaults()
        {
            EnsureLoaded();
            bool changed = false;
            changed |= SetValue(ref _mouseSensitivity, MouseSensitivityKey, DefaultMouseSensitivity,
                MinimumMouseSensitivity, MaximumMouseSensitivity);
            changed |= SetValue(ref _controllerSensitivity, ControllerSensitivityKey, DefaultControllerSensitivity,
                MinimumControllerSensitivity, MaximumControllerSensitivity);
            if (changed)
                Changed?.Invoke();
        }

        public static void Save()
        {
            EnsureLoaded();
            PlayerPrefs.Save();
        }

        private static void EnsureLoaded()
        {
            if (_loaded)
                return;
            _loaded = true;
            _mouseSensitivity = Clamp(PlayerPrefs.GetFloat(MouseSensitivityKey, DefaultMouseSensitivity),
                MinimumMouseSensitivity, MaximumMouseSensitivity);
            _controllerSensitivity = Clamp(
                PlayerPrefs.GetFloat(ControllerSensitivityKey, DefaultControllerSensitivity),
                MinimumControllerSensitivity, MaximumControllerSensitivity);
        }

        private static void Set(ref float currentValue, string key, float value, float minimum, float maximum,
            bool notify = true)
        {
            if (SetValue(ref currentValue, key, value, minimum, maximum) && notify)
                Changed?.Invoke();
        }

        private static bool SetValue(ref float currentValue, string key, float value, float minimum, float maximum)
        {
            float clamped = Clamp(value, minimum, maximum);
            if (Mathf.Approximately(currentValue, clamped))
                return false;
            currentValue = clamped;
            PlayerPrefs.SetFloat(key, clamped);
            return true;
        }

        private static float Clamp(float value, float minimum, float maximum) =>
            float.IsFinite(value) ? Mathf.Clamp(value, minimum, maximum) : minimum;
    }
}
