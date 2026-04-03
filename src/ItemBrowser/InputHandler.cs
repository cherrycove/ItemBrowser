using System;
using System.Reflection;

using UnityEngine;

namespace ItemBrowser;

internal static class InputHandler
{
    // ── 模块内部状态 ──
    private static bool _inputSystemChecked;
    private static bool _inputSystemAvailable;
    private static PropertyInfo? _inputSystemKeyboardCurrentProp;
    private static PropertyInfo? _inputSystemKeyboardItemProp;
    private static PropertyInfo? _inputSystemKeyControlPressedProp;
    private static Type? _inputSystemKeyType;
    private static bool _legacyInputAvailable = true;

    public static bool IsTogglePressed()
    {
        if (SharedState.ConfigToggleKey == null)
        {
            return false;
        }

        KeyCode key = SharedState.ConfigToggleKey.Value;

        if (TryGetInputSystemKeyDown(key, out bool pressedByInputSystem))
        {
            return pressedByInputSystem;
        }

        if (TryGetLegacyInputKeyDown(key, out bool pressedByLegacy))
        {
            return pressedByLegacy;
        }

        return false;
    }

    private static bool TryGetInputSystemKeyDown(KeyCode keyCode, out bool pressed)
    {
        pressed = false;

        if (!_inputSystemChecked)
        {
            _inputSystemAvailable = InitializeInputSystemReflection();
            _inputSystemChecked = true;
        }

        if (!_inputSystemAvailable) return false;

        try
        {
            var keyboard = _inputSystemKeyboardCurrentProp?.GetValue(null, null);
            if (keyboard == null) return true;

            var keyEnum = Enum.Parse(_inputSystemKeyType!, keyCode.ToString());
            var keyControl = _inputSystemKeyboardItemProp?.GetValue(keyboard, new[] { keyEnum });
            if (keyControl == null) return true;

            pressed = (bool)_inputSystemKeyControlPressedProp!.GetValue(keyControl, null)!;
            return true;
        }
        catch
        {
            _inputSystemAvailable = false;
            return false;
        }
    }

    private static bool InitializeInputSystemReflection()
    {
        try
        {
            var keyboardType = Type.GetType("UnityEngine.InputSystem.Keyboard, Unity.InputSystem");
            _inputSystemKeyType = Type.GetType("UnityEngine.InputSystem.Key, Unity.InputSystem");
            var keyControlType = Type.GetType("UnityEngine.InputSystem.Controls.KeyControl, Unity.InputSystem");
            if (keyboardType == null || _inputSystemKeyType == null || keyControlType == null) return false;

            _inputSystemKeyboardCurrentProp = keyboardType.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
            _inputSystemKeyboardItemProp = keyboardType.GetProperty("Item", BindingFlags.Public | BindingFlags.Instance);
            _inputSystemKeyControlPressedProp = keyControlType.GetProperty("wasPressedThisFrame", BindingFlags.Public | BindingFlags.Instance);

            return _inputSystemKeyboardCurrentProp != null
                && _inputSystemKeyboardItemProp != null
                && _inputSystemKeyControlPressedProp != null;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetLegacyInputKeyDown(KeyCode keyCode, out bool pressed)
    {
        pressed = false;
        if (!_legacyInputAvailable) return false;

        try
        {
            pressed = Input.GetKeyDown(keyCode);
            return true;
        }
        catch
        {
            _legacyInputAvailable = false;
            return false;
        }
    }
}
