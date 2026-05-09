// DIRECT360 – Universal Controller Remapper  (C# Edition)
// Supports: Xbox, PS3, PS4, PS5 (wired + wireless via BT/USB)
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using SharpDX.DirectInput;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

// Global crash logger – catches any unhandled exception and writes crash.log
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    // FIX: end timer period before exit so Windows timer resolution is restored
    if (App.PollModeAtCrash == PollMode.Performance)
        NativeMethods.timeEndPeriod(1);
    string msg = $"[{DateTime.Now}] CRASH: {e.ExceptionObject}\n";
    try { File.AppendAllText("crash.log", msg); } catch { }
    Console.WriteLine("\n  DIRECT360 crashed. Details saved to crash.log.");
    Console.WriteLine("  Press Enter to exit.");
    Console.ReadLine();
};

App.Run();

// ---------------------------------------------------------------------------
static class App
{
    const string ProfilesDir = "profiles";
    const int    RecentMax   = 5;

    // Exposed so the crash handler can call timeEndPeriod if needed
    public static PollMode PollModeAtCrash = PollMode.Balanced;

    // Known PS controller VendorIDs (Sony = 0x054C)
    static readonly HashSet<ushort> SonyVids = new() { 0x054C };

    // PS controller ProductIDs
    static readonly HashSet<ushort> SonyPids = new() { 0x0268, 0x05C4, 0x09CC, 0x0CE6, 0x0DF2 };

    static readonly string[] VirtualKeywords =
        { "xbox 360 for windows", "xinput", "vigem", "virtual", "x360" };

    static readonly (string Label, Xbox360Button Btn)[] XboxButtons =
    {
        ("A",     Xbox360Button.A),
        ("B",     Xbox360Button.B),
        ("X",     Xbox360Button.X),
        ("Y",     Xbox360Button.Y),
        ("LB",    Xbox360Button.LeftShoulder),
        ("RB",    Xbox360Button.RightShoulder),
        ("START", Xbox360Button.Start),
        ("BACK",  Xbox360Button.Back),
        ("L3",    Xbox360Button.LeftThumb),
        ("R3",    Xbox360Button.RightThumb),
    };

    static readonly Dictionary<string, Xbox360Button> DpadBtnMap = new()
    {
        ["(0, 1)"]  = Xbox360Button.Up,
        ["(0, -1)"] = Xbox360Button.Down,
        ["(-1, 0)"] = Xbox360Button.Left,
        ["(1, 0)"]  = Xbox360Button.Right,
    };

    static AppSettings _settings = new();
    static volatile bool _stop = false;

    // Thread-safety: DirectInput COM objects may not love concurrent Poll() across devices
    // from the same instance. We serialize all Poll/GetCurrentState calls.
    static readonly object _diPollLock = new();

    record PadInfo(Joystick Pad, string ProfileName, string DisplayName, int Slot,
                   Guid InstanceGuid, bool IsPlayStation);

    public static void Run()
    {
        _settings = AppSettings.Load();
        PollModeAtCrash = _settings.PollMode;
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; _stop = true; };

        // timeBeginPeriod only for Performance mode – prevents unnecessary heat
        if (_settings.PollMode == PollMode.Performance)
            NativeMethods.timeBeginPeriod(1);

        Banner();
        CheckViGEm();

        while (!_stop)
        {
            var pads = GetAllRealPads();
            if (_stop) break;

            if (pads.Count == 1)
                RunSingleController(pads[0]);
            else
                RunMultiController(pads);
        }

        if (_settings.PollMode == PollMode.Performance)
            NativeMethods.timeEndPeriod(1);

        Console.WriteLine("\n  Remapper stopped. Goodbye!");
        Thread.Sleep(1200);
    }

    static void Banner()
    {
        try { Console.Clear(); } catch { }
        Console.WriteLine("  " + new string('=', 43));
        Console.WriteLine("              DIRECT360");
        Console.WriteLine("      Universal Controller Remapper");
        Console.WriteLine("   Xbox / PS3 / PS4 / PS5  |  Wired & BT");
        Console.WriteLine("  " + new string('=', 43));
        Console.WriteLine($"  Mode: {_settings.PollMode}  |  Poll: ~{PollHz(_settings.PollMode)}Hz");
        Console.WriteLine();
    }

    static void CheckViGEm()
    {
        try { using var c = new ViGEmClient(); }
        catch
        {
            Console.WriteLine("  ERROR: ViGEmBus driver not found!");
            Console.WriteLine("  Install: https://github.com/nefarius/ViGEmBus/releases/latest");
            Console.WriteLine("  Restart after installing.");
            Console.ReadLine();
            Environment.Exit(1);
        }
    }

    static bool IsVirtual(string name)
    {
        string lower = name.ToLowerInvariant();
        return VirtualKeywords.Any(k => lower.Contains(k));
    }

    static bool IsPlayStation(DeviceInstance d)
    {
        byte[] bytes = d.ProductGuid.ToByteArray();
        ushort vid = (ushort)(bytes[1] << 8 | bytes[0]);
        ushort pid = (ushort)(bytes[3] << 8 | bytes[2]);
        return SonyVids.Contains(vid) && SonyPids.Contains(pid);
    }

    static readonly List<DirectInput> _activeDiInstances = new();

    static void DisposeActiveDi()
    {
        foreach (var di in _activeDiInstances) try { di.Dispose(); } catch { }
        _activeDiInstances.Clear();
    }

    static List<PadInfo> GetAllRealPads()
    {
        DisposeActiveDi();

        while (true)
        {
            List<PadInfo>? result = null;
            var di = new DirectInput();

            var devices = di
                .GetDevices(DeviceType.Gamepad,  DeviceEnumerationFlags.AllDevices)
                .Concat(di.GetDevices(DeviceType.Joystick, DeviceEnumerationFlags.AllDevices))
                .GroupBy(d => d.InstanceGuid).Select(g => g.First())
                .Where(d => !IsVirtual(d.ProductName))
                .Take(4).ToList();

            if (devices.Count > 0)
            {
                var nameCounts = new Dictionary<string, int>();
                result = new List<PadInfo>();

                foreach (var info in devices)
                {
                    var joy = new Joystick(di, info.InstanceGuid);
                    foreach (var obj in joy.GetObjects(DeviceObjectTypeFlags.AbsoluteAxis))
                        try { joy.GetObjectPropertiesById(obj.ObjectId).Range = new InputRange(-32768, 32767); }
                        catch { }

                    joy.Properties.BufferSize = 128;
                    try { joy.Acquire(); }
                    catch { joy.Dispose(); continue; }

                    string baseName = info.ProductName;
                    if (!nameCounts.ContainsKey(baseName)) nameCounts[baseName] = 0;
                    nameCounts[baseName]++;

                    string displayName = nameCounts[baseName] > 1
                        ? $"{baseName} #{nameCounts[baseName]}" : baseName;
                    string profileName = nameCounts[baseName] > 1
                        ? $"{baseName}_{info.InstanceGuid.ToString("N")[..8]}" : baseName;

                    bool isPS = IsPlayStation(info);
                    result.Add(new PadInfo(joy, profileName, displayName, result.Count + 1,
                                           info.InstanceGuid, isPS));
                }
            }

            if (result != null && result.Count > 0)
            {
                // FIX: only add di AFTER all joysticks are successfully acquired,
                // so DisposeActiveDi() won't pull the rug on live poll loops.
                _activeDiInstances.Add(di);
                string plural = result.Count == 1 ? "controller" : "controllers";
                Console.WriteLine($"  {result.Count} {plural} detected.");
                foreach (var p in result)
                    Console.WriteLine($"  Slot {p.Slot} : {p.DisplayName}{(p.IsPlayStation ? "  [PS]" : "")}");
                Console.WriteLine();
                return result;
            }

            try { di.Dispose(); } catch { }
            Console.WriteLine("  No controllers detected.");
            Console.Write("  Connect your controller and press Enter...");
            Console.ReadLine();
        }
    }

    // ---------------------------------------------------------------------------
    // SINGLE CONTROLLER FLOW
    // ---------------------------------------------------------------------------
    static void RunSingleController(PadInfo padInfo)
    {
        while (!_stop)
        {
            var profile = SelectLayout(padInfo.Pad, padInfo.ProfileName, padInfo.IsPlayStation);
            if (profile == null || _stop) break;

            Console.WriteLine();
            Console.WriteLine("  " + new string('=', 43));
            Console.WriteLine("  DIRECT360 is now running.");
            Console.WriteLine($"  Controller : {padInfo.DisplayName}");
            Console.WriteLine($"  Layout     : {profile.LayoutName}");
            Console.WriteLine($"  Sensitivity: {SensitivityLabel(profile.AntiDeadzone)} ({profile.AntiDeadzone})");
            Console.WriteLine($"  Poll rate  : ~{PollHz(_settings.PollMode)}Hz  [{_settings.PollMode}]");
            Console.WriteLine("  M = Back to Menu  |  Ctrl+C = Exit");
            Console.WriteLine("  " + new string('=', 43));
            Console.WriteLine();

            using var cts = new CancellationTokenSource();
            string result = RunRemapperCore(padInfo.Pad, profile, null, cts, isSingle: true);

            if (result == "exit" || _stop) break;

            if (result == "disconnected")
            {
                Console.WriteLine("\n  Controller disconnected. Waiting to reconnect...");
                Console.WriteLine("  Press Enter to go to menu instead.");

                bool ok = WaitForReconnect(padInfo.InstanceGuid, padInfo.ProfileName,
                    padInfo.DisplayName, padInfo.IsPlayStation, out PadInfo? newPad);

                // Dispose old joystick to free the device handle
                try { padInfo.Pad.Dispose(); } catch { }

                if (!ok || newPad == null || _stop) break;

                Console.WriteLine($"\n  Reconnected! Resuming [{profile.LayoutName}]...\n");
                padInfo = newPad;
                continue;
            }
        }
    }

    static bool WaitForReconnect(Guid targetGuid, string profileName, string displayName,
        bool isPS, out PadInfo? reconnected)
    {
        reconnected = null;

        // FIX: use CancellationTokenSource so the abort thread is properly cleaned up
        // when reconnect succeeds, instead of leaking a blocked ReadLine thread forever.
        using var abortCts = new CancellationTokenSource();
        bool userAborted = false;
        var abortTask = Task.Run(() =>
        {
            try { Console.ReadLine(); }
            catch { }
            userAborted = true;
        });

        while (!_stop && !userAborted)
        {
            Thread.Sleep(600);
            var di = new DirectInput();
            try
            {
                bool found = di
                    .GetDevices(DeviceType.Gamepad,  DeviceEnumerationFlags.AllDevices)
                    .Concat(di.GetDevices(DeviceType.Joystick, DeviceEnumerationFlags.AllDevices))
                    .Any(d => d.InstanceGuid == targetGuid);

                if (!found) { di.Dispose(); continue; }

                var joy = new Joystick(di, targetGuid);
                foreach (var obj in joy.GetObjects(DeviceObjectTypeFlags.AbsoluteAxis))
                    try { joy.GetObjectPropertiesById(obj.ObjectId).Range = new InputRange(-32768, 32767); }
                    catch { }
                joy.Properties.BufferSize = 128;
                try { joy.Acquire(); }
                catch { di.Dispose(); continue; }

                DisposeActiveDi();
                _activeDiInstances.Add(di);
                reconnected = new PadInfo(joy, profileName, displayName, 1, targetGuid, isPS);

                // Unblock the abort ReadLine thread by sending a newline to stdin.
                // This is the cleanest way to wake a blocked Console.ReadLine on Windows.
                try { NativeMethods.PostStdinNewline(); } catch { }

                return true;
            }
            catch { try { di.Dispose(); } catch { } }
        }
        return false;
    }

    // ---------------------------------------------------------------------------
    // MULTI-CONTROLLER FLOW
    // ---------------------------------------------------------------------------
    static void RunMultiController(List<PadInfo> pads)
    {
        var selected = SelectControllers(pads);
        if (selected.Count == 0 || _stop) return;
        if (selected.Count == 1) { RunSingleController(selected[0]); return; }

        bool allHaveLastUsed = selected.All(p => {
            string? last = LoadLastUsed(p.ProfileName);
            return last != null && ListProfiles(p.ProfileName).Contains(last);
        });

        if (allHaveLastUsed)
        {
            Console.WriteLine();
            Console.WriteLine("  All controllers have a previously used layout.");
            Console.Write("  [L] Quick load all last used  |  [Enter] Choose manually: ");
            if ((Console.ReadLine()?.Trim() ?? "").ToLower() == "l")
            {
                var assignments = new List<(PadInfo Pad, Profile Profile)>();
                foreach (var pad in selected)
                {
                    string last    = LoadLastUsed(pad.ProfileName)!;
                    var    profile = LoadProfile(pad.ProfileName, last);
                    if (profile == null) { Console.WriteLine($"  Could not load '{last}' for {pad.DisplayName}."); return; }
                    assignments.Add((pad, profile));
                }
                ShowRunningScreen(assignments);
                RunAllRemappers(assignments);
                return;
            }
        }

        var manual = new List<(PadInfo Pad, Profile Profile)>();
        for (int i = 0; i < selected.Count && !_stop; i++)
        {
            var pad = selected[i];
            Console.WriteLine();
            Console.WriteLine("  " + new string('=', 43));
            Console.WriteLine($"  PLAYER {pad.Slot} SETUP  --  {pad.DisplayName}");
            Console.WriteLine("  " + new string('=', 43));
            var profile = SelectLayout(pad.Pad, pad.ProfileName, pad.IsPlayStation);
            if (profile == null || _stop) return;
            manual.Add((pad, profile));
        }

        if (_stop || manual.Count == 0) return;
        ShowRunningScreen(manual);
        RunAllRemappers(manual);
    }

    static void ShowRunningScreen(List<(PadInfo Pad, Profile Profile)> assignments)
    {
        Console.WriteLine();
        Console.WriteLine("  " + new string('=', 43));
        Console.WriteLine("  DIRECT360 is now running.");
        Console.WriteLine("  " + new string('=', 43));
        foreach (var (pad, profile) in assignments)
            Console.WriteLine($"  P{pad.Slot}: {pad.DisplayName,-24} -> {profile.LayoutName}  ({SensitivityLabel(profile.AntiDeadzone)} {profile.AntiDeadzone})");
        Console.WriteLine("  " + new string('=', 43));
        Console.WriteLine("  M = Back to Menu  |  Ctrl+C = Exit");
        Console.WriteLine();
    }

    static List<PadInfo> SelectControllers(List<PadInfo> available)
    {
        Console.WriteLine("  " + new string('=', 43));
        Console.WriteLine($"  {available.Count} CONTROLLERS DETECTED");
        Console.WriteLine("  " + new string('=', 43));
        for (int i = 0; i < available.Count; i++)
            Console.WriteLine($"  [{i + 1}] Player {available[i].Slot} : {available[i].DisplayName}{(available[i].IsPlayStation ? " [PS]" : "")}");
        Console.WriteLine();
        Console.WriteLine("  [A] All  |  [S] Choose specific  |  [1-{0}] One only", available.Count);
        Console.WriteLine();
        Console.Write("  Choice: ");
        string c = Console.ReadLine()?.Trim() ?? "";
        if (c.ToLower() == "a") return available;
        if (c.ToLower() == "s")
        {
            Console.Write("  Numbers (e.g. 1 3): ");
            var sel = (Console.ReadLine() ?? "").Trim()
                .Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(p => int.TryParse(p, out int n) && n >= 1 && n <= available.Count)
                .Select(p => available[int.Parse(p) - 1]).Distinct().ToList();
            return sel.Count > 0 ? sel : available;
        }
        if (int.TryParse(c, out int idx) && idx >= 1 && idx <= available.Count)
            return new List<PadInfo> { available[idx - 1] };
        return available;
    }

    static void RunAllRemappers(List<(PadInfo Pad, Profile Profile)> controllers)
    {
        using var client = new ViGEmClient();

        // FIX: each controller gets its own CTS so one player pressing M
        // doesn't cancel everyone else's session.
        var ctsList = controllers.Select(_ => new CancellationTokenSource()).ToArray();

        var tasks = controllers.Select((c, idx) =>
            Task.Run(() => RunRemapperCore(c.Pad.Pad, c.Profile, client, ctsList[idx], isSingle: false))
        ).ToArray();

        while (!_stop && !tasks.All(t => t.IsCompleted))
        {
            if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.M)
            {
                Console.WriteLine("\n  Returning to menu...");
                foreach (var cts in ctsList) cts.Cancel();
                break;
            }
            Thread.Sleep(30); // Slower check loop – less CPU in main thread
        }

        if (_stop) foreach (var cts in ctsList) cts.Cancel();
        try { Task.WaitAll(tasks, 3000); } catch { }

        foreach (var cts in ctsList) cts.Dispose();

        // Report any failures so user knows what happened
        for (int i = 0; i < tasks.Length; i++)
        {
            if (tasks[i].IsFaulted)
                Console.WriteLine($"  P{i + 1} task faulted: {tasks[i].Exception?.InnerException?.Message}");
        }
    }

    // ---------------------------------------------------------------------------
    // UNIFIED REMAPPER CORE  (heat-optimized, thread-safe, array-safe)
    // ---------------------------------------------------------------------------
    static string RunRemapperCore(Joystick pad, Profile profile,
        ViGEmClient? sharedClient, CancellationTokenSource cts, bool isSingle)
    {
        ViGEmClient? ownedClient = isSingle ? new ViGEmClient() : null;
        var client  = ownedClient ?? sharedClient!;
        var gamepad = client.CreateXbox360Controller();
        gamepad.Connect();

        // Heat fix: Eco=10ms (~100Hz), Balanced=5ms (~200Hz), Performance=2ms (~500Hz)
        int sleepMs = _settings.PollMode switch
        {
            PollMode.Eco         => 10,
            PollMode.Performance => 2,
            _                    => 5,
        };

        var btnMap = new Dictionary<int, Xbox360Button>(XboxButtons.Length);
        foreach (var (label, btn) in XboxButtons)
            if (profile.Buttons.TryGetValue(label, out int? idx) && idx.HasValue && idx.Value >= 0)
                btnMap[idx.Value] = btn;

        int? ltIdx = profile.Triggers.TryGetValue("LT", out int? lt) && lt.HasValue && lt.Value >= 0 ? lt : null;
        int? rtIdx = profile.Triggers.TryGetValue("RT", out int? rt) && rt.HasValue && rt.Value >= 0 ? rt : null;

        bool usePsAnalogTriggers = profile.UseAnalogTriggers;
        // FIX: validate trigger axis indices are in valid range [0-5]
        int  ltAxis = profile.AnalogLTAxis >= 0 && profile.AnalogLTAxis <= 5 ? profile.AnalogLTAxis : 4;
        int  rtAxis = profile.AnalogRTAxis >= 0 && profile.AnalogRTAxis <= 5 ? profile.AnalogRTAxis : 5;

        var ls     = profile.LeftStick;
        var rs     = profile.RightStick;
        int antiDz = profile.AntiDeadzone;

        var dpadEntries = profile.DpadMode == "buttons"
            ? profile.DpadButtons
                .Where(kv => kv.Key != null && DpadBtnMap.ContainsKey(kv.Key))
                .Select(kv => (Btn: DpadBtnMap[kv.Key], Idx: kv.Value)).ToArray()
            : Array.Empty<(Xbox360Button, int)>();

        (int X, int Y) prevHat  = (0, 0);
        bool[]         prevBtns = Array.Empty<bool>();
        short prevLx = 0, prevLy = 0, prevRx = 0, prevRy = 0;
        byte  prevLt = 0, prevRt = 0;
        string exitReason = "exit";

        void ReleaseAll()
        {
            try
            {
                foreach (var btn in btnMap.Values) gamepad.SetButtonState(btn, false);
                gamepad.SetButtonState(Xbox360Button.Up,    false);
                gamepad.SetButtonState(Xbox360Button.Down,  false);
                gamepad.SetButtonState(Xbox360Button.Left,  false);
                gamepad.SetButtonState(Xbox360Button.Right, false);
                gamepad.SetSliderValue(Xbox360Slider.LeftTrigger,  0);
                gamepad.SetSliderValue(Xbox360Slider.RightTrigger, 0);
                gamepad.SetAxisValue(Xbox360Axis.LeftThumbX,  0);
                gamepad.SetAxisValue(Xbox360Axis.LeftThumbY,  0);
                gamepad.SetAxisValue(Xbox360Axis.RightThumbX, 0);
                gamepad.SetAxisValue(Xbox360Axis.RightThumbY, 0);
                gamepad.SubmitReport();
            }
            catch { }
        }

        try
        {
            while (!cts.IsCancellationRequested && !_stop)
            {
                JoystickState state;
                // Thread-safe poll: serialize DirectInput access to prevent COM races
                lock (_diPollLock)
                {
                    try { pad.Poll(); state = pad.GetCurrentState(); }
                    catch { exitReason = "disconnected"; break; }
                }

                // CRITICAL FIX: Clone the button array – SharpDX reuses the same buffer!
                bool[] currentBtns = state.Buttons.Length > 0 ? (bool[])state.Buttons.Clone() : Array.Empty<bool>();
                bool[] snapshotBtns = prevBtns;
                int    numBtns      = currentBtns.Length;
                bool   changed      = false;

                // -- M key → return to menu (single controller only) --
                if (isSingle && Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.M)
                {
                    Console.WriteLine("\n  Returning to menu...");
                    exitReason = "menu"; break;
                }

                // -- Buttons --
                foreach (var (idx, btn) in btnMap)
                {
                    if (idx < numBtns)
                    {
                        bool val = currentBtns[idx];
                        bool old = idx < snapshotBtns.Length && snapshotBtns[idx];
                        if (val != old) { gamepad.SetButtonState(btn, val); changed = true; }
                    }
                }

                // -- Triggers --
                if (usePsAnalogTriggers)
                {
                    byte newLt = AxisToByte(GetAxis(state, ltAxis));
                    byte newRt = AxisToByte(GetAxis(state, rtAxis));
                    if (newLt != prevLt) { gamepad.SetSliderValue(Xbox360Slider.LeftTrigger,  newLt); prevLt = newLt; changed = true; }
                    if (newRt != prevRt) { gamepad.SetSliderValue(Xbox360Slider.RightTrigger, newRt); prevRt = newRt; changed = true; }
                }
                else
                {
                    byte newLt = (ltIdx.HasValue && ltIdx < numBtns && currentBtns[ltIdx.Value]) ? (byte)255 : (byte)0;
                    byte newRt = (rtIdx.HasValue && rtIdx < numBtns && currentBtns[rtIdx.Value]) ? (byte)255 : (byte)0;
                    if (newLt != prevLt) { gamepad.SetSliderValue(Xbox360Slider.LeftTrigger,  newLt); prevLt = newLt; changed = true; }
                    if (newRt != prevRt) { gamepad.SetSliderValue(Xbox360Slider.RightTrigger, newRt); prevRt = newRt; changed = true; }
                }

                // -- Sticks --
                short lx = ScaleAxis(GetAxis(state, ls.XAxis) * ls.XPosSign, antiDz, ls.InnerDeadzone);
                short ly = ScaleAxis(GetAxis(state, ls.YAxis) * ls.YPosSign, antiDz, ls.InnerDeadzone);
                short rx = ScaleAxis(GetAxis(state, rs.XAxis) * rs.XPosSign, antiDz, rs.InnerDeadzone);
                short ry = ScaleAxis(GetAxis(state, rs.YAxis) * rs.YPosSign, antiDz, rs.InnerDeadzone);
                if (lx != prevLx) { gamepad.SetAxisValue(Xbox360Axis.LeftThumbX,  lx); prevLx = lx; changed = true; }
                if (ly != prevLy) { gamepad.SetAxisValue(Xbox360Axis.LeftThumbY,  ly); prevLy = ly; changed = true; }
                if (rx != prevRx) { gamepad.SetAxisValue(Xbox360Axis.RightThumbX, rx); prevRx = rx; changed = true; }
                if (ry != prevRy) { gamepad.SetAxisValue(Xbox360Axis.RightThumbY, ry); prevRy = ry; changed = true; }

                // -- D-pad --
                if (profile.DpadMode == "hat" && state.PointOfViewControllers.Length > 0)
                {
                    var hat = GetHat(state);
                    if (hat != prevHat)
                    {
                        gamepad.SetButtonState(Xbox360Button.Up,    hat.Y > 0);
                        gamepad.SetButtonState(Xbox360Button.Down,  hat.Y < 0);
                        gamepad.SetButtonState(Xbox360Button.Left,  hat.X < 0);
                        gamepad.SetButtonState(Xbox360Button.Right, hat.X > 0);
                        prevHat = hat; changed = true;
                    }
                }
                else if (profile.DpadMode == "buttons")
                {
                    foreach (var (dBtn, btnIdx) in dpadEntries)
                    {
                        if (btnIdx < numBtns)
                        {
                            bool val = currentBtns[btnIdx];
                            bool old = btnIdx < snapshotBtns.Length && snapshotBtns[btnIdx];
                            if (val != old) { gamepad.SetButtonState(dBtn, val); changed = true; }
                        }
                    }
                }

                prevBtns = currentBtns;
                if (changed)
                {
                    try { gamepad.SubmitReport(); }
                    catch { /* ViGEm device may have been removed – ignore and continue */ }
                }

                Thread.Sleep(sleepMs);
            }
        }
        finally
        {
            ReleaseAll();
            try { gamepad.Disconnect(); } catch { }
            ownedClient?.Dispose();
        }

        return exitReason;
    }

    // ---------------------------------------------------------------------------
    // AXIS & STICK HELPERS
    // ---------------------------------------------------------------------------
    static double GetAxis(JoystickState s, int idx) =>
        (double)(idx switch
        {
            0 => s.X, 1 => s.Y, 2 => s.Z,
            3 => s.RotationX, 4 => s.RotationY, 5 => s.RotationZ,
            _ => 0
        }) / 32767.0;

    // FIX: AxisToByte for trigger axes which output 0 to 1 range (0 to 32767 raw)
    static byte AxisToByte(double val) =>
        (byte)Math.Clamp(val * 255.0, 0, 255);
    
    // Old centered conversion (for reference, not used anymore):
    // (byte)Math.Clamp((val + 1.0) / 2.0 * 255.0, 0, 255);

    static short ScaleAxis(double val, int antiDeadzone, double innerDeadzone = 0.08)
    {
        if (Math.Abs(val) <= innerDeadzone) return 0;
        int    sign = val > 0 ? 1 : -1;
        double abs  = (Math.Abs(val) - innerDeadzone) / (1.0 - innerDeadzone);
        abs = Math.Clamp(abs, 0.0, 1.0);
        // FIX: guard against antiDeadzone=0 so negative sign still produces correct output
        int safeAntiDz = Math.Max(antiDeadzone, 1);
        double scaled = safeAntiDz + (abs * abs) * (32767 - safeAntiDz);
        return (short)Math.Clamp(scaled * sign, -32767, 32767);
    }

    static (int X, int Y) GetHat(JoystickState s)
    {
        if (s.PointOfViewControllers.Length == 0) return (0, 0);
        return s.PointOfViewControllers[0] switch
        {
            0     => ( 0,  1),  4500  => ( 1,  1),
            9000  => ( 1,  0),  13500 => ( 1, -1),
            18000 => ( 0, -1),  22500 => (-1, -1),
            27000 => (-1,  0),  31500 => (-1,  1),
            _     => ( 0,  0),
        };
    }

    // ---------------------------------------------------------------------------
    // WIZARD INPUT DETECTION  (reduced CPU from 1ms → 5ms sleep)
    // ---------------------------------------------------------------------------
    enum Cmd { None, Back, Redo, Disconnected }

    static Cmd CheckKey()
    {
        if (!Console.KeyAvailable) return Cmd.None;
        return Console.ReadKey(true).Key switch
        { ConsoleKey.B => Cmd.Back, ConsoleKey.R => Cmd.Redo, _ => Cmd.None };
    }

    static (bool IsCmd, Cmd CmdResult, int BtnIndex) DetectButtonOrCmd(Joystick pad)
    {
        bool[] baseline;
        lock (_diPollLock)
        {
            try { pad.Poll(); baseline = pad.GetCurrentState().Buttons.ToArray(); }
            catch { return (true, Cmd.Disconnected, -1); }
        }
        while (Console.KeyAvailable) Console.ReadKey(true);
        while (true)
        {
            Thread.Sleep(5); // was 1ms – reduced heat during wizard idle
            Cmd cmd = CheckKey();
            if (cmd != Cmd.None) return (true, cmd, -1);
            bool[] btns;
            lock (_diPollLock)
            {
                // FIX: clone the buffer — SharpDX reuses the same array on every call
                try { pad.Poll(); btns = (bool[])pad.GetCurrentState().Buttons.Clone(); }
                catch { return (true, Cmd.Disconnected, -1); }
            }
            for (int i = 0; i < btns.Length; i++)
            {
                if (!btns[i] || (i < baseline.Length && baseline[i])) continue;
                // Wait for release -- prevents ghost presses on next wizard step
                int t = 3000;
                while (t-- > 0)
                {
                    Thread.Sleep(5);
                    bool released = false;
                    lock (_diPollLock)
                    {
                        try { pad.Poll(); var cur = pad.GetCurrentState().Buttons; released = i >= cur.Length || !cur[i]; }
                        catch { break; }
                    }
                    if (released) break;
                }
                Thread.Sleep(80); // debounce
                return (false, Cmd.None, i);
            }
        }
    }

    static (bool IsCmd, Cmd CmdResult, int AxisIndex, int Sign) DetectAxisOrCmd(Joystick pad)
    {
        double[] baseline;
        lock (_diPollLock)
        {
            try { pad.Poll(); baseline = Enumerable.Range(0, 6).Select(i => GetAxis(pad.GetCurrentState(), i)).ToArray(); }
            catch { return (true, Cmd.Disconnected, -1, 0); }
        }
        while (Console.KeyAvailable) Console.ReadKey(true);
        while (true)
        {
            Thread.Sleep(5); // was 1ms
            Cmd cmd = CheckKey();
            if (cmd != Cmd.None) return (true, cmd, -1, 0);
            JoystickState state;
            lock (_diPollLock)
            {
                try { pad.Poll(); state = pad.GetCurrentState(); }
                catch { return (true, Cmd.Disconnected, -1, 0); }
            }
            for (int i = 0; i < 6; i++)
            {
                double val = GetAxis(state, i);
                // FIX: lower threshold from 0.5 to 0.35 so PS trigger presses at 35%+ are detected
                if (Math.Abs(val) <= 0.35 || Math.Abs(val - baseline[i]) <= 0.3) continue;
                int sign = val > 0 ? 1 : -1;
                int t = 3000;
                while (t-- > 0)
                {
                    Thread.Sleep(5); // was 1ms
                    lock (_diPollLock)
                    {
                        try { pad.Poll(); } catch { break; }
                        try { if (Math.Abs(GetAxis(pad.GetCurrentState(), i)) < 0.15) break; } catch { break; }
                    }
                }
                return (false, Cmd.None, i, sign);
            }
        }
    }

    static int PollHz(PollMode m) => m switch
    {
        PollMode.Eco         => 100,
        PollMode.Performance => 500,
        _                    => 200,
    };

    // ---------------------------------------------------------------------------
    // SENSITIVITY & DEADZONE
    // ---------------------------------------------------------------------------
    static string SensitivityLabel(int v) =>
        v >= 12000 ? "Light" : v >= 8000 ? "Normal" : "Firm";

    static int AskSensitivity(int currentVal = 10000)
    {
        Console.WriteLine();
        Console.WriteLine("  " + new string('=', 43));
        Console.WriteLine("  STICK SENSITIVITY");
        Console.WriteLine("  " + new string('=', 43));
        Console.WriteLine("  [1] Light  -- slightest touch registers  (12000-16000)");
        Console.WriteLine("  [2] Normal -- balanced for most games    ( 8000-11999)  <- default");
        Console.WriteLine("  [3] Firm   -- needs deliberate push      ( 4000- 7999)");
        Console.WriteLine();
        Console.WriteLine($"  Current: {SensitivityLabel(currentVal)} ({currentVal})");
        Console.Write("  Choose [1/2/3] then Enter to use default, or type exact value: ");
        string cat = Console.ReadLine()?.Trim() ?? "";

        int min, max, def;
        switch (cat) {
            case "1": min = 12000; max = 16000; def = 13000; break;
            case "3": min = 4000;  max = 7999;  def = 7000;  break;
            default:  min = 8000;  max = 11999; def = 10000; break;
        }

        Console.Write($"  Enter to use {def}, or type {min}-{max}: ");
        string inp = Console.ReadLine()?.Trim() ?? "";
        if (int.TryParse(inp, out int v) && v >= min && v <= max)
        { Console.WriteLine($"  Set to {SensitivityLabel(v)} ({v})"); return v; }
        Console.WriteLine($"  Set to {SensitivityLabel(def)} ({def})");
        return def;
    }

    static double AskInnerDeadzone(double current = 0.08)
    {
        Console.WriteLine($"  Inner deadzone -- current: {(int)(current * 100)}%");
        Console.WriteLine("  Range 0-30. Higher = more stick movement ignored (fixes drift).");
        Console.Write("  Enter 0-30 or press Enter to keep: ");
        string inp = Console.ReadLine()?.Trim() ?? "";
        return int.TryParse(inp, out int pct) && pct >= 0 && pct <= 30 ? pct / 100.0 : current;
    }

    static void AdjustSensitivity(Joystick pad, string ctrlName, List<string> existing)
    {
        if (existing.Count == 0) { Console.WriteLine("  No layouts."); return; }
        PickLayout(existing, out int idx);
        if (idx < 0) return;
        var profile = LoadProfile(ctrlName, existing[idx]);
        if (profile == null) return;
        profile.AntiDeadzone = AskSensitivity(profile.AntiDeadzone);
        Console.Write("  Adjust inner deadzone too? [y/N]: ");
        if (Console.ReadLine()?.Trim().ToLower() == "y")
        {
            Console.WriteLine("  Left stick:");  profile.LeftStick.InnerDeadzone  = AskInnerDeadzone(profile.LeftStick.InnerDeadzone);
            Console.WriteLine("  Right stick:"); profile.RightStick.InnerDeadzone = AskInnerDeadzone(profile.RightStick.InnerDeadzone);
        }
        SaveProfile(profile);
        Console.WriteLine($"  Saved [{profile.LayoutName}].");
    }

    // ---------------------------------------------------------------------------
    // RECENT / LAST USED / PROFILE IO
    // ---------------------------------------------------------------------------
    static string RecentPath() =>
        Path.Combine(AppContext.BaseDirectory, ProfilesDir, "_recent.json");

    record RecentEntry(string Controller, string Layout);

    static List<RecentEntry> LoadRecent()
    {
        try { return File.Exists(RecentPath())
            ? JsonSerializer.Deserialize<List<RecentEntry>>(File.ReadAllText(RecentPath())) ?? new() : new(); }
        catch { return new(); }
    }

    static void SaveRecent(string ctrl, string layout)
    {
        var list = LoadRecent();
        list.RemoveAll(e => e.Controller == ctrl && e.Layout == layout);
        list.Insert(0, new RecentEntry(ctrl, layout));
        if (list.Count > RecentMax) list = list.Take(RecentMax).ToList();
        try
        {
            Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, ProfilesDir));
            File.WriteAllText(RecentPath(), JsonSerializer.Serialize(list, JsonOpts));
        }
        catch { }
    }

    static string LastUsedPath(string ctrl) => Path.Combine(ProfileDir(ctrl), "_last_used.txt");

    static void SaveLastUsed(string ctrl, string layout)
    { try { File.WriteAllText(LastUsedPath(ctrl), layout); } catch { } }

    static string? LoadLastUsed(string ctrl)
    {
        string path = LastUsedPath(ctrl);
        if (!File.Exists(path)) return null;
        try { string n = File.ReadAllText(path).Trim(); return string.IsNullOrEmpty(n) ? null : n; }
        catch { return null; }
    }

    static string Safe(string s)   => s.Replace(" ", "_").Replace("/", "-");
    static string Unsave(string s) => s.Replace("_", " ");

    static string ProfileDir(string ctrl)
    {
        string p = Path.Combine(AppContext.BaseDirectory, ProfilesDir, Safe(ctrl));
        Directory.CreateDirectory(p);
        return p;
    }

    static string ProfilePath(string ctrl, string layout) =>
        Path.Combine(ProfileDir(ctrl), $"{Safe(layout)}.json");

    static List<string> ListProfiles(string ctrl) =>
        Directory.GetFiles(ProfileDir(ctrl), "*.json")
            .Select(f => Path.GetFileNameWithoutExtension(f)!)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(Unsave).ToList();

    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    static void SaveProfile(Profile p)
    {
        try { File.WriteAllText(ProfilePath(p.ControllerName, p.LayoutName),
                  JsonSerializer.Serialize(p, JsonOpts)); }
        catch (Exception ex) { Console.WriteLine($"  Could not save: {ex.Message}"); }
    }

    static Profile? LoadProfile(string ctrl, string layout)
    {
        string path = ProfilePath(ctrl, layout);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<Profile>(File.ReadAllText(path)); }
        catch { Console.WriteLine($"  Could not read '{layout}' -- corrupt?"); return null; }
    }

    static List<(string Name, int Idx)> FindDuplicates(Profile p)
    {
        var seen  = new Dictionary<int, string>();
        // FIX: use a HashSet to track already-added names so no entry appears twice
        var addedNames = new HashSet<string>();
        var dupes = new List<(string, int)>();
        foreach (var kv in p.Buttons.Concat(p.Triggers))
        {
            if (kv.Value == null) continue;
            int idx = kv.Value.Value;
            if (seen.TryGetValue(idx, out string? prev))
            {
                if (addedNames.Add(kv.Key))  dupes.Add((kv.Key, idx));
                if (addedNames.Add(prev))     dupes.Add((prev,   idx));
            }
            else seen[idx] = kv.Key;
        }
        return dupes;
    }

    // ---------------------------------------------------------------------------
    // WIZARD DISCONNECT RECOVERY
    // ---------------------------------------------------------------------------
    static bool WizardReconnect(ref Joystick pad, string ctrlName)
    {
        Console.WriteLine();
        Console.WriteLine("  Controller disconnected!");
        Console.WriteLine("  Reconnect your controller to continue from this step.");
        Console.WriteLine("  Press Enter to abort wizard.");

        // FIX: same zombie-thread fix as WaitForReconnect
        bool userAborted = false;
        var abortTask = Task.Run(() =>
        {
            try { Console.ReadLine(); }
            catch { }
            userAborted = true;
        });

        while (!_stop && !userAborted)
        {
            Thread.Sleep(600);
            var di = new DirectInput();
            try
            {
                var devices = di
                    .GetDevices(DeviceType.Gamepad,  DeviceEnumerationFlags.AllDevices)
                    .Concat(di.GetDevices(DeviceType.Joystick, DeviceEnumerationFlags.AllDevices))
                    .Where(d => !IsVirtual(d.ProductName))
                    .GroupBy(d => d.InstanceGuid).Select(g => g.First())
                    .ToList();

                if (devices.Count == 0) { di.Dispose(); continue; }

                var joy = new Joystick(di, devices[0].InstanceGuid);
                foreach (var obj in joy.GetObjects(DeviceObjectTypeFlags.AbsoluteAxis))
                    try { joy.GetObjectPropertiesById(obj.ObjectId).Range = new InputRange(-32768, 32767); } catch { }
                joy.Properties.BufferSize = 128;
                try { joy.Acquire(); }
                catch { di.Dispose(); continue; }

                DisposeActiveDi();
                _activeDiInstances.Add(di);
                pad = joy;
                Console.WriteLine("  Reconnected! Continuing from where you left off...");
                Console.WriteLine();

                try { NativeMethods.PostStdinNewline(); } catch { }
                return true;
            }
            catch { try { di.Dispose(); } catch { } }
        }
        Console.WriteLine("  Wizard cancelled.");
        return false;
    }

    // ---------------------------------------------------------------------------
    // SETUP WIZARD
    // ---------------------------------------------------------------------------
    static Profile RunWizard(Joystick pad, string ctrlName, string layoutName, bool isPS = false)
    {
        JoystickState initState;
        lock (_diPollLock)
        {
            try { pad.Poll(); initState = pad.GetCurrentState(); }
            catch
            {
                Console.WriteLine("  Controller disconnected. Reconnect and try again.");
                // FIX: return null sentinel instead of empty profile so caller doesn't save garbage
                return Profile.Incomplete(ctrlName, layoutName);
            }
        }
        int  numBtns = initState.Buttons.Length;
        bool hasHat  = initState.PointOfViewControllers.Length > 0;

        Console.WriteLine();
        Console.WriteLine("  " + new string('=', 43));
        Console.WriteLine("  CONTROLLER SETUP WIZARD");
        Console.WriteLine($"  Controller : {ctrlName}");
        Console.WriteLine($"  Layout     : {layoutName}");
        Console.WriteLine($"  Buttons: {numBtns}   HAT: {(hasHat ? "Yes" : "No")}");
        Console.WriteLine("  " + new string('=', 43));
        Console.WriteLine("  Press each button/axis when asked.");
        Console.WriteLine("  B = go back  |  R = redo this step");
        Console.WriteLine();

        var steps = new List<(string Label, string Type, string Key)>();
        foreach (var b in XboxButtons) steps.Add((b.Label, "btn", b.Label));
        steps.Add(("LT", isPS ? "axis" : "btn", "LT"));
        steps.Add(("RT", isPS ? "axis" : "btn", "RT"));
        steps.Add(("LEFT STICK UP",    "axis", "ls_up"));
        steps.Add(("LEFT STICK DOWN",  "axis", "ls_down"));
        steps.Add(("LEFT STICK LEFT",  "axis", "ls_left"));
        steps.Add(("LEFT STICK RIGHT", "axis", "ls_right"));
        steps.Add(("RIGHT STICK UP",   "axis", "rs_up"));
        steps.Add(("RIGHT STICK DOWN", "axis", "rs_down"));
        steps.Add(("RIGHT STICK LEFT", "axis", "rs_left"));
        steps.Add(("RIGHT STICK RIGHT","axis", "rs_right"));

        var axisInstr = new Dictionary<string, string>
        {
            ["ls_up"]    = "LEFT STICK fully UP, release",
            ["ls_down"]  = "LEFT STICK fully DOWN, release",
            ["ls_left"]  = "LEFT STICK fully LEFT, release",
            ["ls_right"] = "LEFT STICK fully RIGHT, release",
            ["rs_up"]    = "RIGHT STICK fully UP, release",
            ["rs_down"]  = "RIGHT STICK fully DOWN, release",
            ["rs_left"]  = "RIGHT STICK fully LEFT, release",
            ["rs_right"] = "RIGHT STICK fully RIGHT, release",
            ["LT"]       = "Press L2 fully down, then release",
            ["RT"]       = "Press R2 fully down, then release",
        };

        var results = new Dictionary<string, (int Val, int Sign)>();
        int i = 0;

        while (i < steps.Count)
        {
            var (label, stype, key) = steps[i];
            // FIX: blank line before each step for breathing room in the output
            Console.Write($"\n  {i + 1}/{steps.Count}  [ {label} ]  ");
            if (stype == "axis")
            {
                Console.WriteLine(); Console.Write($"  {axisInstr[key]}  ");
                var (isCmd, cmdR, axisIdx, sign) = DetectAxisOrCmd(pad);
                if (isCmd && cmdR == Cmd.Disconnected)
                {
                    if (!WizardReconnect(ref pad, ctrlName)) return Profile.Incomplete(ctrlName, layoutName);
                }
                else if (isCmd) HandleBackRedo(cmdR, ref i, key, steps, results);
                else { Console.WriteLine($"axis {axisIdx} sign {(sign > 0 ? "+" : "")}{sign}"); results[key] = (axisIdx, sign); i++; }
            }
            else
            {
                var (isCmd, cmdR, btnIdx) = DetectButtonOrCmd(pad);
                if (isCmd && cmdR == Cmd.Disconnected)
                {
                    if (!WizardReconnect(ref pad, ctrlName)) return Profile.Incomplete(ctrlName, layoutName);
                }
                else if (isCmd) HandleBackRedo(cmdR, ref i, key, steps, results);
                else { Console.WriteLine($"button {btnIdx}"); results[key] = (btnIdx, 1); i++; }
            }
        }

        var profile = new Profile { ControllerName = ctrlName, LayoutName = layoutName };
        foreach (var b in XboxButtons)
            profile.Buttons[b.Label] = results.TryGetValue(b.Label, out var r) ? r.Val : (int?)null;
        if (isPS)
        {
            profile.UseAnalogTriggers = true;
            profile.AnalogLTAxis = results.TryGetValue("LT", out var ltr) ? ltr.Val : 4;
            profile.AnalogRTAxis = results.TryGetValue("RT", out var rtr) ? rtr.Val : 5;
            profile.Triggers["LT"] = -1;
            profile.Triggers["RT"] = -1;
        }
        else
        {
            foreach (var t in new[] { "LT", "RT" })
                profile.Triggers[t] = results.TryGetValue(t, out var r) ? r.Val : (int?)null;
        }

        var lsUp    = results.GetValueOrDefault("ls_up",    (Val: 0, Sign: 1));
        var lsDown  = results.GetValueOrDefault("ls_down",  (Val: 0, Sign: 1));
        var lsLeft  = results.GetValueOrDefault("ls_left",  (Val: 1, Sign: 1));
        var lsRight = results.GetValueOrDefault("ls_right", (Val: 1, Sign: 1));
        var rsUp    = results.GetValueOrDefault("rs_up",    (Val: 0, Sign: 1));
        var rsDown  = results.GetValueOrDefault("rs_down",  (Val: 0, Sign: 1));
        var rsLeft  = results.GetValueOrDefault("rs_left",  (Val: 1, Sign: 1));
        var rsRight = results.GetValueOrDefault("rs_right", (Val: 1, Sign: 1));

        // FIX: use DOWN sign for YPosSign so both up and down detections contribute.
        // UP gives axis index, DOWN confirms the sign direction. Same for right sticks.
        // Negate the sign to invert the Y-axis so UP input moves up and DOWN input moves down.
        profile.LeftStick  = new StickConfig { XAxis = lsRight.Val, YAxis = lsUp.Val,
                                               XPosSign = lsRight.Sign, YPosSign = -lsDown.Sign };
        profile.RightStick = new StickConfig { XAxis = rsRight.Val, YAxis = rsUp.Val,
                                               XPosSign = rsRight.Sign, YPosSign = -rsDown.Sign };

        Console.WriteLine();
        if (hasHat) { profile.DpadMode = "hat"; Console.WriteLine("  D-pad: HAT detected."); }
        else
        {
            profile.DpadMode = "buttons";
            var dpadSteps = new[] { ("DPAD UP","(0, 1)"),("DPAD DOWN","(0, -1)"),("DPAD LEFT","(-1, 0)"),("DPAD RIGHT","(1, 0)") };
            int j = 0;
            while (j < dpadSteps.Length)
            {
                Console.Write($"\n  [ {dpadSteps[j].Item1} ]  ");
                var (isCmd, cmd, val) = DetectButtonOrCmd(pad);
                if (isCmd)
                {
                    if (cmd == Cmd.Redo) { profile.DpadButtons.Remove(dpadSteps[j].Item2); Console.WriteLine("Redo..."); }
                    else if (cmd == Cmd.Back)
                    {
                        if (j == 0) Console.WriteLine("Already first D-pad step.");
                        else { j--; profile.DpadButtons.Remove(dpadSteps[j].Item2); Console.WriteLine($"Back to {dpadSteps[j].Item1}"); }
                    }
                }
                else { Console.WriteLine($"button {val}"); profile.DpadButtons[dpadSteps[j].Item2] = val; j++; }
            }
        }

        profile.AntiDeadzone = AskSensitivity();
        Console.Write("  Set inner deadzone? [y/N]: ");
        if (Console.ReadLine()?.Trim().ToLower() == "y")
        {
            Console.WriteLine("  Left stick:");  profile.LeftStick.InnerDeadzone  = AskInnerDeadzone();
            Console.WriteLine("  Right stick:"); profile.RightStick.InnerDeadzone = AskInnerDeadzone();
        }

        Console.Write("  Mark as favorite? [y/N]: ");
        profile.IsFavorite = Console.ReadLine()?.Trim().ToLower() == "y";
        Console.Write("  Note (Enter to skip): ");
        string note = Console.ReadLine()?.Trim() ?? "";
        if (!string.IsNullOrEmpty(note)) profile.Notes = note;

        var dupes = FindDuplicates(profile);
        if (dupes.Count > 0)
        {
            Console.WriteLine("  WARNING: Duplicate button indices!");
            foreach (var (n, idx) in dupes) Console.WriteLine($"     {n} -> button {idx}");
        }

        SaveProfile(profile);
        Console.WriteLine("\n  Layout saved!\n");
        return profile;
    }

    static void HandleBackRedo(Cmd cmd, ref int i, string key,
        List<(string, string, string)> steps, Dictionary<string, (int Val, int Sign)> results)
    {
        if (cmd == Cmd.Redo) { results.Remove(key); Console.WriteLine("Redo..."); }
        else if (cmd == Cmd.Back)
        {
            if (i == 0) Console.WriteLine("Already first step.");
            else { i--; results.Remove(steps[i].Item3); Console.WriteLine($"Back to [ {steps[i].Item1} ]"); }
        }
    }

    // ---------------------------------------------------------------------------
    // EDIT / TEST / EXPORT / IMPORT
    // ---------------------------------------------------------------------------
    static void EditLayout(Joystick pad, Profile profile)
    {
        while (true)
        {
            Console.WriteLine();
            Console.WriteLine("  " + new string('=', 43));
            Console.WriteLine($"  EDIT LAYOUT -- {profile.LayoutName}");
            Console.WriteLine("  " + new string('=', 43));

            var dupeNames = FindDuplicates(profile).Select(d => d.Name).ToHashSet();
            var allItems  = XboxButtons.Select(b => (b.Label, profile.Buttons.GetValueOrDefault(b.Label), "btn"))
                .Concat(new[] { ("LT", profile.Triggers.GetValueOrDefault("LT"), "trg"),
                                ("RT", profile.Triggers.GetValueOrDefault("RT"), "trg") }).ToList();

            for (int i = 0; i < allItems.Count; i++)
            {
                var (name, idx, _) = allItems[i];
                string warn = dupeNames.Contains(name) ? " [!dup]" : "";
                Console.WriteLine($"  [{i + 1,2}]  {name,-6} -> {idx}{warn}");
            }

            Console.WriteLine();
            Console.WriteLine("  [S] Swap  [F] Toggle favorite  [N] Edit note  [Enter] Back");
            Console.Write("  Remap number: ");
            string choice = Console.ReadLine()?.Trim() ?? "";
            if (choice == "") break;

            if (choice.ToLower() == "s") { SwapButtons(pad, profile, allItems); continue; }
            if (choice.ToLower() == "f")
            {
                profile.IsFavorite = !profile.IsFavorite;
                SaveProfile(profile);
                Console.WriteLine(profile.IsFavorite ? "  Favorited." : "  Unfavorited.");
                continue;
            }
            if (choice.ToLower() == "n")
            {
                Console.WriteLine($"  Current note: {profile.Notes ?? "(none)"}");
                Console.Write("  New note (Enter to clear): ");
                string n = Console.ReadLine()?.Trim() ?? "";
                profile.Notes = string.IsNullOrEmpty(n) ? null : n;
                SaveProfile(profile); Console.WriteLine("  Saved."); continue;
            }

            if (int.TryParse(choice, out int num) && num >= 1 && num <= allItems.Count)
            {
                var (name, currentIdx, kind) = allItems[num - 1];
                Console.Write($"  Press button for [ {name} ]...  ");
                var (isCmd, _, newIdx) = DetectButtonOrCmd(pad);
                if (isCmd) { Console.WriteLine("Cancelled."); continue; }
                Console.WriteLine($"button {newIdx}");
                var conflict = profile.Buttons.Concat(profile.Triggers)
                    .Where(kv => kv.Value == newIdx && kv.Key != name).Select(kv => kv.Key).ToList();
                if (conflict.Count > 0)
                {
                    Console.WriteLine($"  Already used by {string.Join(", ", conflict)}");
                    Console.Write("  Assign anyway? [y/N]: ");
                    if (Console.ReadLine()?.Trim().ToLower() != "y") { Console.WriteLine("  Cancelled."); continue; }
                }
                if (kind == "trg") profile.Triggers[name] = newIdx;
                else               profile.Buttons[name]  = newIdx;
                SaveProfile(profile);
                Console.WriteLine($"  {name}: {currentIdx} -> {newIdx} saved.");
            }
            else Console.WriteLine("  Invalid.");
        }
    }

    static void SwapButtons(Joystick pad, Profile profile,
        List<(string Name, int? Idx, string Kind)> items)
    {
        Console.Write("  Two numbers to swap (e.g. 1 2): ");
        string[] parts = (Console.ReadLine() ?? "").Trim()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !int.TryParse(parts[0], out int a) || !int.TryParse(parts[1], out int b))
        { Console.WriteLine("  Invalid."); return; }
        a--; b--;
        if (a < 0 || a >= items.Count || b < 0 || b >= items.Count)
        { Console.WriteLine("  Out of range."); return; }
        var (nA, iA, kA) = items[a]; var (nB, iB, kB) = items[b];
        if (kA == "trg") profile.Triggers[nA] = iB; else profile.Buttons[nA] = iB;
        if (kB == "trg") profile.Triggers[nB] = iA; else profile.Buttons[nB] = iA;
        SaveProfile(profile);
        Console.WriteLine($"  Swapped {nA} <-> {nB}.");
    }

    static (List<string> Errors, List<string> Warnings) Validate(Profile p, Joystick pad)
    {
        var errors = new List<string>(); var warnings = new List<string>();

        // FIX: guard against validating an incomplete wizard profile
        if (p.IsIncomplete)
            return (new List<string> { "Profile is incomplete (wizard was cancelled or disconnected)" }, warnings);

        int n;
        try { pad.Poll(); n = pad.GetCurrentState().Buttons.Length; }
        catch { return (new List<string> { "Controller not responding" }, warnings); }
        
        // FIX: validate analog trigger axes if using PS analog triggers
        if (p.UseAnalogTriggers)
        {
            if (p.AnalogLTAxis < 0 || p.AnalogLTAxis > 5)
                errors.Add($"LT axis {p.AnalogLTAxis} invalid (must be 0-5)");
            if (p.AnalogRTAxis < 0 || p.AnalogRTAxis > 5)
                errors.Add($"RT axis {p.AnalogRTAxis} invalid (must be 0-5)");
        }
        
        foreach (var kv in p.Buttons.Concat(p.Triggers))
        {
            if (kv.Value == null) warnings.Add($"{kv.Key} not mapped");
            else if (kv.Value == -1) { /* PS analog axis trigger, skip */ }
            else if (kv.Value < 0 || kv.Value >= n) errors.Add($"{kv.Key} -> btn {kv.Value}, invalid (range 0-{n-1})");
        }
        foreach (var (name, idx) in FindDuplicates(p))
            warnings.Add($"{name} shares btn {idx}");
        return (errors, warnings);
    }

    static void RunTestMode(Joystick pad, Profile profile)
    {
        var rev = new Dictionary<int, string>();
        foreach (var kv in profile.Buttons.Concat(profile.Triggers))
            if (kv.Value.HasValue) rev[kv.Value.Value] = kv.Key;

        Console.WriteLine();
        Console.WriteLine("  " + new string('=', 43));
        Console.WriteLine($"  TEST MODE  --  {profile.LayoutName}");
        Console.WriteLine("  Press buttons to see Xbox mapping. Escape to exit.");
        Console.WriteLine("  " + new string('=', 43) + "\n");

        bool[] prev = new bool[128];
        while (!_stop)
        {
            if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape) break;
            Thread.Sleep(10); // reduced from 4ms
            try
            {
                pad.Poll();
                bool[] btns = pad.GetCurrentState().Buttons;
                int limit = Math.Min(btns.Length, prev.Length);
                for (int i = 0; i < limit; i++)
                {
                    if (btns[i] && !prev[i])
                        Console.WriteLine($"  btn {i,2}  ->  Xbox [ {(rev.TryGetValue(i, out var m) ? m : "(unmapped)")} ]");
                    prev[i] = btns[i];
                }
            }
            catch
            {
                Console.WriteLine("\n  Controller disconnected during test mode.");
                break;
            }
        }
        Console.WriteLine("\n  Test mode ended.\n");
    }

    static void ExportLayout(string ctrlName, List<string> existing)
    {
        if (existing.Count == 0) { Console.WriteLine("  Nothing to export."); return; }
        PickLayout(existing, out int idx);
        if (idx < 0) return;
        string src = ProfilePath(ctrlName, existing[idx]);
        if (!File.Exists(src)) { Console.WriteLine("  File not found."); return; }
        string? dest = RunFileDialog(save: true, filter: "JSON (*.json)|*.json",
            title: "Save Profile As", defaultFile: $"{Safe(existing[idx])}.json");
        if (string.IsNullOrEmpty(dest)) { Console.WriteLine("  Cancelled."); return; }
        try { File.Copy(src, dest, overwrite: true); Console.WriteLine($"  Exported to: {dest}"); }
        catch (Exception ex) { Console.WriteLine($"  Export failed: {ex.Message}"); }
    }

    static void ImportLayout(string ctrlName)
    {
        string? src = RunFileDialog(save: false, filter: "JSON (*.json)|*.json",
            title: "Select Profile JSON", defaultFile: null);
        if (string.IsNullOrEmpty(src)) { Console.WriteLine("  Cancelled."); return; }
        Profile? p;
        try { p = JsonSerializer.Deserialize<Profile>(File.ReadAllText(src)); }
        catch { Console.WriteLine("  Invalid file."); return; }
        if (p == null) { Console.WriteLine("  Empty file."); return; }
        p.ControllerName = ctrlName;
        Console.Write($"  Name (default: {p.LayoutName}): ");
        string name = Console.ReadLine()?.Trim() ?? "";
        if (!string.IsNullOrEmpty(name)) p.LayoutName = name;
        SaveProfile(p);
        Console.WriteLine($"  Imported as [{p.LayoutName}].");
    }

    // ---------------------------------------------------------------------------
    // SETTINGS
    // ---------------------------------------------------------------------------
    static void ShowSettings()
    {
        Console.WriteLine();
        Console.WriteLine("  " + new string('=', 43));
        Console.WriteLine("  SETTINGS");
        Console.WriteLine("  " + new string('=', 43));
        Console.WriteLine($"  Current poll mode: {_settings.PollMode} (~{PollHz(_settings.PollMode)}Hz)");
        Console.WriteLine();
        Console.WriteLine("  [1] Eco         (~100Hz) -- lowest CPU, suits any PC");
        Console.WriteLine("  [2] Balanced    (~200Hz) -- recommended default");
        Console.WriteLine("  [3] Performance (~500Hz) -- lowest latency, fast PCs only");
        Console.WriteLine();
        Console.Write("  Choose [1/2/3] or Enter to keep: ");
        string c = Console.ReadLine()?.Trim() ?? "";
        _settings.PollMode = c switch
        {
            "1" => PollMode.Eco,
            "3" => PollMode.Performance,
            "2" => PollMode.Balanced,
            _   => _settings.PollMode,
        };
        _settings.Save();
        PollModeAtCrash = _settings.PollMode;
        Console.WriteLine($"  Poll mode set to {_settings.PollMode}.");
    }

    // ---------------------------------------------------------------------------
    // LAYOUT MENU
    // ---------------------------------------------------------------------------
    static Profile? SelectLayout(Joystick pad, string ctrlName, bool isPS)
    {
        while (true)
        {
            var all      = ListProfiles(ctrlName);
            var favs     = all.Where(n => LoadProfile(ctrlName, n)?.IsFavorite == true).ToList();
            var others   = all.Where(n => LoadProfile(ctrlName, n)?.IsFavorite != true).ToList();
            var existing = favs.Concat(others).ToList();

            var recent = LoadRecent()
                .Where(e => e.Controller == ctrlName && existing.Contains(e.Layout))
                .Take(3).ToList();

            Console.WriteLine();
            Console.WriteLine("  " + new string('=', 43));
            Console.WriteLine($"              {ctrlName}");
            Console.WriteLine("  " + new string('=', 43));

            if (recent.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("  RECENT");
                for (int i = 0; i < recent.Count; i++)
                {
                    var rp = LoadProfile(ctrlName, recent[i].Layout);
                    string rs = rp != null ? $"  [{SensitivityLabel(rp.AntiDeadzone)} {rp.AntiDeadzone}]" : "";
                    Console.WriteLine($"  [Recent{i + 1}]  {recent[i].Layout}{rs}");
                }
                Console.WriteLine();
            }

            if (existing.Count == 0)
            {
                Console.WriteLine("  (no saved layouts yet)");
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine("  LAYOUTS");
                for (int i = 0; i < existing.Count; i++)
                {
                    var p    = LoadProfile(ctrlName, existing[i]);
                    string fav  = p?.IsFavorite == true ? " *" : "  ";
                    string ps   = p?.UseAnalogTriggers == true ? "  [PS]" : "";
                    string s    = p != null ? $"  [{SensitivityLabel(p.AntiDeadzone)} {p.AntiDeadzone}]" : "";
                    string g    = p?.GameExePath != null ? $"  [{Path.GetFileNameWithoutExtension(p.GameExePath)}]" : "";
                    string note = !string.IsNullOrEmpty(p?.Notes) ? $"  \"{p!.Notes}\"" : "";
                    string dup  = p != null && FindDuplicates(p).Count > 0 ? "  [!dup]" : "";
                    Console.WriteLine($"  [{i + 1}]{fav} {existing[i]}{ps}{s}{g}{note}{dup}");
                }
            }

            Console.WriteLine();
            Console.WriteLine("  [N] New layout (wizard)");
            Console.WriteLine("  [P] PS smart default  (DS4/DS5 preset)");
            if (existing.Count > 0)
            {
                string? lastUsed = LoadLastUsed(ctrlName);
                if (lastUsed != null && existing.Contains(lastUsed))
                    Console.WriteLine($"  [L]  Quick load last: {lastUsed}");
                Console.WriteLine($"  [1-{existing.Count}]  Load a layout");
                if (recent.Count > 0)
                    Console.WriteLine($"  [Recent1-Recent{recent.Count}]  Load recent");
                Console.WriteLine();
                Console.WriteLine("  [E]  Edit layout");
                Console.WriteLine("  [T]  Test layout");
                Console.WriteLine("  [A]  Adjust sensitivity");
                Console.WriteLine("  [G]  Set game launcher");
                Console.WriteLine("  [X]  Export layout");
                Console.WriteLine("  [I]  Import layout");
                Console.WriteLine("  [R]  Rename layout");
                Console.WriteLine("  [D]  Delete layout");
            }
            Console.WriteLine();
            Console.WriteLine("  [S]  Settings");
            Console.WriteLine();
            Console.Write("  Choice: ");
            string choice = Console.ReadLine()?.Trim() ?? "";
            string cl     = choice.ToLower();

            if (cl.StartsWith("recent") && int.TryParse(cl["recent".Length..], out int ri) && ri >= 1 && ri <= recent.Count)
            {
                string rLayout  = recent[ri - 1].Layout;
                var    rProfile = LoadProfile(ctrlName, rLayout);
                if (rProfile == null) { Console.WriteLine("  Could not load."); continue; }
                var (re, _) = Validate(rProfile, pad);
                if (re.Count > 0) { foreach (string e in re) Console.WriteLine($"  ERR: {e}"); continue; }
                SaveLastUsed(ctrlName, rLayout); SaveRecent(ctrlName, rLayout);
                TryLaunchGame(rProfile); return rProfile;
            }

            if (cl == "s") { ShowSettings(); Banner(); continue; }

            if (cl == "p")
            {
                Console.WriteLine("  PS Wizard -- L2/R2 will be mapped as analog axes.");
                Console.Write("  Layout name (e.g. DS4, DualSense): ");
                string name = Console.ReadLine()?.Trim() ?? "PS Default";
                if (string.IsNullOrEmpty(name)) name = "PS Default";
                if (existing.Any(e => Safe(e) == Safe(name)))
                {
                    Console.Write($"  '{name}' exists. Overwrite? [y/N]: ");
                    if (Console.ReadLine()?.Trim().ToLower() != "y") continue;
                }
                var wp = RunWizard(pad, ctrlName, name, isPS: true);
                // FIX: don't return an incomplete profile to the caller
                if (wp.IsIncomplete) { Console.WriteLine("  Wizard did not complete. Layout not saved."); continue; }
                return wp;
            }

            if (cl == "l" && existing.Count > 0)
            {
                string? lastUsed = LoadLastUsed(ctrlName);
                if (lastUsed != null && existing.Contains(lastUsed))
                {
                    var profile = LoadProfile(ctrlName, lastUsed);
                    if (profile != null)
                    {
                        var (errs, _) = Validate(profile, pad);
                        if (errs.Count > 0) { foreach (string e in errs) Console.WriteLine($"  ERR: {e}"); Console.ReadLine(); continue; }
                        Console.WriteLine($"  Loaded [{lastUsed}].");
                        SaveLastUsed(ctrlName, lastUsed); SaveRecent(ctrlName, lastUsed);
                        TryLaunchGame(profile); return profile;
                    }
                }
                else Console.WriteLine("  No last used found.");
                continue;
            }

            if (cl == "n")
            {
                Console.Write("  Layout name: ");
                string name = Console.ReadLine()?.Trim() ?? "default";
                if (string.IsNullOrEmpty(name)) name = "default";
                if (existing.Any(e => Safe(e) == Safe(name)))
                {
                    Console.Write($"  '{name}' exists. Overwrite? [y/N]: ");
                    if (Console.ReadLine()?.Trim().ToLower() != "y") continue;
                }
                var wp = RunWizard(pad, ctrlName, name, isPS);
                // FIX: don't return an incomplete profile to the caller
                if (wp.IsIncomplete) { Console.WriteLine("  Wizard did not complete. Layout not saved."); continue; }
                return wp;
            }

            if (cl == "e" && existing.Count > 0)
            { PickLayout(existing, out int ec); if (ec >= 0) { var p = LoadProfile(ctrlName, existing[ec]); if (p != null) EditLayout(pad, p); } continue; }

            if (cl == "t" && existing.Count > 0)
            { PickLayout(existing, out int tc); if (tc >= 0) { var p = LoadProfile(ctrlName, existing[tc]); if (p != null) RunTestMode(pad, p); } continue; }

            if (cl == "a" && existing.Count > 0) { AdjustSensitivity(pad, ctrlName, existing); continue; }

            if (cl == "g" && existing.Count > 0) { SetGameLauncher(ctrlName, existing); continue; }

            if (cl == "x" && existing.Count > 0) { ExportLayout(ctrlName, existing); continue; }

            if (cl == "i") { ImportLayout(ctrlName); continue; }

            if (cl == "r" && existing.Count > 0)
            {
                PickLayout(existing, out int rc);
                if (rc >= 0)
                {
                    string oldName = existing[rc];
                    Console.Write($"  New name for '{oldName}': ");
                    string newName = Console.ReadLine()?.Trim() ?? "";
                    if (!string.IsNullOrEmpty(newName))
                    {
                        var p = LoadProfile(ctrlName, oldName);
                        if (p != null)
                        {
                            try
                            {
                                p.LayoutName = newName;
                                File.WriteAllText(ProfilePath(ctrlName, newName), JsonSerializer.Serialize(p, JsonOpts));
                                File.Delete(ProfilePath(ctrlName, oldName));
                                string? lu = LoadLastUsed(ctrlName);
                                if (lu != null && Safe(lu) == Safe(oldName)) SaveLastUsed(ctrlName, newName);
                                Console.WriteLine($"  Renamed -> '{newName}'");
                            }
                            catch (Exception ex) { Console.WriteLine($"  Rename failed: {ex.Message}"); }
                        }
                    }
                    else Console.WriteLine("  Cancelled.");
                }
                continue;
            }

            if (cl == "d" && existing.Count > 0)
            {
                PickLayout(existing, out int dc);
                if (dc >= 0)
                {
                    string del = existing[dc];
                    Console.Write($"  Delete '{del}'? [y/N]: ");
                    if (Console.ReadLine()?.Trim().ToLower() == "y")
                        try { File.Delete(ProfilePath(ctrlName, del)); Console.WriteLine($"  Deleted."); }
                        catch (Exception ex) { Console.WriteLine($"  Failed: {ex.Message}"); }
                    else Console.WriteLine("  Cancelled.");
                }
                continue;
            }

            if (int.TryParse(choice, out int load) && load >= 1 && load <= existing.Count)
            {
                var profile = LoadProfile(ctrlName, existing[load - 1]);
                if (profile == null) { Console.WriteLine("  Load failed."); continue; }
                var (errs, warns) = Validate(profile, pad);
                if (errs.Count > 0)
                { foreach (string e in errs) Console.WriteLine($"  ERR: {e}"); Console.ReadLine(); continue; }
                if (warns.Count > 0)
                { foreach (string w in warns) Console.WriteLine($"  WARN: {w}");
                  Console.Write("  Load anyway? [Y/n]: ");
                  if (Console.ReadLine()?.Trim().ToLower() == "n") continue; }
                Console.WriteLine($"  Loaded [{existing[load - 1]}].");
                SaveLastUsed(ctrlName, existing[load - 1]);
                SaveRecent(ctrlName, existing[load - 1]);
                TryLaunchGame(profile); return profile;
            }

            Console.WriteLine("  Invalid choice.");
        }
    }

    // ---------------------------------------------------------------------------
    // GAME LAUNCHER
    // ---------------------------------------------------------------------------
    static void SetGameLauncher(string ctrlName, List<string> existing)
    {
        PickLayout(existing, out int idx);
        if (idx < 0) return;
        var profile = LoadProfile(ctrlName, existing[idx]);
        if (profile == null) return;

        Console.WriteLine($"\n  Current: {profile.GameExePath ?? "(none)"}");
        Console.WriteLine("  [C] Clear  |  [Enter] Browse for .exe");
        Console.Write("  Choice: ");
        string nav = Console.ReadLine()?.Trim().ToLower() ?? "";
        if (nav == "c") { profile.GameExePath = null; SaveProfile(profile); Console.WriteLine("  Cleared."); return; }

        string input = RunFileDialog(save: false,
            filter: "Executable (*.exe)|*.exe|All (*.*)|*.*",
            title: "Select Game EXE", defaultFile: null) ?? "";
        if (string.IsNullOrEmpty(input)) { Console.WriteLine("  Cancelled."); return; }
        profile.GameExePath = input;
        SaveProfile(profile);
        Console.WriteLine($"  Set to: {Path.GetFileNameWithoutExtension(input)}");
    }

    static string? RunFileDialog(bool save, string filter, string title, string? defaultFile)
    {
        string? result = null;
        var t = new Thread(() =>
        {
            try
            {
                if (save)
                {
                    var d = new System.Windows.Forms.SaveFileDialog
                        { Title = title, Filter = filter, FileName = defaultFile ?? "" };
                    if (d.ShowDialog() == System.Windows.Forms.DialogResult.OK) result = d.FileName;
                }
                else
                {
                    var d = new System.Windows.Forms.OpenFileDialog
                        { Title = title, Filter = filter, CheckFileExists = true };
                    if (d.ShowDialog() == System.Windows.Forms.DialogResult.OK) result = d.FileName;
                }
            }
            catch { }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start(); t.Join();
        return result;
    }

    static void TryLaunchGame(Profile profile)
    {
        if (string.IsNullOrEmpty(profile.GameExePath)) return;
        string exeName = Path.GetFileNameWithoutExtension(profile.GameExePath);
        Console.Write($"\n  Launch {exeName}? [Y/n]: ");
        if (Console.ReadLine()?.Trim().ToLower() == "n") return;
        if (!File.Exists(profile.GameExePath))
        { Console.WriteLine($"  Not found: {profile.GameExePath}"); return; }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                { FileName = profile.GameExePath,
                  WorkingDirectory = Path.GetDirectoryName(profile.GameExePath) ?? "",
                  UseShellExecute = true });
            Console.WriteLine($"  Launched {exeName}. Starting remapper...");
            Thread.Sleep(800);
        }
        catch (Exception ex) { Console.WriteLine($"  Could not launch: {ex.Message}"); }
    }

    static void PickLayout(List<string> list, out int result)
    {
        Console.WriteLine();
        for (int i = 0; i < list.Count; i++) Console.WriteLine($"    [{i + 1}] {list[i]}");
        Console.Write("  Pick number: ");
        result = int.TryParse(Console.ReadLine(), out int n) && n >= 1 && n <= list.Count ? n - 1 : -1;
    }
}

// ---------------------------------------------------------------------------
// DATA MODELS
// ---------------------------------------------------------------------------
enum PollMode { Eco, Balanced, Performance }

class AppSettings
{
    static string SettingsPath => Path.Combine(AppContext.BaseDirectory, "profiles", "_settings.json");
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true,
        Converters = { new JsonStringEnumConverter() } };

    [JsonPropertyName("poll_mode")] public PollMode PollMode { get; set; } = PollMode.Balanced;

    public static AppSettings Load()
    {
        try { if (File.Exists(SettingsPath)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Opts) ?? new(); }
        catch { }
        return new();
    }

    public void Save()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
              File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, Opts)); }
        catch { }
    }
}

class Profile
{
    [JsonPropertyName("controller_name")]    public string  ControllerName    { get; set; } = "";
    [JsonPropertyName("layout_name")]        public string  LayoutName        { get; set; } = "";
    [JsonPropertyName("buttons")]            public Dictionary<string, int?> Buttons  { get; set; } = new();
    [JsonPropertyName("triggers")]           public Dictionary<string, int?> Triggers { get; set; } = new();
    [JsonPropertyName("left_stick")]         public StickConfig LeftStick     { get; set; } = new();
    [JsonPropertyName("right_stick")]        public StickConfig RightStick    { get; set; } = new();
    [JsonPropertyName("dpad_mode")]          public string  DpadMode          { get; set; } = "hat";
    [JsonPropertyName("dpad_buttons")]       public Dictionary<string, int> DpadButtons { get; set; } = new();
    [JsonPropertyName("game_exe_path")]      public string? GameExePath       { get; set; } = null;
    [JsonPropertyName("is_favorite")]        public bool    IsFavorite        { get; set; } = false;
    [JsonPropertyName("notes")]              public string? Notes             { get; set; } = null;
    [JsonPropertyName("use_analog_triggers")]public bool    UseAnalogTriggers { get; set; } = false;
    [JsonPropertyName("analog_lt_axis")]     public int     AnalogLTAxis      { get; set; } = 4;
    [JsonPropertyName("analog_rt_axis")]     public int     AnalogRTAxis      { get; set; } = 5;

    // FIX: IsIncomplete flag replaces the silent empty-profile return from wizard failures.
    // Marked JsonIgnore so it never persists to disk.
    [JsonIgnore] public bool IsIncomplete { get; private set; } = false;

    // Factory for an incomplete sentinel — never saved, never returned to remapper core.
    public static Profile Incomplete(string ctrl, string layout) =>
        new Profile { ControllerName = ctrl, LayoutName = layout, IsIncomplete = true };

    [JsonPropertyName("anti_deadzone")]
    public int AntiDeadzoneRaw { get; set; } = 0;

    // FIX: use -1 as the "not set" sentinel so that a legitimate value of 0
    // (no anti-deadzone) is preserved correctly across save/load cycles.
    [JsonIgnore]
    public int AntiDeadzone
    {
        get => AntiDeadzoneRaw < 0 ? 10000 : AntiDeadzoneRaw;
        set => AntiDeadzoneRaw = value;
    }
}

class StickConfig
{
    [JsonPropertyName("x_axis")]         public int    XAxis         { get; set; }
    [JsonPropertyName("y_axis")]         public int    YAxis         { get; set; }
    [JsonPropertyName("x_pos_sign")]     public int    XPosSign      { get; set; } = 1;
    [JsonPropertyName("y_pos_sign")]     public int    YPosSign      { get; set; } = 1;
    [JsonPropertyName("inner_deadzone")] public double InnerDeadzone { get; set; } = 0.08;
}

static class NativeMethods
{
    [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint p);
    [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint p);

    // Used to unblock a hanging Console.ReadLine() on the abort thread after reconnect.
    // Simulates pressing Enter on the console input by writing to the input handle.
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteConsoleInput(IntPtr hConsoleInput,
        [In] INPUT_RECORD[] lpBuffer, uint nLength, out uint lpNumberOfEventsWritten);

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT_RECORD
    {
        public ushort EventType;
        public KEY_EVENT_RECORD KeyEvent;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KEY_EVENT_RECORD
    {
        public int  bKeyDown;
        public ushort wRepeatCount;
        public ushort wVirtualKeyCode;
        public ushort wVirtualScanCode;
        public char  uChar;
        public uint  dwControlKeyState;
    }

    public static void PostStdinNewline()
    {
        var handle = GetStdHandle(-10); // STD_INPUT_HANDLE
        var records = new INPUT_RECORD[2];
        // key down
        records[0].EventType = 1;
        records[0].KeyEvent  = new KEY_EVENT_RECORD
            { bKeyDown = 1, wRepeatCount = 1, wVirtualKeyCode = 0x0D, uChar = '\r' };
        // key up
        records[1].EventType = 1;
        records[1].KeyEvent  = new KEY_EVENT_RECORD
            { bKeyDown = 0, wRepeatCount = 1, wVirtualKeyCode = 0x0D, uChar = '\r' };
        WriteConsoleInput(handle, records, 2, out _);
    }
}