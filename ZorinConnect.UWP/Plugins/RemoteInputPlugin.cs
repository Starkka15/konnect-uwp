using System;
using System.Collections.Generic;
using Windows.Foundation.Metadata;
using Windows.UI.Input.Preview.Injection;
using ZorinConnect.Core;

namespace ZorinConnect.Plugins
{
    /// <summary>
    /// Remote Input (SPEC T34+T35). Desktop controls the phone's mouse + keyboard via
    /// kdeconnect.mousepad.request, injected with Windows.UI.Input.Preview.Injection.InputInjector
    /// (inputInjectionBrokered confirmed OK by T31). Combines MouseReceiver + RemoteKeyboard since
    /// both ride the same packet type. Also advertises keyboard readiness (mousepad.keyboardstate).
    /// </summary>
    public sealed class RemoteInputPlugin : IPlugin
    {
        private const string RequestType = "kdeconnect.mousepad.request";
        private const string KeyboardStateType = "kdeconnect.mousepad.keyboardstate";
        private const string EchoType = "kdeconnect.mousepad.echo";

        private PluginContext _ctx;
        private InputInjector _injector;
        private double _scrollAccum;

        /// <summary>Phone-side mouse-move multiplier (KDE Connect applies sensitivity sender-side;
        /// this is an extra local tuning knob). Persisted in app settings.</summary>
        public static double Sensitivity
        {
            get => _sens;
            set { _sens = value < 0.1 ? 0.1 : (value > 5.0 ? 5.0 : value); Core.SettingsStore.Set(Core.SettingsStore.App, "mouseSensitivity", _sens); }
        }
        private static double _sens = LoadSens();
        private static double LoadSens()
        {
            var c = Core.SettingsStore.App;
            return c.Values.TryGetValue("mouseSensitivity", out var v) && v is double d ? d : 1.0;
        }

        public string Key => "RemoteInputPlugin";
        public string DisplayName => "Remote Input";
        public bool EnabledByDefault => true;
        public IEnumerable<string> SupportedPacketTypes => new[] { RequestType };
        public IEnumerable<string> OutgoingPacketTypes => new[] { KeyboardStateType, EchoType };

        public void OnCreate(PluginContext context)
        {
            _ctx = context;
            try
            {
                if (ApiInformation.IsTypePresent("Windows.UI.Input.Preview.Injection.InputInjector"))
                    _injector = InputInjector.TryCreate();
            }
            catch (Exception e) { _ctx?.Log?.Invoke($"input injector create failed: {e.Message}"); }

            // Tell the desktop we accept keyboard input.
            _ctx?.SendPacket(new NetworkPacket(KeyboardStateType).Set("state", _injector != null));
        }

        public void OnDestroy() { _injector = null; }

        public bool OnPacketReceived(NetworkPacket np)
        {
            if (np.Type != RequestType) return false;
            if (_injector == null) return true; // no injector -> consume silently
            try
            {
                if (np.Has("key") || np.Has("specialKey")) HandleKeyboard(np);
                else HandleMouse(np);
            }
            catch (Exception e) { _ctx?.Log?.Invoke($"inject failed: {e.Message}"); }
            return true;
        }

        // ---- mouse ----

        private void HandleMouse(NetworkPacket np)
        {
            bool scroll = np.GetBool("scroll");
            double dx = np.GetDouble("dx");
            double dy = np.GetDouble("dy");

            if (scroll)
            {
                _scrollAccum += dy;
                int detents = (int)(_scrollAccum / 3.0);
                if (detents != 0)
                {
                    _scrollAccum -= detents * 3.0;
                    Inject(new InjectedInputMouseInfo
                    {
                        MouseData = unchecked((uint)(detents * 120)), // WHEEL_DELTA
                        MouseOptions = InjectedInputMouseOptions.Wheel,
                    });
                }
                return;
            }

            if (dx != 0 || dy != 0)
            {
                Inject(new InjectedInputMouseInfo
                {
                    DeltaX = (int)Math.Round(dx * _sens),
                    DeltaY = (int)Math.Round(dy * _sens),
                    MouseOptions = InjectedInputMouseOptions.MoveNoCoalesce,
                });
                return;
            }

            if (np.GetBool("singleclick")) Click(InjectedInputMouseOptions.LeftDown, InjectedInputMouseOptions.LeftUp);
            else if (np.GetBool("rightclick")) Click(InjectedInputMouseOptions.RightDown, InjectedInputMouseOptions.RightUp);
            else if (np.GetBool("middleclick")) Click(InjectedInputMouseOptions.MiddleDown, InjectedInputMouseOptions.MiddleUp);
            else if (np.GetBool("doubleclick"))
            {
                Click(InjectedInputMouseOptions.LeftDown, InjectedInputMouseOptions.LeftUp);
                Click(InjectedInputMouseOptions.LeftDown, InjectedInputMouseOptions.LeftUp);
            }
            else if (np.GetBool("singlehold")) Inject(new InjectedInputMouseInfo { MouseOptions = InjectedInputMouseOptions.LeftDown });
            else if (np.GetBool("singlerelease")) Inject(new InjectedInputMouseInfo { MouseOptions = InjectedInputMouseOptions.LeftUp });
        }

        private void Click(InjectedInputMouseOptions down, InjectedInputMouseOptions up)
        {
            Inject(new InjectedInputMouseInfo { MouseOptions = down });
            Inject(new InjectedInputMouseInfo { MouseOptions = up });
        }

        private void Inject(InjectedInputMouseInfo info) => _injector.InjectMouseInput(new[] { info });

        // ---- keyboard ----

        private void HandleKeyboard(NetworkPacket np)
        {
            bool ctrl = np.GetBool("ctrl"), shift = np.GetBool("shift"), alt = np.GetBool("alt"), sup = np.GetBool("super");
            var mods = new List<ushort>();
            if (ctrl) mods.Add(0x11);   // VK_CONTROL
            if (shift) mods.Add(0x10);  // VK_SHIFT
            if (alt) mods.Add(0x12);    // VK_MENU
            if (sup) mods.Add(0x5B);    // VK_LWIN

            foreach (var m in mods) KeyDown(m);

            if (np.Has("specialKey"))
            {
                ushort vk = MapSpecialKey(np.GetInt("specialKey"));
                if (vk != 0) { KeyDown(vk); KeyUp(vk); }
            }
            else
            {
                // Unicode text -> ONE event per char (Android commits once via commitText). A
                // down+up pair emits the character TWICE on W10M ("double letters").
                foreach (var ch in np.GetString("key"))
                {
                    _injector.InjectKeyboardInput(new[]
                    {
                        new InjectedInputKeyboardInfo { ScanCode = ch, KeyOptions = InjectedInputKeyOptions.Unicode },
                    });
                }
            }

            for (int i = mods.Count - 1; i >= 0; i--) KeyUp(mods[i]);

            if (np.GetBool("sendAck"))
            {
                var echo = new NetworkPacket(EchoType).Set("isAck", true);
                if (np.Has("key")) echo.Set("key", np.GetString("key"));
                if (np.Has("specialKey")) echo.Set("specialKey", np.GetInt("specialKey"));
                _ctx?.SendPacket(echo);
            }
        }

        private void KeyDown(ushort vk) =>
            _injector.InjectKeyboardInput(new[] { new InjectedInputKeyboardInfo { VirtualKey = vk } });

        private void KeyUp(ushort vk) =>
            _injector.InjectKeyboardInput(new[] { new InjectedInputKeyboardInfo { VirtualKey = vk, KeyOptions = InjectedInputKeyOptions.KeyUp } });

        /// <summary>Android SpecialKeysMap -> Win32 virtual-key codes.</summary>
        private static ushort MapSpecialKey(int sk)
        {
            switch (sk)
            {
                case 1: return 0x08;  // Backspace
                case 2: return 0x09;  // Tab
                case 4: return 0x25;  // Left
                case 5: return 0x26;  // Up
                case 6: return 0x27;  // Right
                case 7: return 0x28;  // Down
                case 8: return 0x21;  // PageUp
                case 9: return 0x22;  // PageDown
                case 10: return 0x24; // Home
                case 11: return 0x23; // End
                case 12: return 0x0D; // Enter
                case 13: return 0x2E; // Delete
                case 14: return 0x1B; // Esc
                case 15: return 0x2C; // PrintScreen
                case 16: return 0x91; // ScrollLock
                default:
                    if (sk >= 21 && sk <= 32) return (ushort)(0x70 + (sk - 21)); // F1..F12
                    return 0;
            }
        }
    }
}
