// DIRECT360 – Universal Controller Remapper  (C# Edition)
// Supports: Xbox (wired + wireless via USB)
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

    public static PollMode PollModeAtCrash = PollMode.Balanced;

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

    // Serialize all DirectInput Poll/GetCurrentState calls to prevent COM races
    static readonly object _diPollLock = new();

    // Cache for detected pads in multi-mode to avoid re-acquiring on every menu loop
    // FIX #8: avoid calling GetAllRealPads() on every management menu iteration
    static List<PadInfo>? _cachedMultiPads = null;

    record PadInfo(Joystick Pad, string ProfileName, string DisplayName, int Slot,
                   Guid InstanceGuid);

    public static void Run()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        _settings = AppSettings.Load();
        PollModeAtCrash = _settings.PollMode;
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; _stop = true; };

        if (_settings.PollMode == PollMode.Performance)
            NativeMethods.timeBeginPeriod(1);

        Banner();
        CheckViGEm();

        while (!_stop)
        {
            Console.WriteLine();
            Console.WriteLine(CenterText("MODE SELECTION"));
            Console.WriteLine();
            Console.WriteLine("  [1] Single Controller Mode");
            Console.WriteLine("  [2] Multi-Controller Mode");
            Console.WriteLine("  [S] Settings");
            Console.WriteLine();
            Console.Write("  Choice: ");
            string modeChoice = Console.ReadLine()?.Trim().ToLower() ?? "";

            if (modeChoice == "s") { ShowSettings(); Banner(); continue; }
            if (modeChoice == "2") { RunMultiControllerMode(); continue; }
            if (modeChoice == "1" || modeChoice == "")
            {
                var pads = GetAllRealPads();
                if (_stop) break;
                if (pads.Count >= 1)
                    RunSingleController(pads[0]);
            }
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
        Console.WriteLine(CenterText("DIRECT360"));
        Console.WriteLine(CenterText("Xbox Controller Remapper"));
        Console.WriteLine("  " + new string('=', 43));
        Console.WriteLine($"  Poll: ~{PollHz(_settings.PollMode)}Hz [{_settings.PollMode}]");
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

                    result.Add(new PadInfo(joy, profileName, displayName, result.Count + 1,
                                           info.InstanceGuid));
                }
            }

            if (result != null && result.Count > 0)
            {
                _activeDiInstances.Add(di);
                string plural = result.Count == 1 ? "controller" : "controllers";
                Console.WriteLine($"  {result.Count} {plural} detected.");
                foreach (var p in result)
                    Console.WriteLine($"  Slot {p.Slot} : {p.DisplayName}");
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
            var profile = SelectLayout(padInfo.Pad, padInfo.ProfileName);
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
            string result = RunRemapperCore(padInfo.Pad, profile, null, cts);

            if (result == "exit" || _stop) break;

            if (result == "disconnected")
            {
                Console.WriteLine("\n  Controller disconnected. Waiting to reconnect...");
                Console.WriteLine("  Press Enter to go to menu instead.");

                bool ok = WaitForReconnect(padInfo.InstanceGuid, padInfo.ProfileName,
                    padInfo.DisplayName, out PadInfo? newPad);

                try { padInfo.Pad.Dispose(); } catch { }

                if (!ok || newPad == null || _stop) break;

                Console.WriteLine($"\n  Reconnected! Resuming [{profile.LayoutName}]...\n");
                padInfo = newPad;
                continue;
            }
        }
    }

    static bool WaitForReconnect(Guid targetGuid, string profileName, string displayName,
        out PadInfo? reconnected)
    {
        reconnected = null;

        // FIX #3: use a shared flag instead of unlinked CTS so the abort thread is
        // reliably stopped when reconnect succeeds, not just signalled and ignored.
        bool userAborted = false;
        bool reconnectSucceeded = false;
        var abortThread = new Thread(() =>
        {
            try { Console.ReadLine(); }
            catch { }
            if (!reconnectSucceeded) userAborted = true;
        }) { IsBackground = true };
        abortThread.Start();

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
                reconnected = new PadInfo(joy, profileName, displayName, 1, targetGuid);

                reconnectSucceeded = true; // set BEFORE unblocking ReadLine
                try { NativeMethods.PostStdinNewline(); } catch { }

                return true;
            }
            catch { try { di.Dispose(); } catch { } }
        }
        return false;
    }

    // ---------------------------------------------------------------------------
    // UNIFIED REMAPPER CORE
    // ---------------------------------------------------------------------------
    static string RunRemapperCore(Joystick pad, Profile profile,
        ViGEmClient? sharedClient, CancellationTokenSource cts)
    {
        // FIX #2: only create an owned client when no shared client is provided.
        // In multi-mode the caller passes its own client — don't create a redundant one.
        ViGEmClient? ownedClient = sharedClient == null ? new ViGEmClient() : null;
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
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long lastKeyCheckMs = 0;
            long lastReportMs = 0;
            
            // Pin this thread to a CPU core to minimize context switching and latency
            try
            {
                var proc = System.Diagnostics.Process.GetCurrentProcess();
                var affinity = proc.ProcessorAffinity;
                // Use the lowest available core to avoid contention with other processes
                var threads = proc.Threads;
                var currentThread = threads.Cast<System.Diagnostics.ProcessThread>()
                    .FirstOrDefault(t => t.Id == System.Threading.Thread.CurrentThread.ManagedThreadId);
                if (currentThread != null && affinity != IntPtr.Zero)
                {
                    // Set ProcessorAffinity to first available core
                    int coreCount = Environment.ProcessorCount;
                    proc.ProcessorAffinity = new IntPtr(1 << (coreCount > 1 ? 1 : 0)); // Use core 1 if available, else core 0
                }
            }
            catch { /* CPU affinity setting failed – continue without it */ }
            
            while (!cts.IsCancellationRequested && !_stop)
            {
                JoystickState state;
                lock (_diPollLock)
                {
                    try { pad.Poll(); state = pad.GetCurrentState(); }
                    catch { exitReason = "disconnected"; break; }
                }

                bool[] currentBtns = state.Buttons;
                bool[] snapshotBtns = prevBtns;
                int    numBtns      = currentBtns.Length;
                bool   changed      = false;

                // Only check console input every 50ms to reduce overhead
                if (sw.ElapsedMilliseconds - lastKeyCheckMs >= 50)
                {
                    lastKeyCheckMs = sw.ElapsedMilliseconds;
                    if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.M)
                    {
                        Console.WriteLine("\n  Returning to menu...");
                        exitReason = "menu"; break;
                    }
                }
                
                // Resize prevBtns if needed (handle device changes)
                if (prevBtns.Length != numBtns)
                    prevBtns = new bool[numBtns];

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
                byte newLt = (ltIdx.HasValue && ltIdx < numBtns && currentBtns[ltIdx.Value]) ? (byte)255 : (byte)0;
                byte newRt = (rtIdx.HasValue && rtIdx < numBtns && currentBtns[rtIdx.Value]) ? (byte)255 : (byte)0;
                if (newLt != prevLt) { gamepad.SetSliderValue(Xbox360Slider.LeftTrigger,  newLt); prevLt = newLt; changed = true; }
                if (newRt != prevRt) { gamepad.SetSliderValue(Xbox360Slider.RightTrigger, newRt); prevRt = newRt; changed = true; }

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
                    catch { /* ViGEm device removed – ignore */ }
                    lastReportMs = sw.ElapsedMilliseconds;
                }
                else
                {
                    // Always send a report every 10ms to ensure games get consistent updates
                    long currentMs = sw.ElapsedMilliseconds;
                    if (currentMs - lastReportMs >= 10)
                    {
                        try { gamepad.SubmitReport(); }
                        catch { /* ViGEm device removed – ignore */ }
                        lastReportMs = currentMs;
                    }
                }

                // Use high-precision timing instead of Thread.Sleep
                long targetMs = sw.ElapsedMilliseconds + sleepMs;
                while (sw.ElapsedMilliseconds < targetMs)
                {
                    long remaining = targetMs - sw.ElapsedMilliseconds;
                    if (remaining > 1)
                        System.Threading.Thread.Sleep(0);  // Yield without busy-waiting
                    else if (remaining > 0)
                        System.Threading.Thread.Yield();   // Minimal yield for sub-ms precision
                }
            }
        }
        finally
        {
            ReleaseAll();
            try { gamepad.Disconnect(); } catch { }
            ownedClient?.Dispose(); // only disposes if we created it — safe for shared client
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

    static short ScaleAxis(double val, int antiDeadzone, double innerDeadzone = 0.08)
    {
        if (Math.Abs(val) <= innerDeadzone) return 0;
        int    sign = val > 0 ? 1 : -1;
        double abs  = (Math.Abs(val) - innerDeadzone) / (1.0 - innerDeadzone);
        abs = Math.Clamp(abs, 0.0, 1.0);
        // FIX #6: when antiDeadzone is 0, start from 0 (pure quadratic scaling).
        // Use the raw value — Math.Max(antiDeadzone, 1) was incorrectly forcing a
        // minimum of 1, meaning a user setting of 0 never actually reached zero output.
        double scaled = antiDeadzone + (abs * abs) * (32767 - antiDeadzone);
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
    // WIZARD INPUT DETECTION
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
            Thread.Sleep(5);
            Cmd cmd = CheckKey();
            if (cmd != Cmd.None) return (true, cmd, -1);
            bool[] btns;
            lock (_diPollLock)
            {
                try { pad.Poll(); btns = (bool[])pad.GetCurrentState().Buttons.Clone(); }
                catch { return (true, Cmd.Disconnected, -1); }
            }
            for (int i = 0; i < btns.Length; i++)
            {
                if (!btns[i] || (i < baseline.Length && baseline[i])) continue;
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
            Thread.Sleep(5);
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
                if (Math.Abs(val) <= 0.5 || Math.Abs(val - baseline[i]) <= 0.3) continue;
                int sign = val > 0 ? 1 : -1;
                int t = 3000;
                while (t-- > 0)
                {
                    Thread.Sleep(5);
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

    static string CenterText(string text, int width = 43) =>
        "  " + text.PadLeft((width + text.Length) / 2).PadRight(width);

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
        Console.WriteLine(CenterText("Stick Sensitivity"));
        Console.WriteLine("  " + new string('=', 43));
        Console.WriteLine("  [1] Light   (12000-16000) - slightest touch");
        Console.WriteLine("  [2] Normal  (8000-11999)   - balanced (default)");
        Console.WriteLine("  [3] Firm    (4000-7999)    - deliberate push");
        Console.WriteLine();
        Console.WriteLine($"  Current: {SensitivityLabel(currentVal)} ({currentVal})");
        Console.Write("  Pick [1/2/3] or Enter to use default: ");
        string cat = Console.ReadLine()?.Trim() ?? "";

        int min, max, def;
        switch (cat) {
            case "1": min = 12000; max = 16000; def = 13000; break;
            case "3": min = 4000;  max = 7999;  def = 7000;  break;
            default:  min = 8000;  max = 11999; def = 10000; break;
        }

        Console.Write($"  [{min}-{max}] or press Enter: ");
        string inp = Console.ReadLine()?.Trim() ?? "";
        if (int.TryParse(inp, out int v) && v >= min && v <= max)
        { Console.WriteLine($"  ✓ Set to {SensitivityLabel(v)} ({v})"); return v; }
        Console.WriteLine($"  ✓ Set to {SensitivityLabel(def)} ({def})");
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
        Console.Write("  Adjust inner deadzone too? [Y/N]: ");
        if (Console.ReadLine()?.Trim().ToLower() == "y")
        {
            Console.WriteLine("  Left stick:");  profile.LeftStick.InnerDeadzone  = AskInnerDeadzone(profile.LeftStick.InnerDeadzone);
            Console.WriteLine("  Right stick:"); profile.RightStick.InnerDeadzone = AskInnerDeadzone(profile.RightStick.InnerDeadzone);
        }
        SaveProfile(profile);
        Console.WriteLine($"  Saved [{profile.LayoutName}].");
    }

    static void AdjustDeadzone(Joystick pad, string ctrlName, List<string> existing)
    {
        if (existing.Count == 0) { Console.WriteLine("  No layouts."); return; }
        PickLayout(existing, out int idx);
        if (idx < 0) return;
        var profile = LoadProfile(ctrlName, existing[idx]);
        if (profile == null) return;
        Console.WriteLine();
        Console.WriteLine("  " + new string('=', 43));
        Console.WriteLine(CenterText("Inner Deadzone Settings"));
        Console.WriteLine("  " + new string('=', 43));
        Console.WriteLine("  Left stick:");
        profile.LeftStick.InnerDeadzone = AskInnerDeadzone(profile.LeftStick.InnerDeadzone);
        Console.WriteLine("  Right stick:");
        profile.RightStick.InnerDeadzone = AskInnerDeadzone(profile.RightStick.InnerDeadzone);
        SaveProfile(profile);
        Console.WriteLine($"  ✓ Saved [{profile.LayoutName}].");
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

    // FIX #7: also check DpadButtons for index collisions with regular buttons/triggers
    static List<(string Name, int Idx)> FindDuplicates(Profile p)
    {
        var seen       = new Dictionary<int, string>();
        var addedNames = new HashSet<string>();
        var dupes      = new List<(string, int)>();

        // Check buttons + triggers + dpad buttons all in one pass
        var allEntries = p.Buttons
            .Where(kv => kv.Value.HasValue)
            .Select(kv => (kv.Key, kv.Value!.Value))
            .Concat(p.Triggers
                .Where(kv => kv.Value.HasValue)
                .Select(kv => (kv.Key, kv.Value!.Value)))
            .Concat(p.DpadButtons
                .Select(kv => ($"DPAD:{kv.Key}", kv.Value)));

        foreach (var (name, idx) in allEntries)
        {
            if (seen.TryGetValue(idx, out string? prev))
            {
                if (addedNames.Add(name)) dupes.Add((name, idx));
                if (addedNames.Add(prev)) dupes.Add((prev, idx));
            }
            else seen[idx] = name;
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

        // FIX #3 (same pattern as WaitForReconnect): shared flag, background thread
        bool userAborted = false;
        bool reconnectSucceeded = false;
        var abortThread = new Thread(() =>
        {
            try { Console.ReadLine(); }
            catch { }
            if (!reconnectSucceeded) userAborted = true;
        }) { IsBackground = true };
        abortThread.Start();

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

                reconnectSucceeded = true;
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
    static Profile RunWizard(Joystick pad, string ctrlName, string layoutName)
    {
        JoystickState initState;
        lock (_diPollLock)
        {
            try { pad.Poll(); initState = pad.GetCurrentState(); }
            catch
            {
                Console.WriteLine("  Controller disconnected. Reconnect and try again.");
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
        steps.Add(("LT", "btn", "LT"));
        steps.Add(("RT", "btn", "RT"));
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
        foreach (var t in new[] { "LT", "RT" })
            profile.Triggers[t] = results.TryGetValue(t, out var r) ? r.Val : (int?)null;

        var lsUp    = results.GetValueOrDefault("ls_up",    (Val: 0, Sign: 1));
        var lsDown  = results.GetValueOrDefault("ls_down",  (Val: 0, Sign: 1));
        var lsLeft  = results.GetValueOrDefault("ls_left",  (Val: 1, Sign: 1));
        var lsRight = results.GetValueOrDefault("ls_right", (Val: 1, Sign: 1));
        var rsUp    = results.GetValueOrDefault("rs_up",    (Val: 0, Sign: 1));
        var rsDown  = results.GetValueOrDefault("rs_down",  (Val: 0, Sign: 1));
        var rsLeft  = results.GetValueOrDefault("rs_left",  (Val: 1, Sign: 1));
        var rsRight = results.GetValueOrDefault("rs_right", (Val: 1, Sign: 1));

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
        Console.Write("  Set inner deadzone? [Y/N]: ");
        if (Console.ReadLine()?.Trim().ToLower() == "y")
        {
            Console.WriteLine("  Left stick:");  profile.LeftStick.InnerDeadzone  = AskInnerDeadzone();
            Console.WriteLine("  Right stick:"); profile.RightStick.InnerDeadzone = AskInnerDeadzone();
        }

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
                    Console.Write("  Assign anyway? [Y/N]: ");
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

        if (p.IsIncomplete)
            return (new List<string> { "Profile is incomplete (wizard was cancelled or disconnected)" }, warnings);

        int n;
        try { pad.Poll(); n = pad.GetCurrentState().Buttons.Length; }
        catch { return (new List<string> { "Controller not responding" }, warnings); }

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
        Console.WriteLine(CenterText($"Test: {profile.LayoutName}"));
        Console.WriteLine("  " + new string('=', 43));
        Console.WriteLine("  Press buttons to see mapping");
        Console.WriteLine("  Escape = Exit  |  M = Back to Menu");
        Console.WriteLine();

        bool[] prev = new bool[128];
        while (!_stop)
        {
            if (Console.KeyAvailable)
            {
                var key = Console.ReadKey(true).Key;
                if (key == ConsoleKey.Escape || key == ConsoleKey.M) break;
            }
            Thread.Sleep(10);
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
    // FIX #1: removed the duplicate [N] handler that appeared twice in this method
    // ---------------------------------------------------------------------------
    static Profile? SelectLayout(Joystick pad, string ctrlName)
    {
        while (true)
        {
            var all      = ListProfiles(ctrlName);
            var favs     = all.Where(n => LoadProfile(ctrlName, n)?.IsFavorite == true).ToList();
            var others   = all.Where(n => LoadProfile(ctrlName, n)?.IsFavorite != true).ToList();
            var existing = favs.Concat(others).ToList();

            Console.WriteLine();
            Console.WriteLine("  " + new string('=', 43));
            Console.WriteLine(CenterText(ctrlName));
            Console.WriteLine("  " + new string('=', 43));
            Console.WriteLine();

            if (existing.Count > 0)
            {
                for (int i = 0; i < existing.Count; i++)
                {
                    var p    = LoadProfile(ctrlName, existing[i]);
                    string fav  = p?.IsFavorite == true ? "★" : " ";
                    string dup  = p != null && FindDuplicates(p).Count > 0 ? " [!]" : "";
                    Console.WriteLine($"  [{i + 1}]{fav} {existing[i]}{dup}");
                }
            }
            else
            {
                Console.WriteLine("  (no layouts saved)");
            }

            Console.WriteLine();
            Console.WriteLine("  [N] New Layout");
            Console.WriteLine("  [1-{0}] Load Layout".Replace("{0}", existing.Count.ToString()));
            if (existing.Count > 0)
            {
                Console.WriteLine();
                string? lastUsed = LoadLastUsed(ctrlName);
                if (lastUsed != null && existing.Contains(lastUsed))
                    Console.WriteLine($"  [L] Last: {lastUsed}");
                Console.WriteLine("  [E] Edit Layout");
                Console.WriteLine("  [T] Test Layout");
                Console.WriteLine("  [R] Rename");
                Console.WriteLine("  [D] Delete");
                Console.WriteLine("  [A] Sensitivity");
                Console.WriteLine("  [Z] Deadzone");
                Console.WriteLine("  [G] Game Launcher");
                Console.WriteLine("  [X] Export");
                Console.WriteLine("  [I] Import");
                Console.WriteLine();
            }
            Console.WriteLine("  [S] Settings");
            Console.WriteLine("  [B] Back");
            Console.WriteLine();
            Console.Write("  Choice: ");
            string choice = Console.ReadLine()?.Trim() ?? "";
            string cl     = choice.ToLower();

            if (cl == "b") { return null; }

            if (cl == "s") { ShowSettings(); Banner(); continue; }

            // FIX #1: [N] handler appears exactly once here — duplicate removed
            if (cl == "n")
            {
                Console.Write("  Layout name: ");
                string name = Console.ReadLine()?.Trim() ?? "default";
                if (string.IsNullOrEmpty(name)) name = "default";
                if (existing.Any(e => Safe(e) == Safe(name)))
                {
                    Console.Write($"  '{name}' exists. Overwrite? [Y/N]: ");
                    if (Console.ReadLine()?.Trim().ToLower() != "y") continue;
                }
                var wp = RunWizard(pad, ctrlName, name);
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

            if (cl == "e" && existing.Count > 0)
            { PickLayout(existing, out int ec); if (ec >= 0) { var p = LoadProfile(ctrlName, existing[ec]); if (p != null) EditLayout(pad, p); } continue; }

            if (cl == "t" && existing.Count > 0)
            { PickLayout(existing, out int tc); if (tc >= 0) { var p = LoadProfile(ctrlName, existing[tc]); if (p != null) RunTestMode(pad, p); } continue; }

            if (cl == "a" && existing.Count > 0) { AdjustSensitivity(pad, ctrlName, existing); continue; }

            if (cl == "z" && existing.Count > 0) { AdjustDeadzone(pad, ctrlName, existing); continue; }

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
                    Console.Write($"  Delete '{del}'? [Y/N]: ");
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

    // ---------------------------------------------------------------------------
    // MULTI-CONTROLLER MODE
    // ---------------------------------------------------------------------------
    static void RunMultiControllerMode()
    {
        // FIX #8: invalidate pad cache when entering multi-mode fresh
        _cachedMultiPads = null;

        while (!_stop)
        {
            var multiSetups = ListMultiSetups();
            Console.WriteLine();
            Console.WriteLine("  " + new string('=', 43));
            Console.WriteLine(CenterText("Multi-Controller Setups"));
            Console.WriteLine("  " + new string('=', 43));
            Console.WriteLine();

            if (multiSetups.Count > 0)
            {
                for (int i = 0; i < multiSetups.Count; i++)
                {
                    var setup = LoadMultiSetupFromFile(multiSetups[i]);
                    if (setup != null)
                    {
                        int controllerCount = setup.Controllers.Count;
                        Console.WriteLine($"  [{i + 1}] {setup.Name} ({controllerCount} controllers)");
                    }
                }
                Console.WriteLine();
            }
            else
            {
                Console.WriteLine("  (no multi-setups saved)");
                Console.WriteLine();
            }

            Console.WriteLine("  [N] New Setup");
            if (multiSetups.Count > 0)
                Console.WriteLine($"  [1-{multiSetups.Count}] Load Setup");
            Console.WriteLine("  [B] Back to Main Menu");
            Console.WriteLine();
            Console.Write("  Choice: ");
            string choice = Console.ReadLine()?.Trim().ToLower() ?? "";

            if (choice == "b") break;
            if (choice == "n") { CreateNewMultiSetup(); continue; }
            if (int.TryParse(choice, out int idx) && idx >= 1 && idx <= multiSetups.Count)
            {
                var setup = LoadMultiSetupFromFile(multiSetups[idx - 1]);
                if (setup != null) RunMultiSetupManagement(setup);
                continue;
            }
            Console.WriteLine("  Invalid choice.");
        }
    }

    static List<string> ListMultiSetups()
    {
        string multiDir = Path.Combine(AppContext.BaseDirectory, "profiles", "MultiSetups");
        try
        {
            Directory.CreateDirectory(multiDir);
            return Directory.GetFiles(multiDir, "*.json")
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .Where(n => !string.IsNullOrEmpty(n))
                .ToList();
        }
        catch { return new List<string>(); }
    }

    static string MultiSetupPath(string setupName) =>
        Path.Combine(AppContext.BaseDirectory, "profiles", "MultiSetups", $"{setupName}.json");

    static void CreateNewMultiSetup()
    {
        var pads = GetAllRealPads();
        _cachedMultiPads = pads; // cache after fresh detection
        if (pads.Count == 0)
        {
            Console.WriteLine("  No controllers detected.");
            Console.ReadLine();
            return;
        }

        Console.WriteLine();
        Console.WriteLine("  " + new string('=', 43));
        Console.WriteLine(CenterText("New Multi-Setup"));
        Console.WriteLine("  " + new string('=', 43));
        Console.WriteLine();
        Console.WriteLine("  Detected Controllers:");
        for (int i = 0; i < pads.Count; i++)
            Console.WriteLine($"    [{i + 1}] {pads[i].DisplayName}");
        Console.WriteLine();

        Console.Write("  Setup name (e.g. GameSession1): ");
        string setupName = Console.ReadLine()?.Trim() ?? "NewSetup";
        if (string.IsNullOrEmpty(setupName)) setupName = "NewSetup";

        var setup = new MultiSetup { Name = setupName };

        for (int i = 0; i < pads.Count; i++)
        {
            Console.WriteLine();
            Console.WriteLine("  " + new string('=', 43));
            Console.WriteLine($"  [{i + 1}/{pads.Count}] {pads[i].DisplayName}");
            Console.WriteLine("  " + new string('=', 43));

            Console.WriteLine("  [N] Create new layout (wizard)");
            Console.WriteLine("  [E] Use existing layout");
            Console.Write("  Choice: ");
            string layoutChoice = Console.ReadLine()?.Trim().ToLower() ?? "";

            Profile? profile = null;
            if (layoutChoice == "n")
            {
                Console.Write("  Layout name: ");
                string layoutName = Console.ReadLine()?.Trim() ?? $"player{i + 1}";
                if (string.IsNullOrEmpty(layoutName)) layoutName = $"player{i + 1}";
                profile = RunWizard(pads[i].Pad, pads[i].ProfileName, layoutName);
                if (profile == null || profile.IsIncomplete)
                {
                    Console.WriteLine("  Wizard cancelled.");
                    Console.ReadLine();
                    return;
                }
            }
            else if (layoutChoice == "e")
            {
                var layouts = ListProfiles(pads[i].ProfileName);
                if (layouts.Count == 0)
                {
                    Console.WriteLine("  No saved layouts for this controller.");
                    i--;
                    continue;
                }
                PickLayout(layouts, out int layoutIdx);
                if (layoutIdx >= 0)
                    profile = LoadProfile(pads[i].ProfileName, layouts[layoutIdx]);
            }

            if (profile != null)
            {
                setup.Controllers.Add(new MultiSetupController
                {
                    InstanceGuid = pads[i].InstanceGuid.ToString(),
                    DisplayName = pads[i].DisplayName,
                    ProfileName = pads[i].ProfileName,
                    LayoutName = profile.LayoutName
                });
            }
        }

        if (setup.Controllers.Count > 0)
        {
            SaveMultiSetup(setup);
            Console.WriteLine($"\n  ✓ Setup saved! ({setup.Controllers.Count} controllers)");
            Console.WriteLine("  Mapping complete. Running multi-controller setup...\n");
            Thread.Sleep(1000);
            RunMultiSetupManagement(setup);
        }
        else
        {
            Console.WriteLine("\n  No controllers configured. Setup cancelled.");
            Console.ReadLine();
        }
    }

    static void RunMultiSetupManagement(MultiSetup setup)
    {
        // FIX #8: detect pads once when entering management for this setup,
        // then reuse the cached list for all menu actions (edit, test, adjust, etc.)
        // Only re-detect when explicitly running, or when cache is stale/null.
        if (_cachedMultiPads == null)
            _cachedMultiPads = GetAllRealPads();

        while (!_stop)
        {
            var pads = _cachedMultiPads;
            if (pads == null) break;
            var availableGuids = pads.Select(p => p.InstanceGuid.ToString()).ToHashSet();
            var activeControllers = setup.Controllers.Where(c => availableGuids.Contains(c.InstanceGuid)).ToList();

            Console.WriteLine();
            Console.WriteLine("  " + new string('=', 43));
            Console.WriteLine(CenterText(setup.Name));
            Console.WriteLine("  " + new string('=', 43));
            Console.WriteLine();

            if (activeControllers.Count < setup.Controllers.Count)
            {
                Console.WriteLine($"  ⚠️  {setup.Controllers.Count - activeControllers.Count} controller(s) not found");
                Console.WriteLine();
            }

            for (int i = 0; i < activeControllers.Count; i++)
            {
                var c = activeControllers[i];
                Console.WriteLine($"  P{i + 1}: {c.DisplayName}");
                Console.WriteLine($"      Layout: [{c.LayoutName}]");
            }
            Console.WriteLine();

            Console.WriteLine("  [R] Run Now");
            if (activeControllers.Count > 0)
            {
                Console.WriteLine("  [E] Edit Controller");
                Console.WriteLine("  [T] Test Controller");
                Console.WriteLine("  [A] Adjust Sensitivity");
                Console.WriteLine("  [Z] Adjust Deadzone");
            }
            Console.WriteLine("  [G] Set Game");
            Console.WriteLine("  [D] Delete Setup");
            Console.WriteLine("  [B] Back");
            Console.WriteLine();
            Console.Write("  Choice: ");
            string choice = Console.ReadLine()?.Trim().ToLower() ?? "";

            if (choice == "b") break;
            if (choice == "d")
            {
                Console.Write("  Delete this setup? [Y/N]: ");
                if (Console.ReadLine()?.Trim().ToLower() == "y")
                {
                    try { File.Delete(MultiSetupPath(setup.Name)); }
                    catch { }
                    Console.WriteLine("  Deleted.");
                    break;
                }
            }
            if (choice == "g")
            {
                Console.WriteLine("  Set game for this setup...");
                Console.WriteLine("  [C] Clear  |  [Enter] Browse for .exe");
                Console.Write("  Choice: ");
                string nav = Console.ReadLine()?.Trim().ToLower() ?? "";
                if (nav == "c") { setup.GameExePath = null; SaveMultiSetup(setup); Console.WriteLine("  Cleared."); }
                else
                {
                    string? gamePath = RunFileDialog(save: false, filter: "Executable (*.exe)|*.exe|All (*.*)|*.*",
                        title: "Select Game EXE", defaultFile: null);
                    if (!string.IsNullOrEmpty(gamePath))
                    {
                        setup.GameExePath = gamePath;
                        SaveMultiSetup(setup);
                        Console.WriteLine($"  Set to: {Path.GetFileNameWithoutExtension(gamePath)}");
                    }
                }
                continue;
            }
            if (choice == "a" && activeControllers.Count > 0)
            {
                Console.WriteLine("  Adjust sensitivity for which controller?");
                for (int i = 0; i < activeControllers.Count; i++)
                    Console.WriteLine($"    [{i + 1}] {activeControllers[i].DisplayName}");
                Console.Write("  Choice: ");
                if (int.TryParse(Console.ReadLine(), out int ac) && ac >= 1 && ac <= activeControllers.Count)
                {
                    var c = activeControllers[ac - 1];
                    var profile = LoadProfile(c.ProfileName, c.LayoutName);
                    if (profile != null)
                    {
                        profile.AntiDeadzone = AskSensitivity(profile.AntiDeadzone);
                        SaveProfile(profile);
                        Console.WriteLine($"  ✓ Saved [{profile.LayoutName}].");
                    }
                }
                continue;
            }
            if (choice == "z" && activeControllers.Count > 0)
            {
                Console.WriteLine("  Adjust deadzone for which controller?");
                for (int i = 0; i < activeControllers.Count; i++)
                    Console.WriteLine($"    [{i + 1}] {activeControllers[i].DisplayName}");
                Console.Write("  Choice: ");
                if (int.TryParse(Console.ReadLine(), out int zc) && zc >= 1 && zc <= activeControllers.Count)
                {
                    var c = activeControllers[zc - 1];
                    var profile = LoadProfile(c.ProfileName, c.LayoutName);
                    if (profile != null)
                    {
                        try
                        {
                            Console.WriteLine("  Left stick:");
                            profile.LeftStick.InnerDeadzone = AskInnerDeadzone(profile.LeftStick.InnerDeadzone);
                            Console.WriteLine("  Right stick:");
                            profile.RightStick.InnerDeadzone = AskInnerDeadzone(profile.RightStick.InnerDeadzone);
                            SaveProfile(profile);
                            Console.WriteLine($"  ✓ Saved [{profile.LayoutName}].");
                        }
                        catch (Exception ex) { Console.WriteLine($"  Error: {ex.Message}"); }
                    }
                }
                continue;
            }
            if (choice == "r" && activeControllers.Count > 0)
            {
                // FIX #9: trigger game launch for multi-setup before running
                if (!string.IsNullOrEmpty(setup.GameExePath))
                {
                    string exeName = Path.GetFileNameWithoutExtension(setup.GameExePath);
                    Console.Write($"\n  Launch {exeName}? [Y/n]: ");
                    if (Console.ReadLine()?.Trim().ToLower() != "n")
                    {
                        if (!File.Exists(setup.GameExePath))
                            Console.WriteLine($"  Not found: {setup.GameExePath}");
                        else
                        {
                            try
                            {
                                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                                    { FileName = setup.GameExePath,
                                      WorkingDirectory = Path.GetDirectoryName(setup.GameExePath) ?? "",
                                      UseShellExecute = true });
                                Console.WriteLine($"  Launched {exeName}. Starting remapper...");
                                Thread.Sleep(800);
                            }
                            catch (Exception ex) { Console.WriteLine($"  Could not launch: {ex.Message}"); }
                        }
                    }
                }

                // FIX #8: invalidate cache after run so next entry re-detects fresh
                RunMultiControllers(activeControllers, pads);
                _cachedMultiPads = null;
                continue;
            }
            if (choice == "e" && activeControllers.Count > 0)
            {
                Console.WriteLine("  Edit layout for which controller?");
                for (int i = 0; i < activeControllers.Count; i++)
                    Console.WriteLine($"    [{i + 1}] {activeControllers[i].DisplayName}");
                Console.Write("  Choice: ");
                if (int.TryParse(Console.ReadLine(), out int ec) && ec >= 1 && ec <= activeControllers.Count)
                {
                    var c = activeControllers[ec - 1];
                    var profile = LoadProfile(c.ProfileName, c.LayoutName);
                    var pad = pads.FirstOrDefault(p => p.InstanceGuid.ToString() == c.InstanceGuid);
                    if (profile != null && pad != null) EditLayout(pad.Pad, profile);
                    else Console.WriteLine("  Controller or layout not found.");
                }
                continue;
            }
            if (choice == "t" && activeControllers.Count > 0)
            {
                Console.WriteLine("  Test layout for which controller?");
                for (int i = 0; i < activeControllers.Count; i++)
                    Console.WriteLine($"    [{i + 1}] {activeControllers[i].DisplayName}");
                Console.Write("  Choice: ");
                if (int.TryParse(Console.ReadLine(), out int tc) && tc >= 1 && tc <= activeControllers.Count)
                {
                    var c = activeControllers[tc - 1];
                    var profile = LoadProfile(c.ProfileName, c.LayoutName);
                    var pad = pads.FirstOrDefault(p => p.InstanceGuid.ToString() == c.InstanceGuid);
                    if (profile != null && pad != null) RunTestMode(pad.Pad, profile);
                    else Console.WriteLine("  Controller or layout not found.");
                }
                continue;
            }
        }
    }

    static void RunMultiControllers(List<MultiSetupController> activeControllers, List<PadInfo> allPads)
    {
        var tasks   = new List<Task>();
        var ctsList = new List<CancellationTokenSource>();

        // FIX #2: create one shared ViGEmClient and pass it to every core instance.
        // The original code created a NEW ViGEmClient per controller and discarded
        // the shared one — wasting handles and never releasing them correctly.
        using var client = new ViGEmClient();

        Console.WriteLine();
        Console.WriteLine("  " + new string('=', 43));
        Console.WriteLine("  DIRECT360 is now running");
        Console.WriteLine("  " + new string('=', 43));
        for (int i = 0; i < activeControllers.Count; i++)
        {
            var ac = activeControllers[i];
            var pad = allPads.FirstOrDefault(p => p.InstanceGuid.ToString() == ac.InstanceGuid);
            if (pad == null) continue;
            var profile = LoadProfile(ac.ProfileName, ac.LayoutName);
            if (profile != null)
                Console.WriteLine($"  P{i + 1}: {ac.DisplayName} -> {ac.LayoutName}");
        }
        Console.WriteLine("  " + new string('=', 43));
        Console.WriteLine("  ✓ Controllers active and remapping");
        Console.WriteLine("  M = Back to Menu  |  Ctrl+C = Exit");
        Console.WriteLine();

        foreach (var ac in activeControllers)
        {
            var cts = new CancellationTokenSource();
            ctsList.Add(cts);
            var pad = allPads.FirstOrDefault(p => p.InstanceGuid.ToString() == ac.InstanceGuid);
            if (pad == null) { Console.WriteLine($"  Warning: {ac.DisplayName} not found."); continue; }
            var profile = LoadProfile(ac.ProfileName, ac.LayoutName);
            if (profile != null)
                // Pass the shared client — RunRemapperCore will NOT create an owned one
                tasks.Add(Task.Run(() => RunRemapperCore(pad.Pad, profile, client, cts)));
        }

        var keyCheckSw = System.Diagnostics.Stopwatch.StartNew();
        while (!_stop && !tasks.All(t => t.IsCompleted))
        {
            // Check console input only every 100ms to minimize lock contention with polling threads
            if (keyCheckSw.ElapsedMilliseconds >= 100)
            {
                keyCheckSw.Restart();
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true);
                    if (key.Key == ConsoleKey.M)
                    {
                        Console.WriteLine("\n  Returning to menu...");
                        foreach (var cts in ctsList) cts.Cancel();
                        break;
                    }
                }
            }
            System.Threading.Thread.Sleep(10);
        }

        if (_stop) foreach (var cts in ctsList) cts.Cancel();
        try { Task.WaitAll(tasks.ToArray(), 3000); } catch { }

        foreach (var cts in ctsList) cts.Dispose();
        Console.WriteLine();
    }

    static void SaveMultiSetup(MultiSetup setup)
    {
        try
        {
            string multiDir = Path.Combine(AppContext.BaseDirectory, "profiles", "MultiSetups");
            Directory.CreateDirectory(multiDir);
            File.WriteAllText(MultiSetupPath(setup.Name),
                JsonSerializer.Serialize(setup, JsonOpts));
        }
        catch (Exception ex) { Console.WriteLine($"  Could not save: {ex.Message}"); }
    }

    static MultiSetup? LoadMultiSetupFromFile(string setupName)
    {
        string path = MultiSetupPath(setupName);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<MultiSetup>(File.ReadAllText(path)); }
        catch { return null; }
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

    [JsonIgnore] public bool IsIncomplete { get; private set; } = false;

    public static Profile Incomplete(string ctrl, string layout) =>
        new Profile { ControllerName = ctrl, LayoutName = layout, IsIncomplete = true };

    // FIX #5: use a nullable int (int?) to cleanly distinguish "not set" from 0.
    // Previously -1 was used as a sentinel but the property checked < 0, accepting
    // any negative value. A nullable field is unambiguous and serializes correctly.
    [JsonPropertyName("anti_deadzone")]
    public int? AntiDeadzoneRaw { get; set; } = null;

    [JsonIgnore]
    public int AntiDeadzone
    {
        get => AntiDeadzoneRaw ?? 10000;
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
        var handle = GetStdHandle(-10);
        var records = new INPUT_RECORD[2];
        records[0].EventType = 1;
        records[0].KeyEvent  = new KEY_EVENT_RECORD
            { bKeyDown = 1, wRepeatCount = 1, wVirtualKeyCode = 0x0D, uChar = '\r' };
        records[1].EventType = 1;
        records[1].KeyEvent  = new KEY_EVENT_RECORD
            { bKeyDown = 0, wRepeatCount = 1, wVirtualKeyCode = 0x0D, uChar = '\r' };
        WriteConsoleInput(handle, records, 2, out _);
    }
}

// Multi-Controller Setup Classes
class MultiSetupController
{
    [JsonPropertyName("instance_guid")] public string InstanceGuid { get; set; } = "";
    [JsonPropertyName("display_name")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("profile_name")] public string ProfileName { get; set; } = "";
    [JsonPropertyName("layout_name")] public string LayoutName { get; set; } = "";
}

class MultiSetup
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("controllers")] public List<MultiSetupController> Controllers { get; set; } = new();
    [JsonPropertyName("game_exe_path")] public string? GameExePath { get; set; } = null;
}