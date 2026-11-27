using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Diagnostics;
using System.Management;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Reflection;
using Microsoft.Win32;

internal static class WmiOpts
{
    public static EnumerationOptions Create()
    {
        return new EnumerationOptions { Timeout = TimeSpan.FromSeconds(10), ReturnImmediately = false };
    }
}

internal static class UiDpi
{
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr hdc, int index);

    private static float scale = 1F;
    private static bool applied;

    internal static float Scale { get { return scale; } }

    private static float systemScale = -1F;

    // Extra zoom on top of the system DPI scale (used while the main window is maximized or snapped). S() / R() follow it.
    internal static void SetUserZoom(float factor)
    {
        if (systemScale < 0F) systemScale = scale;
        scale = systemScale * factor;
    }

    // Must run before any window is created. System-DPI awareness keeps text crisp and lets the layout be scaled once.
    internal static void Initialize()
    {
        try
        {
            if (!SetProcessDpiAwarenessContext(new IntPtr(-2))) SetProcessDPIAware();
        }
        catch
        {
            try { SetProcessDPIAware(); } catch { }
        }
        try
        {
            IntPtr dc = GetDC(IntPtr.Zero);
            if (dc != IntPtr.Zero)
            {
                try
                {
                    int dpi = GetDeviceCaps(dc, 88); // LOGPIXELSX
                    if (dpi > 0) scale = dpi / 96F;
                }
                finally { ReleaseDC(IntPtr.Zero, dc); }
            }
        }
        catch { }
        if (scale < 1F) scale = 1F;
    }

    // Layout built before MarkApplied() is in 96-DPI units and is scaled in one pass; code that runs later must use S().
    internal static void MarkApplied() { applied = true; }

    internal static int S(int value)
    {
        return applied ? (int)Math.Round(value * scale) : value;
    }

    private static int R(int value) { return (int)Math.Round(value * scale); }

    internal static void ScaleChildren(Control parent)
    {
        if (Math.Abs(scale - 1F) < 0.001F) return;
        foreach (Control child in parent.Controls)
        {
            if (child.Dock == DockStyle.None)
            {
                if (child.AutoSize) child.Location = new Point(R(child.Left), R(child.Top));
                else child.SetBounds(R(child.Left), R(child.Top), R(child.Width), R(child.Height));
            }
            else if (child.Dock == DockStyle.Top || child.Dock == DockStyle.Bottom) child.Height = R(child.Height);
            else if (child.Dock == DockStyle.Left || child.Dock == DockStyle.Right) child.Width = R(child.Width);

            Padding margin = child.Margin;
            child.Margin = new Padding(R(margin.Left), R(margin.Top), R(margin.Right), R(margin.Bottom));
            if (child is ScrollableControl)
            {
                Padding padding = child.Padding;
                child.Padding = new Padding(R(padding.Left), R(padding.Top), R(padding.Right), R(padding.Bottom));
            }
            ScaleChildren(child);
        }
    }

    internal static void ScaleDialog(Form dialog)
    {
        if (Math.Abs(scale - 1F) < 0.001F) return;
        Size client = dialog.ClientSize;
        dialog.ClientSize = new Size(R(client.Width), R(client.Height));
        ScaleChildren(dialog);
    }
}

internal static class Program
{
    internal const string SingleInstanceMutexName = "Local\\NitroHub_SingleInstance_Mutex";
    internal static readonly int WmShowNitroHub = RegisterWindowMessage("WM_SHOW_NITROHUB_INSTANCE");

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    // Wakes the already-running (possibly tray-hidden) instance. HWND_BROADCAST skips owned top-level windows, and a window
    // with ShowInTaskbar = false is owned, so the old broadcast could miss it. Post straight to every top-level window of the
    // other NitroHub process instead, and keep the broadcast only as a fallback.
    private static void SignalExistingInstance()
    {
        try { AllowSetForegroundWindow(-1); } catch { }
        bool posted = false;
        try
        {
            List<uint> ids = new List<uint>();
            using (Process self = Process.GetCurrentProcess())
            {
                foreach (Process other in Process.GetProcessesByName(self.ProcessName))
                {
                    try { if (other.Id != self.Id) ids.Add((uint)other.Id); }
                    finally { other.Dispose(); }
                }
            }
            if (ids.Count > 0)
            {
                EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
                {
                    uint pid;
                    GetWindowThreadProcessId(hWnd, out pid);
                    if (ids.Contains(pid) && PostMessage(hWnd, WmShowNitroHub, IntPtr.Zero, IntPtr.Zero))
                        posted = true;
                    return true;
                }, IntPtr.Zero);
            }
        }
        catch { }
        if (!posted)
            PostMessage((IntPtr)0xFFFF, WmShowNitroHub, IntPtr.Zero, IntPtr.Zero);
    }

    internal static string GetExecutablePath()
    {
        try
        {
            using (Process current = Process.GetCurrentProcess())
            {
                if (current.MainModule != null && !String.IsNullOrEmpty(current.MainModule.FileName))
                    return current.MainModule.FileName;
            }
        }
        catch { }
        return Application.ExecutablePath;
    }

    [STAThread]
    private static void Main(string[] args)
    {
        bool startMinimized = false;
        bool startupApplyOnly = false;
        if (args != null)
        {
            foreach (string arg in args)
            {
                if (String.Equals(arg, "--minimized", StringComparison.OrdinalIgnoreCase))
                    startMinimized = true;
                else if (String.Equals(arg, "--apply-saved-charge-limit", StringComparison.OrdinalIgnoreCase))
                    startupApplyOnly = true;
            }
        }

        if (startupApplyOnly)
        {
            ChargeLimitForm.ApplySavedChargeLimitAtStartup();
            ChargeLimitForm.ApplySavedBluelightAtStartup();
            return;
        }

        bool createdNew;
        using (System.Threading.Mutex mutex = new System.Threading.Mutex(true, SingleInstanceMutexName, out createdNew))
        {
            if (!createdNew)
            {
                SignalExistingInstance();
                return;
            }

            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; }
            catch { }

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e)
            {
                LogCrash("ThreadException", e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                LogCrash("UnhandledException", e.ExceptionObject as Exception);
            };

            UiDpi.Initialize();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                Application.Run(new ChargeLimitForm(startMinimized));
            }
            catch (Exception ex)
            {
                LogCrash("MainRun", ex);
            }
        }
    }

    internal static void LogCrash(string source, Exception ex)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NitroHub", "logs");
            Directory.CreateDirectory(dir);
            string msg = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + source + "] " + (ex != null ? ex.ToString() : "Unknown") + Environment.NewLine;
            File.AppendAllText(Path.Combine(dir, "crash.log"), msg, Encoding.UTF8);
        }
        catch { }
    }
}

internal static class BluelightShield
{
    internal const int LevelCount = 5;
    private const int AttachedToDesktop = 0x1;

    private static readonly string[] LevelNames = new string[] { "Off", "Low", "Medium", "High", "Long Hour" };
    private static readonly double[] RedScale = new double[] { 1.00, 1.00, 1.00, 1.00, 1.00 };
    private static readonly double[] GreenScale = new double[] { 1.00, 0.95, 0.88, 0.80, 0.84 };
    private static readonly double[] BlueScale = new double[] { 1.00, 0.82, 0.65, 0.52, 0.58 };
    // Windows rejects gamma ramps that stray too far from linear. If a display refuses a level,
    // the same level is retried at a weaker strength instead of failing outright.
    private static readonly double[] StrengthSteps = new double[] { 1.00, 0.92, 0.84, 0.76, 0.68, 0.60 };

    [StructLayout(LayoutKind.Sequential)]
    private struct RAMP
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public ushort[] Red;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public ushort[] Green;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public ushort[] Blue;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateDC(string lpszDriver, string lpszDevice, string lpszOutput, IntPtr lpInitData);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool SetDeviceGammaRamp(IntPtr hdc, ref RAMP lpRamp);

    internal static string GetName(int level)
    {
        if (level < 0 || level >= LevelCount) return LevelNames[0];
        return LevelNames[level];
    }

    internal static string Describe(int level)
    {
        if (level <= 0 || level >= LevelCount)
            return "Off - the screen uses its normal colors.";
        int reduction = (int)Math.Round((1.0 - BlueScale[level]) * 100.0);
        switch (level)
        {
            case 1: return "Low - blue channel reduced by about " + reduction + "%. Basic protection with a light warm tint.";
            case 2: return "Medium - blue channel reduced by about " + reduction + "%. A clearly warmer picture.";
            case 3: return "High - blue channel reduced by about " + reduction + "%. Maximum protection, strongly warm colors.";
            default: return "Long Hour - blue channel reduced by about " + reduction + "%. A balanced warm tone for long sessions.";
        }
    }

    internal static int Apply(int level, out bool softened)
    {
        softened = false;
        if (level < 0 || level >= LevelCount) level = 0;
        int applied = 0;
        foreach (string device in GetAttachedDisplays())
        {
            bool ok = false;
            for (int step = 0; step < StrengthSteps.Length && !ok; step++)
            {
                ok = SetRamp(device, level, StrengthSteps[step]);
                if (ok && step > 0) softened = true;
            }
            if (ok) applied++;
        }
        return applied;
    }

    private static List<string> GetAttachedDisplays()
    {
        List<string> names = new List<string>();
        for (uint index = 0; index < 32; index++)
        {
            DISPLAY_DEVICE device = new DISPLAY_DEVICE();
            device.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
            if (!EnumDisplayDevices(null, index, ref device, 0)) break;
            if ((device.StateFlags & AttachedToDesktop) != 0) names.Add(device.DeviceName);
        }
        return names;
    }

    private static bool SetRamp(string deviceName, int level, double strength)
    {
        IntPtr hdc = CreateDC("DISPLAY", deviceName, null, IntPtr.Zero);
        if (hdc == IntPtr.Zero) return false;
        try
        {
            RAMP ramp = BuildRamp(level, strength);
            return SetDeviceGammaRamp(hdc, ref ramp);
        }
        finally
        {
            DeleteDC(hdc);
        }
    }

    private static RAMP BuildRamp(int level, double strength)
    {
        RAMP ramp = new RAMP();
        ramp.Red = new ushort[256];
        ramp.Green = new ushort[256];
        ramp.Blue = new ushort[256];
        double red = Soften(RedScale[level], strength);
        double green = Soften(GreenScale[level], strength);
        double blue = Soften(BlueScale[level], strength);
        for (int i = 0; i < 256; i++)
        {
            double identity = i * 257.0;
            ramp.Red[i] = ToRampValue(identity * red);
            ramp.Green[i] = ToRampValue(identity * green);
            ramp.Blue[i] = ToRampValue(identity * blue);
        }
        return ramp;
    }

    private static double Soften(double scale, double strength)
    {
        return 1.0 - (1.0 - scale) * strength;
    }

    private static ushort ToRampValue(double value)
    {
        if (value < 0.0) value = 0.0;
        if (value > 65535.0) value = 65535.0;
        return (ushort)Math.Round(value);
    }

    private static string LevelPath()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NitroHub", "bluelight-level.txt");
    }

    internal static int ReadSavedLevel()
    {
        try
        {
            string path = LevelPath();
            if (!File.Exists(path)) return 0;
            int level;
            if (Int32.TryParse(File.ReadAllText(path).Trim(), out level) && level >= 0 && level < LevelCount)
                return level;
        }
        catch { }
        return 0;
    }

    internal static void SaveLevel(int level)
    {
        string path = LevelPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        SafeFile.WriteAllText(path, level.ToString(), Encoding.UTF8);
    }
}

internal static class SafeFile
{
    // Writes to a temporary file first, then swaps it in, so a crash or power loss never leaves a half-written settings file.
    internal static void WriteAllText(string path, string contents, Encoding encoding)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, contents, encoding);
        Commit(temp, path);
    }

    internal static void WriteAllText(string path, string contents)
    {
        WriteAllText(path, contents, new UTF8Encoding(false));
    }

    internal static void WriteAllLines(string path, string[] lines)
    {
        WriteAllLines(path, lines, new UTF8Encoding(false));
    }

    internal static void WriteAllLines(string path, string[] lines, Encoding encoding)
    {
        string temp = path + ".tmp";
        File.WriteAllLines(temp, lines, encoding);
        Commit(temp, path);
    }

    private static void Commit(string temp, string path)
    {
        try
        {
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }
    }
}

internal sealed class ThemedComboBox : ComboBox
{
    private bool hover;
    public Color BorderColor { get; set; }
    public Color AccentColor { get; set; }

    public ThemedComboBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        DropDownStyle = ComboBoxStyle.DropDownList;
        DrawMode = DrawMode.OwnerDrawFixed;
        FlatStyle = FlatStyle.Flat;
        ItemHeight = 24;
        BorderColor = Color.FromArgb(70, 82, 95);
        AccentColor = Color.FromArgb(210, 48, 66);
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void OnSelectedIndexChanged(EventArgs e) { Invalidate(); base.OnSelectedIndexChanged(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(UiPaint.ParentBack(this, BackColor));
        Color back = BackColor;
        Color fore = Enabled ? ForeColor : Color.FromArgb(128, 138, 150);
        Color border = (Focused || DroppedDown) ? AccentColor : (hover ? ControlPaint.Light(BorderColor) : BorderColor);

        RectangleF rect = new RectangleF(0.5F, 0.5F, Math.Max(1F, Width - 1F), Math.Max(1F, Height - 1F));
        float d = 14F;
        using (GraphicsPath path = new GraphicsPath())
        {
            path.AddArc(rect.Left, rect.Top, d, d, 180F, 90F);
            path.AddArc(rect.Right - d, rect.Top, d, d, 270F, 90F);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0F, 90F);
            path.AddArc(rect.Left, rect.Bottom - d, d, d, 90F, 90F);
            path.CloseFigure();

            using (SolidBrush brush = new SolidBrush(back))
                g.FillPath(brush, path);
            using (Pen pen = new Pen(border, 1F))
                g.DrawPath(pen, path);
        }

        string text = SelectedItem != null ? GetItemText(SelectedItem) : String.Empty;
        TextRenderer.DrawText(g, text, Font, new Rectangle(8, 0, Math.Max(1, Width - 30), Height), fore,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        int cx = Width - 16, cy = Height / 2;
        using (Pen arrow = new Pen(fore, 1.6F))
        {
            arrow.StartCap = LineCap.Round; arrow.EndCap = LineCap.Round;
            g.DrawLines(arrow, new Point[] { new Point(cx - 4, cy - 2), new Point(cx, cy + 2), new Point(cx + 4, cy - 2) });
        }
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using (SolidBrush brush = new SolidBrush(selected ? AccentColor : BackColor))
            e.Graphics.FillRectangle(brush, e.Bounds);
        Rectangle textRect = new Rectangle(e.Bounds.X + 8, e.Bounds.Y, Math.Max(1, e.Bounds.Width - 12), e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font, textRect,
            selected ? Color.White : ForeColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }
}

internal static class UiLang
{
    internal const string English = "English (US)";
    internal const string Simplified = "简体中文";

    private sealed class PackInfo
    {
        public string DisplayName;
        public string FontFamily;
        public int Order;
        public Dictionary<string, string> Entries;
    }

    private static readonly Dictionary<string, Dictionary<string, string>> Packs;
    private static readonly Dictionary<string, string> FontFamilies;
    internal static readonly string[] Languages;

    static UiLang()
    {
        Packs = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        FontFamilies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, PackInfo> loaded = new Dictionary<string, PackInfo>(StringComparer.OrdinalIgnoreCase);

        LoadEmbeddedPacks(loaded);
        LoadExternalPacks(loaded);

        List<PackInfo> ordered = new List<PackInfo>(loaded.Values);
        ordered.Sort(delegate(PackInfo a, PackInfo b)
        {
            int cmp = a.Order.CompareTo(b.Order);
            if (cmp != 0) return cmp;
            return String.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
        });

        List<string> names = new List<string>();
        names.Add(English);
        foreach (PackInfo info in ordered)
        {
            if (IsEnglish(info.DisplayName)) continue;
            names.Add(info.DisplayName);
            Packs[info.DisplayName] = info.Entries;
            if (!String.IsNullOrEmpty(info.FontFamily))
                FontFamilies[info.DisplayName] = info.FontFamily;
        }
        Languages = names.ToArray();
    }

    internal static bool IsEnglish(string lang)
    {
        return String.IsNullOrEmpty(lang) || String.Equals(lang, English, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsSimplified(string lang)
    {
        return String.Equals(lang, Simplified, StringComparison.OrdinalIgnoreCase);
    }

    internal static IEnumerable<Dictionary<string, string>> AllPacks { get { return Packs.Values; } }

    internal static string Lookup(string lang, string en)
    {
        Dictionary<string, string> p;
        string r;
        if (lang != null && Packs.TryGetValue(lang, out p) && p.TryGetValue(en, out r)) return r;
        return null;
    }

    internal static string Pick(string lang, string en, string zh)
    {
        if (IsEnglish(lang)) return en;
        string r = Lookup(lang, en);
        if (r != null) return r;
        return IsSimplified(lang) ? zh : en;
    }

    internal static string GetUiFontFamily(string lang)
    {
        string family;
        if (!String.IsNullOrEmpty(lang) && FontFamilies.TryGetValue(lang, out family) && !String.IsNullOrEmpty(family))
            return family;
        return "Segoe UI";
    }

    internal static void ApplyFontToTree(Control root, string lang)
    {
        string targetFamily = GetUiFontFamily(lang);
        ApplyFontRecursive(root, targetFamily);
    }

    private static readonly Dictionary<string, Font> fontCache = new Dictionary<string, Font>();

    // One shared Font per family/size/style: switching language repeatedly no longer allocates new GDI fonts every time.
    internal static Font GetCachedFont(string family, float size, FontStyle style)
    {
        string key = family + "|" + size.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + (int)style;
        Font font;
        if (!fontCache.TryGetValue(key, out font))
        {
            font = new Font(family, size, style);
            fontCache[key] = font;
        }
        return font;
    }

    private static void ApplyFontRecursive(Control parent, string targetFamily)
    {
        foreach (Control c in parent.Controls)
        {
            if (c is BrandTitleControl) continue;
            Font f = c.Font;
            if (f != null && !String.Equals(f.FontFamily.Name, "Consolas", StringComparison.OrdinalIgnoreCase))
            {
                FontStyle style = f.Style & ~FontStyle.Bold;
                if (!String.Equals(f.FontFamily.Name, targetFamily, StringComparison.OrdinalIgnoreCase) || f.Style != style)
                {
                    c.Font = GetCachedFont(targetFamily, f.Size, style);
                }
            }
            ApplyFontRecursive(c, targetFamily);
        }
    }

    // Auto-discover embedded locale.<name>.txt resources compiled into the executable.
    private static void LoadEmbeddedPacks(Dictionary<string, PackInfo> target)
    {
        try
        {
            Assembly asm = typeof(UiLang).Assembly;
            foreach (string resName in asm.GetManifestResourceNames())
            {
                if (!resName.StartsWith("locale.", StringComparison.OrdinalIgnoreCase)) continue;
                string fileName = resName.Substring("locale.".Length);
                if (fileName.EndsWith(".template.txt", StringComparison.OrdinalIgnoreCase)) continue;
                using (Stream stream = asm.GetManifestResourceStream(resName))
                {
                    if (stream == null) continue;
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
                        MergePack(target, ParsePack(reader, Path.GetFileNameWithoutExtension(fileName)));
                }
            }
        }
        catch { }
    }

    // Allow drop-in custom or override packs in %LocalAppData%\NitroHub\locales\*.txt without recompiling.
    private static void LoadExternalPacks(Dictionary<string, PackInfo> target)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NitroHub", "locales");
            if (!Directory.Exists(dir)) return;
            foreach (string path in Directory.GetFiles(dir, "*.txt"))
            {
                string fileName = Path.GetFileName(path);
                if (fileName.EndsWith(".template.txt", StringComparison.OrdinalIgnoreCase)) continue;
                using (StreamReader reader = new StreamReader(path, Encoding.UTF8, true))
                    MergePack(target, ParsePack(reader, Path.GetFileNameWithoutExtension(fileName)));
            }
        }
        catch { }
    }

    private static void MergePack(Dictionary<string, PackInfo> target, PackInfo incoming)
    {
        if (incoming == null || String.IsNullOrEmpty(incoming.DisplayName)) return;
        PackInfo existing;
        if (target.TryGetValue(incoming.DisplayName, out existing))
        {
            if (!String.IsNullOrEmpty(incoming.FontFamily)) existing.FontFamily = incoming.FontFamily;
            if (incoming.Order != Int32.MaxValue) existing.Order = incoming.Order;
            foreach (KeyValuePair<string, string> kv in incoming.Entries)
                existing.Entries[kv.Key] = kv.Value;
        }
        else
        {
            target[incoming.DisplayName] = incoming;
        }
    }

    private static PackInfo ParsePack(TextReader reader, string fallbackName)
    {
        string displayName = fallbackName;
        string fontFamily = null;
        int order = Int32.MaxValue;
        Dictionary<string, string> entries = new Dictionary<string, string>(StringComparer.Ordinal);

        string line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length > 0 && line[0] == '\uFEFF') line = line.Substring(1);
            string trimmedStart = line.TrimStart();
            if (trimmedStart.Length == 0) continue;

            if (trimmedStart[0] == '#')
            {
                string meta = trimmedStart.Substring(1).Trim();
                if (meta.StartsWith("DisplayName=", StringComparison.OrdinalIgnoreCase))
                {
                    string v = meta.Substring("DisplayName=".Length).Trim();
                    if (v.Length > 0) displayName = v;
                }
                else if (meta.StartsWith("Font=", StringComparison.OrdinalIgnoreCase))
                {
                    string v = meta.Substring("Font=".Length).Trim();
                    if (v.Length > 0) fontFamily = v;
                }
                else if (meta.StartsWith("Order=", StringComparison.OrdinalIgnoreCase))
                {
                    int parsedOrder;
                    if (Int32.TryParse(meta.Substring("Order=".Length).Trim(), out parsedOrder)) order = parsedOrder;
                }
                else if (meta.StartsWith("Template=", StringComparison.OrdinalIgnoreCase))
                {
                    if (String.Equals(meta.Substring("Template=".Length).Trim(), "true", StringComparison.OrdinalIgnoreCase))
                        return null;
                }
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = Unescape(line.Substring(0, eq));
            string val = Unescape(line.Substring(eq + 1));
            if (key.Length > 0 && val.Length > 0) entries[key] = val;
        }

        if (entries.Count == 0) return null;
        return new PackInfo
        {
            DisplayName = displayName,
            FontFamily = fontFamily,
            Order = order,
            Entries = entries
        };
    }

    private static string Unescape(string text)
    {
        if (text.IndexOf('\\') < 0) return text;
        StringBuilder sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                char next = text[i + 1];
                if (next == 'n') { sb.Append('\n'); i++; continue; }
                if (next == 'r') { sb.Append('\r'); i++; continue; }
                if (next == 't') { sb.Append('\t'); i++; continue; }
                if (next == '\\') { sb.Append('\\'); i++; continue; }
            }
            sb.Append(text[i]);
        }
        return sb.ToString();
    }
}

internal class RoundedButton : Button
{
    private bool isHovered;
    private bool isPressed;

    public RoundedButton()
    {
        FlatStyle = FlatStyle.Flat;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(UiPaint.ParentBack(this, SystemColors.Control));

        RectangleF bounds = new RectangleF(0.5F, 0.5F, Math.Max(0, Width - 1F), Math.Max(0, Height - 1F));
        bool square = Convert.ToString(Tag) == "navigation-square";
        using (GraphicsPath path = CreateButtonPath(bounds, square ? 0F : 9F))
        {
            Color fill = BackColor;
            if (!Enabled) fill = ControlPaint.Dark(fill, 0.08F);
            else if (isPressed && FlatAppearance.MouseDownBackColor != Color.Empty)
                fill = FlatAppearance.MouseDownBackColor;
            else if (isHovered && FlatAppearance.MouseOverBackColor != Color.Empty)
                fill = FlatAppearance.MouseOverBackColor;

            using (SolidBrush brush = new SolidBrush(fill))
                graphics.FillPath(brush, path);

            if (FlatAppearance.BorderSize > 0)
            {
                RectangleF borderBounds = bounds;
                float inset = FlatAppearance.BorderSize / 2F;
                borderBounds.Inflate(-inset, -inset);
                using (GraphicsPath borderPath = CreateButtonPath(borderBounds, square ? 0F : 9F))
                using (Pen borderPen = new Pen(FlatAppearance.BorderColor, FlatAppearance.BorderSize))
                    graphics.DrawPath(borderPen, borderPath);
            }
        }

        Color textColor = Enabled ? ForeColor : SystemColors.GrayText;
        TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
        TextFormatFlags.WordBreak | TextFormatFlags.PreserveGraphicsClipping;
        if (TextAlign == ContentAlignment.MiddleLeft || TextAlign == ContentAlignment.TopLeft || TextAlign == ContentAlignment.BottomLeft)
            flags |= TextFormatFlags.Left;
        else if (TextAlign == ContentAlignment.MiddleRight || TextAlign == ContentAlignment.TopRight || TextAlign == ContentAlignment.BottomRight)
            flags |= TextFormatFlags.Right;
        else
            flags |= TextFormatFlags.HorizontalCenter;
        if (TextAlign == ContentAlignment.TopLeft || TextAlign == ContentAlignment.TopCenter || TextAlign == ContentAlignment.TopRight)
            flags = (flags & ~TextFormatFlags.VerticalCenter) | TextFormatFlags.Top;
        else if (TextAlign == ContentAlignment.BottomLeft || TextAlign == ContentAlignment.BottomCenter || TextAlign == ContentAlignment.BottomRight)
            flags = (flags & ~TextFormatFlags.VerticalCenter) | TextFormatFlags.Bottom;
        TextRenderer.DrawText(graphics, Text, Font, Rectangle.Inflate(ClientRectangle, -8, -4), textColor, flags);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        isHovered = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        isHovered = false;
        isPressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) isPressed = true;
        Invalidate();
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        isPressed = false;
        Invalidate();
        base.OnMouseUp(e);
    }

    private static GraphicsPath CreateButtonPath(RectangleF bounds, float radius)
    {
        GraphicsPath path = new GraphicsPath();
        if (radius <= 0F)
        {
            path.AddRectangle(bounds);
            return path;
        }
        float diameter = radius * 2F;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180F, 90F);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270F, 90F);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0F, 90F);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90F, 90F);
        path.CloseFigure();
        return path;
    }
}

internal sealed class ToggleSwitch : Control
{
    private bool checkedState;
    private float thumbProgress;
    private readonly Timer animationTimer;
    public Color OnColor { get; set; }
    public Color OffColor { get; set; }
    public bool ShowStateText { get; set; }
    public Color StateTextColor { get; set; }
    public event EventHandler Toggled;

    public bool Checked
    {
        get { return checkedState; }
        set
        {
            if (checkedState == value) return;
            checkedState = value;
            if (IsHandleCreated) animationTimer.Start();
            else thumbProgress = checkedState ? 1F : 0F;
            Invalidate();
        }
    }

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Size = new Size(68, 30);
        OnColor = Color.FromArgb(210, 48, 66);
        OffColor = Color.FromArgb(198, 202, 207);
        StateTextColor = Color.FromArgb(183, 205, 228);
        thumbProgress = 0F;
        animationTimer = new Timer { Interval = 15 };
        animationTimer.Tick += delegate
        {
            float target = checkedState ? 1F : 0F;
            float remaining = target - thumbProgress;
            if (Math.Abs(remaining) < 0.035F)
            {
                thumbProgress = target;
                animationTimer.Stop();
            }
            else thumbProgress += remaining * 0.32F;
            Invalidate();
        };
        TabStop = true;
    }

    private static Color BlendColor(Color from, Color to, float t)
    {
        t = Math.Max(0F, Math.Min(1F, t));
        int r = (int)Math.Round(from.R + (to.R - from.R) * t);
        int g = (int)Math.Round(from.G + (to.G - from.G) * t);
        int b = (int)Math.Round(from.B + (to.B - from.B) * t);
        return Color.FromArgb(r, g, b);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        graphics.Clear(UiPaint.ParentBack(this, SystemColors.Control));

        int trackHeight = Math.Max(20, Math.Min(Height - 2, (int)Math.Round(Height * 0.84F)));
        int trackWidth = Math.Min(Width - 2, Math.Max(trackHeight + UiDpi.S(38), (int)Math.Round(trackHeight * 2.64F)));
        Rectangle track = new Rectangle((Width - trackWidth) / 2, (Height - trackHeight) / 2, trackWidth, trackHeight);

        Color trackColor = Enabled
            ? BlendColor(OffColor, OnColor, thumbProgress)
            : Color.FromArgb(85, 94, 103);

        using (GraphicsPath path = new GraphicsPath())
        using (SolidBrush trackBrush = new SolidBrush(trackColor))
        {
            path.AddArc(track.Left, track.Top, trackHeight, trackHeight, 90F, 180F);
            path.AddArc(track.Right - trackHeight, track.Top, trackHeight, trackHeight, 270F, 180F);
            path.CloseFigure();
            graphics.FillPath(trackBrush, path);
        }

        int pad = Math.Max(2, (int)Math.Round(trackHeight * 0.12F));
        int thumbSize = Math.Max(10, trackHeight - pad * 2);
        int thumbTravel = Math.Max(0, track.Width - thumbSize - pad * 2);
        int thumbX = track.Left + pad + (int)Math.Round(thumbTravel * thumbProgress);

        string family = Font != null ? Font.FontFamily.Name : "Segoe UI";
        float fontSize = Font != null ? Math.Max(7.0F, (float)Math.Round(Font.Size * 0.88F, 1)) : 8.0F;
        Font stateFont = UiLang.GetCachedFont(family, fontSize, FontStyle.Regular);
        TextFormatFlags textFlags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
        TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;

        float onAlpha = Math.Max(0F, Math.Min(1F, (thumbProgress - 0.35F) / 0.65F));
        if (onAlpha > 0.02F)
        {
            Color onTextBase = Enabled ? Color.White : Color.FromArgb(215, 222, 230);
            Color onTextColor = BlendColor(trackColor, onTextBase, onAlpha);
            Rectangle onRect = new Rectangle(
                track.Left + pad + 2,
                track.Top,
                Math.Max(1, track.Width - thumbSize - pad * 2 - 2),
                track.Height);
            TextRenderer.DrawText(graphics, "ON", stateFont, onRect, onTextColor, textFlags);
        }

        float offAlpha = Math.Max(0F, Math.Min(1F, (0.65F - thumbProgress) / 0.65F));
        if (offAlpha > 0.02F)
        {
            double lum = 0.299 * trackColor.R + 0.587 * trackColor.G + 0.114 * trackColor.B;
            Color offTextBase = !Enabled
                ? Color.FromArgb(205, 212, 220)
                : (lum > 155.0 ? Color.FromArgb(48, 58, 70) : Color.FromArgb(236, 241, 248));
            Color offTextColor = BlendColor(trackColor, offTextBase, offAlpha);
            Rectangle offRect = new Rectangle(
                track.Left + thumbSize + pad + 1,
                track.Top,
                Math.Max(1, track.Width - thumbSize - pad * 2 - 2),
                track.Height);
            TextRenderer.DrawText(graphics, "OFF", stateFont, offRect, offTextColor, textFlags);
        }

        Rectangle thumb = new Rectangle(thumbX, track.Top + pad, thumbSize, thumbSize);
        using (SolidBrush shadowBrush = new SolidBrush(Color.FromArgb(42, 0, 0, 0)))
            graphics.FillEllipse(shadowBrush, thumb.X, thumb.Y + 1, thumb.Width, thumb.Height);
        using (SolidBrush thumbBrush = new SolidBrush(Enabled ? Color.White : Color.FromArgb(220, 222, 224)))
            graphics.FillEllipse(thumbBrush, thumb);
    }

    protected override void OnClick(EventArgs e)
    {
        if (Enabled)
        {
            Checked = !Checked;
            EventHandler handler = Toggled;
            if (handler != null) handler(this, EventArgs.Empty);
        }
        base.OnClick(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && animationTimer != null) animationTimer.Dispose();
        base.Dispose(disposing);
    }
}

internal sealed class SegmentedControl : Control
{
    private string[] items = new string[0];
    private int selectedIndex;
    private float animatedIndex;
    private readonly Timer animationTimer;

    public Color TrackColor { get; set; }
    public Color ThumbColor { get; set; }
    public Color SelectedTextColor { get; set; }
    public Color UnselectedTextColor { get; set; }
    public event EventHandler SelectedIndexChanged;

    public string[] Items
    {
        get { return items; }
        set
        {
            items = value ?? new string[0];
            if (items.Length == 0) selectedIndex = -1;
            else if (selectedIndex < 0 || selectedIndex >= items.Length) selectedIndex = 0;
            animatedIndex = Math.Max(0, selectedIndex);
            Invalidate();
        }
    }

    public int SelectedIndex
    {
        get { return selectedIndex; }
        set
        {
            if (items.Length == 0) return;
            int clamped = Math.Max(0, Math.Min(items.Length - 1, value));
            if (selectedIndex == clamped) return;
            selectedIndex = clamped;
            if (IsHandleCreated) animationTimer.Start();
            else animatedIndex = selectedIndex;
            Invalidate();
        }
    }

    public SegmentedControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Size = new Size(356, 38);
        Font = new Font("Segoe UI Semibold", 8.5F);
        TrackColor = Color.FromArgb(43, 51, 61);
        ThumbColor = Color.FromArgb(210, 48, 66);
        SelectedTextColor = Color.White;
        UnselectedTextColor = Color.FromArgb(210, 219, 228);
        animationTimer = new Timer { Interval = 15 };
        animationTimer.Tick += delegate
        {
            float target = Math.Max(0, selectedIndex);
            float remaining = target - animatedIndex;
            if (Math.Abs(remaining) < 0.025F)
            {
                animatedIndex = target;
                animationTimer.Stop();
            }
            else animatedIndex += remaining * 0.34F;
            Invalidate();
        };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(UiPaint.ParentBack(this, SystemColors.Control));
        if (Width < 10 || Height < 10 || items == null || items.Length == 0) return;

        int alpha = Enabled ? 255 : 102;
        RectangleF trackBounds = new RectangleF(0.5F, 0.5F, Width - 1F, Height - 1F);
        using (GraphicsPath trackPath = CreateCapsulePath(trackBounds))
        using (SolidBrush trackBrush = new SolidBrush(Color.FromArgb(alpha, TrackColor)))
            g.FillPath(trackBrush, trackPath);

        float padding = 3F;
        float innerWidth = Math.Max(1F, Width - padding * 2F);
        float innerHeight = Math.Max(1F, Height - padding * 2F);
        float segmentWidth = innerWidth / items.Length;

        if (selectedIndex >= 0)
        {
            float thumbX = padding + animatedIndex * segmentWidth;
            RectangleF thumbBounds = new RectangleF(thumbX, padding, segmentWidth, innerHeight);
            using (GraphicsPath thumbPath = CreateCapsulePath(thumbBounds))
            using (SolidBrush thumbBrush = new SolidBrush(Color.FromArgb(alpha, ThumbColor)))
                g.FillPath(thumbBrush, thumbPath);
        }

        for (int i = 0; i < items.Length; i++)
        {
            Rectangle segRect = new Rectangle(
                (int)Math.Round(padding + i * segmentWidth),
                (int)Math.Round(padding),
                (int)Math.Round(segmentWidth),
                (int)Math.Round(innerHeight));
            bool isSelected = i == selectedIndex;
            Color textColor = Color.FromArgb(alpha, isSelected ? SelectedTextColor : UnselectedTextColor);
            TextRenderer.DrawText(g, items[i], Font, segRect, textColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (Enabled && e.Button == MouseButtons.Left && items != null && items.Length > 0)
        {
            int clicked = (int)(e.X * items.Length / (float)Math.Max(1, Width));
            clicked = Math.Max(0, Math.Min(items.Length - 1, clicked));
            if (clicked != selectedIndex)
            {
                SelectedIndex = clicked;
                EventHandler handler = SelectedIndexChanged;
                if (handler != null) handler(this, EventArgs.Empty);
            }
        }
        base.OnMouseDown(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    private static GraphicsPath CreateCapsulePath(RectangleF bounds)
    {
        GraphicsPath path = new GraphicsPath();
        float diameter = Math.Max(2F, Math.Min(bounds.Width, bounds.Height));
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 90F, 180F);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270F, 180F);
        path.CloseFigure();
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && animationTimer != null) animationTimer.Dispose();
        base.Dispose(disposing);
    }
}

internal static class UiPaint
{
    private const int WM_SETREDRAW = 0x000B;
    private const uint RDW_INVALIDATE = 0x0001;
    private const uint RDW_ERASE = 0x0004;
    private const uint RDW_ALLCHILDREN = 0x0080;
    private const uint RDW_UPDATENOW = 0x0100;
    private const uint RDW_FRAME = 0x0400;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, bool wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

    private static int redrawDepth;
    public static bool FadeRequested;

    public static void BeginRedrawBatch(Control c)
    {
        Form f = c as Form;
        if (f == null || !f.IsHandleCreated) return;
        if (redrawDepth++ > 0) return;
        FadeRequested = false;
        SendMessage(f.Handle, WM_SETREDRAW, false, IntPtr.Zero);
    }

    public static void EndRedrawBatch(Control c)
    {
        Form f = c as Form;
        if (f == null || !f.IsHandleCreated || redrawDepth == 0) return;
        if (--redrawDepth > 0) return;
        SendMessage(f.Handle, WM_SETREDRAW, true, IntPtr.Zero);
        // RDW_ALLCHILDREN | RDW_UPDATENOW repaints every child HWND synchronously in one pass
        // (Form.Update() only updates the top-level HWND and leaves child Labels/Panels to lag behind).
        RedrawWindow(f.Handle, IntPtr.Zero, IntPtr.Zero,
            RDW_ERASE | RDW_FRAME | RDW_INVALIDATE | RDW_ALLCHILDREN | RDW_UPDATENOW);
    }

    public static Color ParentBack(Control c, Color fallback)
    {
        Control p = c.Parent;
        while (p != null)
        {
            if (p.BackColor.A == 255) return p.BackColor;
            p = p.Parent;
        }
        return fallback;
    }
}

internal sealed class BufferedPanel : Panel
{
    public BufferedPanel()
    {
        DoubleBuffered = true;
    }
}

internal sealed class RoundedPanel : Panel
{
    public int Radius { get; set; }
    public Color BorderColor { get; set; }

    public RoundedPanel()
    {
        Radius = 12;
        BorderColor = Color.Empty;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnParentBackColorChanged(EventArgs e)
    {
        base.OnParentBackColorChanged(e);
        Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(UiPaint.ParentBack(this, SystemColors.Control));
        RectangleF bounds = new RectangleF(0.5F, 0.5F, Math.Max(0, Width - 1F), Math.Max(0, Height - 1F));
        using (GraphicsPath path = MakeRoundedPath(bounds, Radius))
        {
            using (SolidBrush brush = new SolidBrush(BackColor))
                e.Graphics.FillPath(brush, path);
            if (BorderColor != Color.Empty)
            {
                using (Pen pen = new Pen(BorderColor, 1F))
                    e.Graphics.DrawPath(pen, path);
            }
        }
    }

    private static GraphicsPath MakeRoundedPath(RectangleF bounds, float radius)
    {
        GraphicsPath path = new GraphicsPath();
        float diameter = Math.Min(radius * 2F, Math.Min(bounds.Width, bounds.Height));
        if (diameter <= 0F) { path.AddRectangle(bounds); return path; }
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180F, 90F);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270F, 90F);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0F, 90F);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90F, 90F);
        path.CloseFigure();
        return path;
    }
}

internal sealed class ModernScrollBar : Control
{
    private int maximum = 100;
    private int viewport = 100;
    private int scrollValue;
    private bool hover;
    private bool dragging;
    private int dragStartY;
    private int dragStartValue;

    public Color ThumbColor { get; set; }
    public Color ThumbHoverColor { get; set; }
    public event EventHandler ValueChanged;

    public int Maximum
    {
        get { return maximum; }
        set { maximum = Math.Max(0, value); if (scrollValue > maximum) scrollValue = maximum; UpdateVisibility(); Invalidate(); }
    }

    public int Viewport
    {
        get { return viewport; }
        set { viewport = Math.Max(1, value); UpdateVisibility(); Invalidate(); }
    }

    public int Value
    {
        get { return scrollValue; }
        set
        {
            int clamped = Math.Max(0, Math.Min(maximum, value));
            if (scrollValue == clamped) return;
            scrollValue = clamped;
            Invalidate();
            EventHandler handler = ValueChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }
    }

    public ModernScrollBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Width = 16;
        Cursor = Cursors.Hand;
        ThumbColor = Color.FromArgb(88, 100, 114);
        ThumbHoverColor = Color.FromArgb(128, 142, 160);
    }

    private void UpdateVisibility()
    {
        bool needScroll = maximum > 0;
        if (Visible != needScroll) Visible = needScroll;
    }

    private Rectangle GetThumbBounds()
    {
        int trackHeight = Math.Max(1, Height - 8);
        int total = maximum + viewport;
        int thumbHeight = Math.Max(32, (int)Math.Round(trackHeight * (viewport / (double)Math.Max(1, total))));
        if (thumbHeight > trackHeight) thumbHeight = trackHeight;
        int maxTop = Math.Max(0, trackHeight - thumbHeight);
        int top = maximum == 0 ? 4 : 4 + (int)Math.Round(maxTop * (scrollValue / (double)maximum));
        return new Rectangle(2, top, Math.Max(4, Width - 4), thumbHeight);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(UiPaint.ParentBack(this, BackColor));
        if (maximum <= 0) return;

        Rectangle thumb = GetThumbBounds();
        Color color = (dragging || hover) ? ThumbHoverColor : ThumbColor;
        using (GraphicsPath path = new GraphicsPath())
        using (SolidBrush brush = new SolidBrush(color))
        {
            float d = Math.Min(thumb.Width, thumb.Height);
            path.AddArc(thumb.X, thumb.Y, d, d, 180F, 90F);
            path.AddArc(thumb.Right - d, thumb.Y, d, d, 270F, 90F);
            path.AddArc(thumb.Right - d, thumb.Bottom - d, d, d, 0F, 90F);
            path.AddArc(thumb.X, thumb.Bottom - d, d, d, 90F, 90F);
            path.CloseFigure();
            e.Graphics.FillPath(brush, path);
        }
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && maximum > 0)
        {
            Rectangle thumb = GetThumbBounds();
            if (thumb.Contains(e.Location))
            {
                dragging = true;
                dragStartY = e.Y;
                dragStartValue = scrollValue;
            }
            else
            {
                int trackHeight = Math.Max(1, Height - 8 - thumb.Height);
                double ratio = Math.Max(0.0, Math.Min(1.0, (e.Y - 4 - thumb.Height / 2.0) / trackHeight));
                Value = (int)Math.Round(ratio * maximum);
            }
            Invalidate();
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (dragging && maximum > 0)
        {
            Rectangle thumb = GetThumbBounds();
            int trackHeight = Math.Max(1, Height - 8 - thumb.Height);
            int deltaY = e.Y - dragStartY;
            Value = dragStartValue + (int)Math.Round(deltaY * (maximum / (double)trackHeight));
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (dragging) { dragging = false; Invalidate(); }
        base.OnMouseUp(e);
    }
}

internal sealed class HighQualityLogoPictureBox : PictureBox
{
    protected override void OnPaint(PaintEventArgs e)
    {
        if (Image == null) return;
        e.Graphics.CompositingQuality = CompositingQuality.HighQuality;
        e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        float scale = Math.Min(Width / (float)Image.Width, Height / (float)Image.Height);
        float width = Image.Width * scale;
        float height = Image.Height * scale;
        RectangleF target = new RectangleF((Width - width) / 2F, (Height - height) / 2F, width, height);
        e.Graphics.DrawImage(Image, target);
    }
}

internal sealed class BrandTitleControl : Control
{
    private Color nitroColor = Color.White;
    public Color NitroColor
    {
        get { return nitroColor; }
        set { nitroColor = value; Invalidate(); }
    }

    public BrandTitleControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using (StringFormat format = (StringFormat)StringFormat.GenericTypographic.Clone())
        using (SolidBrush white = new SolidBrush(nitroColor))
        using (Font nitroFont = new Font(Font.FontFamily, Font.Size, FontStyle.Bold | FontStyle.Italic))
        using (Font hubFont = new Font(Font.FontFamily, Font.Size, FontStyle.Bold | FontStyle.Italic))
        {
            SizeF nitroSize = e.Graphics.MeasureString("Nitro", nitroFont, Int32.MaxValue, format);
            float hubX = nitroSize.Width + 4F;
            SizeF hubSize = e.Graphics.MeasureString("Hub", hubFont, Int32.MaxValue, format);
            e.Graphics.DrawString("Nitro", nitroFont, white, new PointF(0F, 0F), format);
            RectangleF gradientBounds = new RectangleF(hubX, 0F, Math.Max(1F, hubSize.Width), Math.Max(1, Font.Height));
            using (LinearGradientBrush hubGradient = new LinearGradientBrush(gradientBounds,
                Color.FromArgb(230, 57, 70), Color.FromArgb(142, 35, 49), LinearGradientMode.Horizontal))
                e.Graphics.DrawString("Hub", hubFont, hubGradient, new PointF(hubX, 0F), format);
        }
    }
}

internal sealed class HeaderGlowLine : Control
{
    public HeaderGlowLine()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(UiPaint.ParentBack(this, Color.Transparent));
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (Width < 2 || Height < 2) return;

        Color headerColor = UiPaint.ParentBack(this, BackColor);
        using (GraphicsPath taperedGlow = new GraphicsPath())
        {
            float tailHalfHeight = 0.6F;
            taperedGlow.StartFigure();
            taperedGlow.AddBezier(0F, 0F, Width * 0.28F, 0F, Width * 0.68F, Height * 0.48F,
                Width, Height / 2F - tailHalfHeight);
            taperedGlow.AddLine(Width, Height / 2F + tailHalfHeight, 0F, (float)Height);
            taperedGlow.CloseFigure();
            using (LinearGradientBrush glow = new LinearGradientBrush(ClientRectangle,
                Color.FromArgb(235, 230, 57, 70), headerColor, LinearGradientMode.Horizontal))
                e.Graphics.FillPath(glow, taperedGlow);
        }
    }
}

internal sealed class VersionBadgePanel : Panel
{
    public VersionBadgePanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Width < 2 || Height < 2) return;
        using (GraphicsPath path = CreatePath())
        {
            Region oldRegion = Region;
            Region = new Region(path);
            if (oldRegion != null) oldRegion.Dispose();
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(UiPaint.ParentBack(this, SystemColors.Control));
        using (GraphicsPath path = CreatePath())
        using (SolidBrush brush = new SolidBrush(BackColor))
            e.Graphics.FillPath(brush, path);
    }

    private GraphicsPath CreatePath()
    {
        GraphicsPath path = new GraphicsPath();
        float radius = Math.Min(9F, Height / 2F);
        float diameter = radius * 2F;
        path.AddArc(0F, 0F, diameter, diameter, 180F, 90F);
        path.AddArc(Width - diameter, 0F, diameter, diameter, 270F, 90F);
        path.AddArc(Width - diameter, Height - diameter, diameter, diameter, 0F, 90F);
        path.AddArc(0F, Height - diameter, diameter, diameter, 90F, 90F);
        path.CloseFigure();
        return path;
    }
}

internal sealed class KeyboardLightingPreview : Control
{
    private sealed class PreviewKeyInfo : IDisposable
    {
        public RectangleF Bounds;
        public GraphicsPath GlowPath;
        public GraphicsPath KeyPath;
        public string Caption;
        public int Row;
        public int Column;
        public int Count;
        public float NormalizedX;
        public float NormalizedY;

        public void Dispose()
        {
            if (GlowPath != null) GlowPath.Dispose();
            if (KeyPath != null) KeyPath.Dispose();
        }
    }

    private static readonly string[][] PreviewRows = new string[][]
    {
        new string[] { "Esc", "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12" },
        new string[] { "~", "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "-", "+", "⌫" },
        new string[] { "Tab", "Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P", "[", "]", "\\" },
        new string[] { "Caps", "A", "S", "D", "F", "G", "H", "J", "K", "L", ";", "'", "Enter" },
        new string[] { "Shift", "Z", "X", "C", "V", "B", "N", "M", ",", ".", "/", "Shift" },
        new string[] { "Ctrl", "Fn", "Win", "Alt", "Space", "Alt", "←", "↓", "→" }
    };

    private readonly Timer animationTimer;
    private readonly List<PreviewKeyInfo> previewKeys = new List<PreviewKeyInfo>();
    private Rectangle keyboardBounds;
    private GraphicsPath chassisPath;
    private Bitmap staticLayer;
    private Bitmap keyBaseLayer;
    private Bitmap keyDetailsLayer;
    private readonly SolidBrush glowBrush = new SolidBrush(Color.Transparent);
    private readonly SolidBrush chassisBrush = new SolidBrush(Color.FromArgb(14, 18, 24));
    private readonly SolidBrush keyBrush = new SolidBrush(Color.FromArgb(255, 17, 22, 29));
    private readonly SolidBrush textBrush = new SolidBrush(Color.FromArgb(155, 184, 195, 207));
    private readonly SolidBrush modeBrush = new SolidBrush(Color.FromArgb(183, 205, 228));
    private readonly Pen keyOutline = new Pen(Color.FromArgb(62, 73, 86), 0.8F);
    private readonly Pen chassisOutline = new Pen(Color.FromArgb(75, 86, 100), 1F);
    private readonly Pen outerOutline = new Pen(Color.FromArgb(58, 69, 82), 1F);
    private readonly Font keyFont = new Font("Segoe UI", 6.5F);
    private readonly Font modeFont = new Font("Segoe UI Semibold", 7.5F);
    private readonly StringFormat keyFormat = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
    private string effect = "Neon";
    private Color selectedColor = Color.FromArgb(210, 48, 66);
    private int brightness = 70;
    private int speed = 5;
    private float phase;

    public KeyboardLightingPreview()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(26, 32, 40);
        animationTimer = new Timer { Interval = 30 };
        animationTimer.Tick += delegate
        {
            Form hostForm = FindForm();
            bool isHostVisible = hostForm != null && hostForm.Visible && hostForm.WindowState != FormWindowState.Minimized;
            if (speed > 0 && Visible && isHostVisible && IsHandleCreated)
            {
                float frameScale = animationTimer.Interval / 90F;
                phase += (effect == "Neon" ? 0.0015F + speed * 0.0025F : 0.004F + speed * 0.012F) * frameScale;
                if (phase >= 1F) phase -= 1F;
                Invalidate();
            }
        };
        animationTimer.Start();
    }

    public void ApplyTheme(bool dark)
    {
        BackColor = dark ? Color.FromArgb(26, 32, 40) : Color.FromArgb(234, 240, 247);
        modeBrush.Color = dark ? Color.FromArgb(183, 205, 228) : Color.FromArgb(88, 104, 122);
        outerOutline.Color = dark ? Color.FromArgb(58, 69, 82) : Color.FromArgb(198, 209, 220);
        BuildStaticLayer();
        Invalidate();
    }

    public void SetPreview(string selectedEffect, Color color, int level, int effectSpeed)
    {
        string previousEffect = effect;
        effect = selectedEffect ?? "Neon";
        selectedColor = color;
        brightness = Math.Max(0, Math.Min(100, level));
        speed = Math.Max(0, Math.Min(9, effectSpeed));
        if (!String.Equals(previousEffect, effect, StringComparison.Ordinal)) BuildStaticLayer();
        Invalidate();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        RebuildLayout();
    }

    private void RebuildLayout()
    {
        foreach (PreviewKeyInfo oldKey in previewKeys) oldKey.Dispose();
        previewKeys.Clear();
        if (chassisPath != null) { chassisPath.Dispose(); chassisPath = null; }
        if (staticLayer != null) { staticLayer.Dispose(); staticLayer = null; }
        if (keyBaseLayer != null) { keyBaseLayer.Dispose(); keyBaseLayer = null; }
        if (keyDetailsLayer != null) { keyDetailsLayer.Dispose(); keyDetailsLayer = null; }
        if (Width < 120 || Height < 74) return;

        keyboardBounds = new Rectangle(18, 24, Math.Max(120, Width - 36), Math.Max(74, Height - 48));
        chassisPath = RoundedRect(keyboardBounds, 10);
        float keyGap = 3F;
        float left = keyboardBounds.Left + 9F;
        float top = keyboardBounds.Top + 10F;
        float rowHeight = Math.Max(12F, (keyboardBounds.Height - 20F - keyGap * (PreviewRows.Length - 1)) / PreviewRows.Length);
        for (int row = 0; row < PreviewRows.Length; row++)
        {
            string[] rowKeys = PreviewRows[row];
            float totalUnits = 0F;
            foreach (string name in rowKeys)
                totalUnits += GetKeyUnits(name);
            float unitWidth = (keyboardBounds.Width - 18F - keyGap * (rowKeys.Length - 1)) / totalUnits;
            float x = left;
            float y = top + row * (rowHeight + keyGap);
            for (int column = 0; column < rowKeys.Length; column++)
            {
                string name = rowKeys[column];
                float keyWidth = unitWidth * GetKeyUnits(name);
                RectangleF bounds = new RectangleF(x, y, keyWidth, rowHeight);
                Rectangle glowRect = Rectangle.Round(new RectangleF(bounds.X - 2F, bounds.Y - 2F, bounds.Width + 4F, bounds.Height + 4F));
                PreviewKeyInfo info = new PreviewKeyInfo
                {
                    Bounds = bounds,
                    GlowPath = RoundedRect(glowRect, 3),
                    KeyPath = RoundedRect(Rectangle.Round(bounds), 3),
                    Caption = name,
                    Row = row,
                    Column = column,
                    Count = rowKeys.Length,
                    NormalizedX = (column + 0.5F) / rowKeys.Length,
                    NormalizedY = (row + 0.5F) / PreviewRows.Length
                };
                previewKeys.Add(info);
                x += keyWidth + keyGap;
            }
        }
        BuildStaticLayer();
    }

    private void BuildStaticLayer()
    {
        if (Width < 1 || Height < 1 || chassisPath == null) return;
        if (staticLayer != null) { staticLayer.Dispose(); staticLayer = null; }
        if (keyBaseLayer != null) { keyBaseLayer.Dispose(); keyBaseLayer = null; }
        if (keyDetailsLayer != null) { keyDetailsLayer.Dispose(); keyDetailsLayer = null; }
        staticLayer = new Bitmap(Width, Height);
        using (Graphics g = Graphics.FromImage(staticLayer))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            g.DrawRectangle(outerOutline, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
            string previewMode = effect == "Neon" ? "NEON  ·  FULL-KEYBOARD COLOR CYCLE" :
                effect == "Wave" ? "WAVE  ·  RAINBOW SWEEP" :
                effect == "Shifting" ? "SHIFTING  ·  COLOR SWEEP" : "ZOOM  ·  RADIAL PULSE";
            g.DrawString(previewMode, modeFont, modeBrush, 12F, 5F);
            g.FillPath(chassisBrush, chassisPath);
            g.DrawPath(chassisOutline, chassisPath);
        }
        keyBaseLayer = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        keyDetailsLayer = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(keyBaseLayer))
        using (Graphics details = Graphics.FromImage(keyDetailsLayer))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            details.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            details.Clear(Color.Transparent);
            foreach (PreviewKeyInfo key in previewKeys)
            {
                g.FillPath(keyBrush, key.KeyPath);
                details.DrawPath(keyOutline, key.KeyPath);
                if (key.Bounds.Width > 16F)
                    details.DrawString(key.Caption, keyFont, textBrush, key.Bounds, keyFormat);
            }
        }
    }

    private static float GetKeyUnits(string name)
    {
        if (name == "Space") return 4.7F;
        return name == "Shift" || name == "Enter" || name == "Caps" || name == "Tab" || name == "⌫" ? 1.55F : 1F;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (staticLayer == null || keyBaseLayer == null || keyDetailsLayer == null) return;
        g.DrawImageUnscaled(staticLayer, 0, 0);
        float brightnessScale = brightness / 100F;
        foreach (PreviewKeyInfo key in previewKeys)
        {
            float keyX = key.NormalizedX;
            float wave = (float)Math.Cos((keyX * 2F - phase) * Math.PI * 2F) * 0.5F + 0.5F;
            float intensity;
            if (effect == "Neon") intensity = 0.82F;
            else if (effect == "Wave") intensity = 0.22F + wave * 0.78F;
            else if (effect == "Shifting")
            {
                float distance = Math.Abs(keyX - phase);
                distance = Math.Min(distance, 1F - distance);
                intensity = Math.Max(0F, 1F - distance / 0.19F);
            }
            else
            {
                float dx = keyX - 0.5F;
                float dy = key.NormalizedY - 0.5F;
                float distance = (float)Math.Sqrt(dx * dx + dy * dy) / 0.7072F;
                float pulse = 0.08F + phase * 0.98F;
                intensity = Math.Max(0F, 1F - Math.Abs(distance - pulse) / 0.20F);
            }
            int alpha = Math.Max(0, Math.Min(235, (int)(brightnessScale * intensity * 230F)));
            if (alpha > 0)
            {
                glowBrush.Color = Color.FromArgb(alpha, GetKeyColor(key.Row, key.Column, key.Count, wave, keyX));
                g.FillPath(glowBrush, key.GlowPath);
            }
        }
        g.DrawImageUnscaled(keyBaseLayer, 0, 0);
        g.DrawImageUnscaled(keyDetailsLayer, 0, 0);
    }

    private Color GetKeyColor(int row, int col, int count, float wave, float keyX)
    {
        if (effect == "Neon") return ColorFromHue(phase);
        if (effect == "Wave") return ColorFromHue(keyX + phase);
        if (effect == "Shifting" || effect == "Zoom") return selectedColor;
        return Color.FromArgb(210, 48, 66);
    }

    private static Color ColorFromHue(float hue)
    {
        float h = (hue - (float)Math.Floor(hue)) * 6F;
        int sector = (int)Math.Floor(h);
        float fraction = h - sector;
        float q = 1F - fraction;
        switch (sector % 6)
        {
            case 0: return Color.FromArgb(0, 255, (int)(fraction * 255F));
            case 1: return Color.FromArgb(0, (int)(q * 255F), 255);
            case 2: return Color.FromArgb((int)(fraction * 255F), 0, 255);
            case 3: return Color.FromArgb(255, 0, (int)(q * 255F));
            case 4: return Color.FromArgb(255, (int)(fraction * 255F), 0);
            default: return Color.FromArgb((int)(q * 255F), 255, 0);
        }
    }

    private static GraphicsPath RoundedRect(Rectangle rect, int radius)
    {
        GraphicsPath path = new GraphicsPath();
        int d = Math.Max(2, radius * 2);
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (animationTimer != null) animationTimer.Dispose();
            if (staticLayer != null) staticLayer.Dispose();
            if (keyBaseLayer != null) keyBaseLayer.Dispose();
            if (keyDetailsLayer != null) keyDetailsLayer.Dispose();
            foreach (PreviewKeyInfo key in previewKeys) key.Dispose();
            previewKeys.Clear();
            if (chassisPath != null) chassisPath.Dispose();
            glowBrush.Dispose(); chassisBrush.Dispose(); keyBrush.Dispose(); textBrush.Dispose(); modeBrush.Dispose();
            keyOutline.Dispose(); chassisOutline.Dispose(); outerOutline.Dispose();
            keyFont.Dispose(); modeFont.Dispose(); keyFormat.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class KeyboardEffectProfile
{
    public readonly string Name;
    public readonly int FirmwareMode;
    public readonly bool SupportsCustomColor;

    public KeyboardEffectProfile(string name, int firmwareMode, bool supportsCustomColor)
    {
        Name = name;
        FirmwareMode = firmwareMode;
        SupportsCustomColor = supportsCustomColor;
    }
    public override string ToString() { return Name; }
}

internal sealed class KeyboardCapabilityReport
{
    public string Model;
    public bool InterfaceFound;
    public bool CanSetEffect;
    public KeyboardEffectProfile[] Effects;
    public string Status;
}

internal sealed class HeaderStatusDot : Control
{
    public HeaderStatusDot()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(UiPaint.ParentBack(this, Color.Transparent));
        using (SolidBrush brush = new SolidBrush(Color.FromArgb(72, 211, 121)))
            e.Graphics.FillEllipse(brush, 1, 1, Math.Max(1, Width - 2), Math.Max(1, Height - 2));
    }
}

internal sealed class CopyIconButton : RoundedButton
{
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        Color iconColor = Enabled ? ForeColor : SystemColors.GrayText;
        int centerX = Width / 2;
        int centerY = Height / 2;
        Rectangle backSheet = new Rectangle(centerX - 5, centerY - 6, 8, 10);
        Rectangle frontSheet = new Rectangle(centerX - 1, centerY - 3, 8, 10);
        using (Pen pen = new Pen(iconColor, 1.1F))
        using (SolidBrush fill = new SolidBrush(BackColor))
        {
            e.Graphics.DrawRectangle(pen, backSheet);
            e.Graphics.FillRectangle(fill, frontSheet);
            e.Graphics.DrawRectangle(pen, frontSheet);
            e.Graphics.DrawLine(pen, frontSheet.Left + 2, frontSheet.Top + 4, frontSheet.Right - 2, frontSheet.Top + 4);
            e.Graphics.DrawLine(pen, frontSheet.Left + 2, frontSheet.Top + 7, frontSheet.Right - 2, frontSheet.Top + 7);
        }
    }
}

internal sealed class ChargeLimitForm : Form
{
    private const int HistoryCapacity = 1800;
    private const int ActionButtonHeight = 36;
    private const float ActionButtonFontSize = 9F;
    private readonly Label stateTitle;
    private readonly Label stateDetail;
    private Panel chargeStatusAccent;
    private Label modelLabel;
    private FlowLayoutPanel overviewIdentityRows;
    private FlowLayoutPanel overviewHardwareRows;
    private Label overviewSerialLabel;
    private Button overviewSerialCopyButton;
    private Panel supportPagePanel;
    private Label supportModelLabel;
    private Label supportSerialLabel;
    private Button supportCopyButton;
    private string supportModelValue;
    private string supportSkuValue;
    private string supportSerialValue;
    private HardwareSnapshot displayedHardwareSnapshot;
    private Timer resumeReapplyTimer;
    private string lastTrayStatusLine;
    private volatile bool batteryReportBusy;
    private readonly Button enableButton;
    private readonly Button disableButton;
    private Label cpuStatusValueLabel;
    private Label gpuStatusValueLabel;
    private Label ramStatusValueLabel;
    private Label systemStatusValueLabel;
    private Label cpuFanStatusValueLabel;
    private Label gpuFanStatusValueLabel;
    private Label telemetryDetailsLabel;
    private Image headerLogoImage;
    private PictureBox laptopProductPhoto;
    private Label laptopPhotoStatus;
    private bool laptopPhotoUsingFallback;
    private readonly Timer telemetryTimer;
    private readonly ToolTip toolTips = new ToolTip();
    private readonly List<float> cpuTemperatureHistory = new List<float>();
    private readonly List<float> gpuTemperatureHistory = new List<float>();
    private readonly List<float> systemTemperatureHistory = new List<float>();
    private readonly List<float> cpuUsageHistory = new List<float>();
    private readonly List<float> gpuUsageHistory = new List<float>();
    private Panel temperatureChart;
    private Panel usageChart;
    private int tempChartHoverIndex = -1;
    private int usageChartHoverIndex = -1;
    private Panel[] pageList;
    private PerformanceCounter cpuLoadCounter;
    private int cpuLoadWarmupTick;
    private int appliedCustomCpuSpeed = -1;
    private int appliedCustomGpuSpeed = -1;
    private PerformanceCounter memoryLoadCounter;
    private Panel appHeader;
    private Panel navigationPanel;
    private Panel pageContainer;
    private readonly Button[] navigationButtons = new RoundedButton[8];
    private int currentPageIndex;
    private Label cpuFanValueLabel;
    private Label gpuFanValueLabel;
    private Button autoFanButton;
    private Button maxFanButton;
    private readonly Button[] powerPlanButtons = new RoundedButton[6];
    private string acPowerPlanScheme;
    private string batteryPowerPlanScheme;
    private bool showingBatteryPlans;
    private bool hasPowerSourceState;
    private PowerLineStatus lastPowerLineStatus;
    private bool isDarkTheme = true;
    private readonly Label calibrationStateLabel;
    private readonly ToggleSwitch calibrationToggle;
    private bool? originalChargeLimitState;
    private readonly ToggleSwitch keyboardTimeoutToggleButton;
    private readonly Label keyboardTimeoutStateLabel;
    private Panel keyboardTimeoutAccent;
    private bool keyboardTimeoutEnabled;
    private ToggleSwitch usbChargeToggle;
    private SegmentedControl usbChargeSegment;
    private int lastUsbChargeOptionIndex = 1;
    private bool usbChargeBusy;
    private int pendingUsbChargeIndex = -1;
    private bool usbChargeStateVerified;
    private bool updatingUsbChargeUi;
    private ToggleSwitch bluelightToggle;
    private CheckBox bluelightKeepCheckBox;
    private SegmentedControl bluelightSegment;
    private int lastBluelightOptionIndex = 0;
    private bool updatingBluelightUi;
    private Timer bluelightReapplyTimer;
    private int bluelightLevel;
    private static readonly Color StatusGood = Color.FromArgb(111, 207, 151);
    private static readonly Color StatusBad = Color.FromArgb(235, 157, 157);
    private Color lightingColor = Color.Red;
    private Button lightingColorButton;
    private Panel lightingColorPreview;
    private KeyboardLightingPreview keyboardLightingPreview;
    private Label lightingStatusLabel;
    private Label keyboardCapabilityLabel;
    private bool keyboardEffectControlAvailable;
    private Label batteryHealthSummaryLabel;
    private Label lightingColorStatusLabel;
    private Label brightnessTitleLabel;
    private Label effectSpeedTitleLabel;
    private Label brightnessValueLabel;
    private Label speedValueLabel;
    private ComboBox effectCombo;
    private Button applyKeyboardButton;
    private TrackBar brightnessTrack;
    private TrackBar speedTrack;
    private TrackBar cpuFanTrack;
    private TrackBar gpuFanTrack;
    private bool fanControlChanged;
    private bool useFahrenheit;
    private NotifyIcon trayIcon;
    private ContextMenuStrip trayMenu;
    private bool allowExit;
    private readonly bool startMinimizedToTray;
    private bool initialHiddenApplied;
    private byte currentFanMode = 1;
    private bool startupInitialized;
    private static readonly string[] PowerPlanSchemes = new string[] {
        "381b4222-f694-41f0-9685-ff5bb260df2e",
        "a1841308-3541-4fab-bc81-f71556f20b4a",
        "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"
    };
    private static readonly Color Teal = Color.FromArgb(210, 48, 66);
    private Color HintBlue { get { return isDarkTheme ? Color.FromArgb(183, 205, 228) : Color.FromArgb(88, 104, 122); } }
    private static readonly Color LightError = Color.FromArgb(183, 205, 228);
    private static readonly Color LightSuccess = Color.FromArgb(183, 205, 228);
    private static readonly Color ProtectionGreen = Color.FromArgb(112, 218, 145);
    private Color SharedHeadingColor { get { return isDarkTheme ? Color.FromArgb(126, 166, 214) : Color.FromArgb(44, 92, 150); } }
    private Color MonitorCyan { get { return isDarkTheme ? Color.FromArgb(0, 215, 230) : Color.FromArgb(0, 118, 138); } }
    private static readonly Color Muted = Color.FromArgb(94, 110, 126);

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            // WS_EX_COMPOSITED: double-buffer the form and all child HWNDs bottom-to-top so DWM
            // never presents an intermediate frame where parents switched theme before child labels/controls.
            cp.ExStyle |= 0x02000000;
            return cp;
        }
    }

    public ChargeLimitForm() : this(false) { }

    public ChargeLimitForm(bool startMinimized)
    {
        startMinimizedToTray = startMinimized;
        AutoScaleMode = AutoScaleMode.None; // layout is scaled explicitly by ApplyDpiLayout()
        Text = "NitroHub";
        ShowIcon = true;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1028, 760);
        AutoScroll = false;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = true;
        MaximizeBox = true;
        BackColor = Color.FromArgb(21, 26, 32);
        Font = new Font("Segoe UI", 9F);

        appHeader = new Panel { Location = new Point(0, 0), Size = new Size(1028, 116), BackColor = Color.FromArgb(24, 47, 70) };
        Controls.Add(appHeader);

        navigationPanel = new Panel { Location = new Point(14, 145), Size = new Size(125, 360), BackColor = BackColor };
        pageContainer = new Panel { Location = new Point(154, 120), Size = new Size(860, 620), BackColor = BackColor };
        string[] pageNames = new string[] { "Overview", "Monitor", "Battery", "Tuning", "Keyboard", "Support", "Others", "Settings" };
        string[] pageTips = new string[] {
            "Computer identification and hardware configuration.", "Live sensor readings and temperature history.",
            "Charge protection status and toggle.", "Control cooling and Windows power plans.",
            "Choose a firmware-supported keyboard effect.",
            "Find Acer documentation and graphics drivers.", "Device power and display features.",
            "Application preferences, shortcuts, updates, and troubleshooting."
        };
        Panel[] pages = new Panel[pageNames.Length];
        for (int i = 0; i < pageNames.Length; i++)
        {
            int pageIndex = i;
            int buttonWidth = 125;
            navigationButtons[i] = new RoundedButton
            {
                Text = pageNames[i], Location = new Point(0, i * 46), Size = new Size(buttonWidth, 38),
                FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI Semibold", 9F),
                UseVisualStyleBackColor = false, Tag = "navigation", TabStop = false, TextAlign = ContentAlignment.MiddleLeft
            };
            navigationButtons[i].FlatAppearance.BorderSize = 0;
            navigationButtons[i].Click += delegate
            {
                if (pageIndex == currentPageIndex && pages[pageIndex].Visible) return;
                currentPageIndex = pageIndex;
                // Show the new page first, then hide the others, with layout paused: one repaint, no forced full-tree invalidation.
                pageContainer.SuspendLayout();
                pages[pageIndex].BringToFront();
                pages[pageIndex].Visible = true;
                for (int page = 0; page < pages.Length; page++)
                    if (page != pageIndex && pages[page].Visible) pages[page].Visible = false;
                pageContainer.ResumeLayout(true);
                UpdateNavigationStyles();
            };
            toolTips.SetToolTip(navigationButtons[i], pageTips[i]);
            navigationPanel.Controls.Add(navigationButtons[i]);
            pages[i] = new BufferedPanel { Dock = DockStyle.Fill, BackColor = BackColor, Visible = i == 0 };
            pageContainer.Controls.Add(pages[i]);
        }
        pageList = pages;
        currentPageIndex = 0;
        UpdateNavigationStyles();
        Controls.Add(pageContainer);
        Controls.Add(navigationPanel);
        Panel overviewPage = pages[0];
        Panel monitorPage = pages[1];
        Panel batteryPage = pages[2];
        Panel tuningPage = pages[3];
        Panel coolingPage = tuningPage;
        Panel performancePage = tuningPage;
        Panel lightingPage = pages[4];
        Panel supportPage = pages[5];
        Panel featuresPage = pages[6];
        Panel settingsPage = pages[7];

        headerLogoImage = LoadHeaderLogo();
        if (headerLogoImage != null)
            appHeader.Controls.Add(new HighQualityLogoPictureBox
        {
            Image = headerLogoImage, Location = new Point(38, 24), Size = new Size(76, 76),
            BackColor = Color.Transparent
        });
        appHeader.Controls.Add(new BrandTitleControl
        {
            Font = new Font("Segoe UI", 23F), Location = new Point(122, 18), Size = new Size(190, 40)
        });
        VersionBadgePanel versionBadge = new VersionBadgePanel
        {
            Location = new Point(287, 36), Size = new Size(56, 20),
            BackColor = Color.FromArgb(34, 41, 49), BorderStyle = BorderStyle.None
        };
        versionBadge.Controls.Add(new Label
        {
            Text = GetApplicationVersion(), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI Semibold", 7.5F), ForeColor = Color.FromArgb(166, 179, 192),
            BackColor = Color.Transparent
        });
        appHeader.Controls.Add(versionBadge);
        appHeader.Controls.Add(new HeaderStatusDot
        {
            Location = new Point(124, 83), Size = new Size(6, 6)
        });
        appHeader.Controls.Add(new Label
        {
            Text = "Your Nitro controls, all in one place.", Font = new Font("Segoe UI", 8.5F, FontStyle.Italic),
            ForeColor = Color.FromArgb(166, 179, 192), Location = new Point(136, 74), Size = new Size(380, 22),
            TextAlign = ContentAlignment.MiddleLeft
        });
        appHeader.Controls.Add(new HeaderGlowLine
        {
            Location = new Point(122, 64), Size = new Size(190, 5)
        });
        versionBadge.BringToFront();
        overviewPage.Controls.Add(new Label
        {
            Text = "DEVICE INFORMATION", Font = new Font("Segoe UI Semibold", 18F),
            ForeColor = Color.FromArgb(39, 57, 74), Location = new Point(20, 18), Size = new Size(770, 34), Tag = "section-heading"
        });
        overviewPage.Controls.Add(new Label
        {
            Text = "Hardware details reported by Windows.", Font = new Font("Segoe UI", 9.5F), ForeColor = Muted,
            Location = new Point(22, 62), Size = new Size(770, 22)
        });
        Panel identityCard = new Panel
        {
            Location = new Point(18, 94), Size = new Size(396, 500),
            BackColor = Color.White, BorderStyle = BorderStyle.None, Tag = "card"
        };
        Panel hardwareCard = new Panel
        {
            Location = new Point(432, 94), Size = new Size(396, 500),
            BackColor = Color.White, BorderStyle = BorderStyle.None, Tag = "card"
        };
        overviewPage.Controls.AddRange(new Control[] { identityCard, hardwareCard });
        identityCard.Controls.Add(new Label
        {
            Text = "LAPTOP", Font = new Font("Segoe UI Semibold", 9F), ForeColor = Teal,
            Location = new Point(18, 16), Size = new Size(350, 22), Tag = "section-heading"
        });
        FlowLayoutPanel identityRows = CreateOverviewRows(identityCard);
        overviewIdentityRows = identityRows;
        identityRows.Location = new Point(18, 232);
        identityRows.Size = new Size(354, 250);
        identityCard.Controls.Add(new Label
        {
            Text = "PRODUCT IMAGE", Font = new Font("Segoe UI", 7.5F),
            ForeColor = Muted, Location = new Point(18, 43), Size = new Size(350, 15)
        });
        laptopProductPhoto = new PictureBox
        {
            Location = new Point(18, 64), Size = new Size(354, 154),
            SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent, Tag = "card"
        };
        identityCard.Controls.Add(laptopProductPhoto);
        laptopPhotoStatus = new Label
        {
            Text = "Loading product photo...", Location = laptopProductPhoto.Location, Size = laptopProductPhoto.Size,
            TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.FromArgb(183, 205, 228),
            BackColor = Color.Transparent, Font = new Font("Segoe UI", 9F), Tag = "card"
        };
        identityCard.Controls.Add(laptopPhotoStatus);
        laptopPhotoStatus.BringToFront();
        laptopProductPhoto.LoadCompleted += delegate(object sender, System.ComponentModel.AsyncCompletedEventArgs e)
        {
            if (e.Error != null)
            {
                if (!laptopPhotoUsingFallback)
                    StartLaptopPhotoDownload(GetFallbackLaptopPhotoUrl(), true);
                else
                    laptopPhotoStatus.Text = "Acer product photo could not be loaded.";
                return;
            }
            laptopPhotoStatus.Visible = false;
            if (!e.Cancelled) SaveLaptopPhotoCache(laptopPhotoUsingFallback);
        };
        HardwareSnapshot initialHw = HardwareSnapshot.LoadFromDisk();
        displayedHardwareSnapshot = initialHw;
        modelLabel = AddOverviewInfoRow(identityRows, "Model", initialHw.Model, true);
        AddOverviewInfoRow(identityRows, "Serial number", initialHw.Serial, false, true);
        AddOverviewInfoRow(identityRows, "BIOS version", initialHw.Bios, false);
        AddOverviewInfoRow(identityRows, "Windows", initialHw.Windows, false);
        hardwareCard.Controls.Add(new Label
        {
            Text = "HARDWARE", Font = new Font("Segoe UI Semibold", 9F), ForeColor = Teal,
            Location = new Point(18, 16), Size = new Size(350, 22), Tag = "section-heading"
        });
        FlowLayoutPanel hardwareRows = CreateOverviewRows(hardwareCard);
        overviewHardwareRows = hardwareRows;
        AddOverviewInfoRow(hardwareRows, "Processor", initialHw.Processor, false);
        AddOverviewInfoRow(hardwareRows, "Graphics", initialHw.Graphics, false);
        AddOverviewInfoRow(hardwareRows, "Memory modules", initialHw.Memory, false);
        AddOverviewInfoRow(hardwareRows, "Storage", initialHw.Storage, false);
        AddOverviewInfoRow(hardwareRows, "Network devices", initialHw.Network, false);
        Panel card = new Panel
        {
            Location = new Point(18, 18), Size = new Size(810, 184),
            BackColor = Color.White, BorderStyle = BorderStyle.None, Tag = "card"
        };
        batteryPage.Controls.Add(card);
        card.Controls.Add(new Label
        {
            Text = "CHARGE LIMIT STATUS", Font = new Font("Segoe UI Semibold", 10.5F),
            ForeColor = Color.FromArgb(232, 237, 243), Location = new Point(18, 16), Size = new Size(760, 24), Tag = "section-heading"
        });
        chargeStatusAccent = new Panel
        {
            Location = new Point(18, 58), Size = new Size(4, 78),
            BackColor = ProtectionGreen, BorderStyle = BorderStyle.None
        };
        card.Controls.Add(chargeStatusAccent);

        stateTitle = new Label
        {
            Text = "Checking status...", Font = new Font("Segoe UI Semibold", 19F),
            ForeColor = Color.FromArgb(39, 57, 74), Location = new Point(36, 62), Size = new Size(490, 34)
        };
        card.Controls.Add(stateTitle);

        stateDetail = new Label
        {
            Text = "Contacting Acer firmware...", Font = new Font("Segoe UI Semibold", 9.5F),
            ForeColor = Muted, Location = new Point(38, 104), Size = new Size(490, 22), AutoEllipsis = false
        };
        card.Controls.Add(stateDetail);

        enableButton = new RoundedButton
        {
            Text = "Enable 80% Limit", Location = new Point(624, 56), Size = new Size(140, 36),
            FlatStyle = FlatStyle.Flat, BackColor = Teal, ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 9F), UseVisualStyleBackColor = false
        };
        enableButton.Tag = "accent";
        enableButton.FlatAppearance.BorderSize = 0;
        enableButton.Click += delegate { ApplyLimit(true); };
        card.Controls.Add(enableButton);
        toolTips.SetToolTip(enableButton, "Use Acer firmware's fixed battery health limit, normally about 80%.");

        disableButton = new RoundedButton
        {
            Text = "Charge to 100%", Location = new Point(624, 102), Size = new Size(140, 36),
            FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = Color.FromArgb(39, 57, 74),
            Font = new Font("Segoe UI Semibold", 9F), UseVisualStyleBackColor = false
        };
        disableButton.FlatAppearance.BorderColor = Color.FromArgb(198, 209, 220);
        disableButton.FlatAppearance.BorderSize = 1;
        disableButton.Click += delegate { ApplyLimit(false); };
        card.Controls.Add(disableButton);
        toolTips.SetToolTip(disableButton, "Allow charging to continue up to 100%.");

        Panel calibrationCard = new Panel
        {
            Location = new Point(18, 222), Size = new Size(396, 220),
            BackColor = Color.White, BorderStyle = BorderStyle.None, Tag = "card"
        };
        batteryPage.Controls.Add(calibrationCard);
        calibrationCard.Controls.Add(new Label
        {
            Text = "BATTERY CALIBRATION", Font = new Font("Segoe UI Semibold", 10.5F),
            ForeColor = Color.FromArgb(232, 237, 243), Location = new Point(18, 14), Size = new Size(350, 24), Tag = "section-heading"
        });
        calibrationCard.Controls.Add(new Label
        {
            Text = "Calibration support is reported separately from the 80% charge limit.",
            Font = new Font("Segoe UI", 8.5F), ForeColor = Muted,
            Location = new Point(18, 48), Size = new Size(350, 36), AutoEllipsis = false
        });
        calibrationStateLabel = new Label
        {
            Text = "…", Font = new Font("Segoe UI Semibold", 9F),
            ForeColor = Color.FromArgb(183, 205, 228), Location = new Point(260, 142), Size = new Size(44, 30),
            TextAlign = ContentAlignment.MiddleRight, AutoEllipsis = false
        };
        calibrationCard.Controls.Add(calibrationStateLabel);
        calibrationToggle = new ToggleSwitch
        {
            Name = "battery-calibration-toggle", Location = new Point(310, 142), Size = new Size(68, 30), Enabled = false,
            AccessibleName = "Battery calibration", AccessibleDescription = "Turn battery calibration on or off."
        };
        calibrationToggle.Toggled += delegate { SetBatteryCalibration(calibrationToggle.Checked); };
        calibrationCard.Controls.Add(calibrationToggle);
        toolTips.SetToolTip(calibrationToggle, "Turn battery calibration on or off.");

        Panel batteryHealthCard = new Panel
        {
            Location = new Point(432, 222), Size = new Size(396, 220), BackColor = Color.White,
            BorderStyle = BorderStyle.None, Tag = "card"
        };
        batteryPage.Controls.Add(batteryHealthCard);
        batteryHealthCard.Controls.Add(new Label
        {
            Text = "BATTERY HEALTH REPORT", Font = new Font("Segoe UI Semibold", 10.5F), ForeColor = Color.FromArgb(232, 237, 243),
            Location = new Point(18, 14), Size = new Size(320, 24), Tag = "section-heading"
        });
        Panel batteryHealthInfoPanel = new Panel
        {
            Location = new Point(18, 58), Size = new Size(360, 76),
            BackColor = Color.FromArgb(43, 51, 61), BorderStyle = BorderStyle.None, Tag = "status-card"
        };
        batteryHealthCard.Controls.Add(batteryHealthInfoPanel);
        batteryHealthSummaryLabel = new Label
        {
            Text = "Reading battery information...", Font = new Font("Segoe UI Semibold", 9F),
            ForeColor = HintBlue, Location = new Point(14, 8), Size = new Size(332, 60), Tag = "hint",
            TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true
        };
        batteryHealthInfoPanel.Controls.Add(batteryHealthSummaryLabel);
        Button batteryReportButton = new RoundedButton
        {
            Text = "Create Report", Location = new Point(128, 154), Size = new Size(140, 36),
            FlatStyle = FlatStyle.Flat, BackColor = Teal, ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 9F), UseVisualStyleBackColor = false, Tag = "accent"
        };
        batteryReportButton.FlatAppearance.BorderSize = 0;
        batteryReportButton.Click += delegate { CreateBatteryHealthReport(); };
        batteryHealthCard.Controls.Add(batteryReportButton);

        Panel coolingCard = new Panel
        {
            Location = new Point(18, 20), Size = new Size(824, 292),
            BackColor = Color.White, BorderStyle = BorderStyle.None, Tag = "card"
        };
        coolingPage.Controls.Add(coolingCard);
        coolingCard.Controls.Add(new Label
        {
            Text = "FAN CONTROL", Font = new Font("Segoe UI Semibold", 10.5F),
            ForeColor = Color.FromArgb(232, 237, 243), Location = new Point(18, 12), Size = new Size(300, 24), Tag = "section-heading"
        });
        coolingCard.Controls.Add(new Label
        {
            Text = "Auto follows Acer's fan curve. Max Cooling runs fans at full speed.",
            Font = new Font("Segoe UI", 9F), ForeColor = HintBlue,
            Location = new Point(18, 39), Size = new Size(770, 20), Tag = "hint"
        });
        Panel fanModePanel = new Panel
        {
            Location = new Point(18, 72), Size = new Size(220, 190),
            BackColor = Color.FromArgb(43, 51, 61), BorderStyle = BorderStyle.None, Tag = "status-card"
        };
        coolingCard.Controls.Add(fanModePanel);
        fanModePanel.Controls.Add(new Label
        {
            Text = "FAN MODE", Font = new Font("Segoe UI Semibold", 9F), ForeColor = SharedHeadingColor,
            Location = new Point(14, 10), Size = new Size(190, 20), Tag = "section-heading"
        });
        autoFanButton = new RoundedButton
        {
            Name = "fan-mode", Text = "Auto Mode", Location = new Point(40, 66), Size = new Size(140, 36),
            FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = Color.FromArgb(39, 57, 74),
            Font = new Font("Segoe UI Semibold", 9F), UseVisualStyleBackColor = false, Tag = "accent"
        };
        ApplyStateButtonAppearance(autoFanButton);
        autoFanButton.Click += delegate { SetFanMode(1); };
        fanModePanel.Controls.Add(autoFanButton);
        maxFanButton = new RoundedButton
        {
            Name = "fan-mode", Text = "Max Cooling", Location = new Point(40, 118), Size = new Size(140, 36),
            FlatStyle = FlatStyle.Flat, BackColor = Teal, ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 9F), UseVisualStyleBackColor = false, Tag = "state-off"
        };
        ApplyStateButtonAppearance(maxFanButton);
        maxFanButton.Click += delegate { SetFanMode(2); };
        fanModePanel.Controls.Add(maxFanButton);

        Panel customFanPanel = new Panel
        {
            Location = new Point(256, 72), Size = new Size(550, 190),
            BackColor = Color.FromArgb(43, 51, 61), BorderStyle = BorderStyle.None, Tag = "status-card"
        };
        coolingCard.Controls.Add(customFanPanel);
        customFanPanel.Controls.Add(new Label
        {
            Text = "CUSTOM FAN SPEEDS", Font = new Font("Segoe UI Semibold", 9F), ForeColor = SharedHeadingColor,
            Location = new Point(14, 10), Size = new Size(510, 20), Tag = "section-heading"
        });
        cpuFanValueLabel = new Label
        {
            Text = "CPU fan: 70%", Location = new Point(14, 48), Size = new Size(250, 18),
            ForeColor = HintBlue, Font = new Font("Segoe UI", 8.5F), Tag = "hint"
        };
        customFanPanel.Controls.Add(cpuFanValueLabel);
        cpuFanTrack = new TrackBar
        {
            Minimum = 40, Maximum = 100, Value = 70, TickFrequency = 20,
            Location = new Point(8, 70), Size = new Size(259, 38)
        };
        customFanPanel.Controls.Add(cpuFanTrack);
        cpuFanTrack.ValueChanged += delegate { cpuFanValueLabel.Text = "CPU fan: " + cpuFanTrack.Value + "%"; };
        gpuFanValueLabel = new Label
        {
            Text = "GPU fan: 70%", Location = new Point(289, 48), Size = new Size(250, 18),
            ForeColor = HintBlue, Font = new Font("Segoe UI", 8.5F), Tag = "hint"
        };
        customFanPanel.Controls.Add(gpuFanValueLabel);
        gpuFanTrack = new TrackBar
        {
            Minimum = 40, Maximum = 100, Value = 70, TickFrequency = 20,
            Location = new Point(283, 70), Size = new Size(259, 38)
        };
        customFanPanel.Controls.Add(gpuFanTrack);
        gpuFanTrack.ValueChanged += delegate { gpuFanValueLabel.Text = "GPU fan: " + gpuFanTrack.Value + "%"; };
        Button customFanButton = new RoundedButton
        {
            Name = "fan-custom-apply", Text = "Apply Custom", Location = new Point(205, 126), Size = new Size(140, 36),
            FlatStyle = FlatStyle.Flat, BackColor = Teal, ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 9F), UseVisualStyleBackColor = false, Tag = "accent"
        };
        customFanButton.FlatAppearance.BorderSize = 0;
        customFanButton.Click += delegate { SetCustomFanSpeeds(); };
        customFanPanel.Controls.Add(customFanButton);
        toolTips.SetToolTip(cpuFanTrack, "Set the custom CPU fan speed before applying.");
        toolTips.SetToolTip(gpuFanTrack, "Set the custom GPU fan speed before applying.");
        toolTips.SetToolTip(customFanButton, "Send both slider values to Acer firmware.");
        toolTips.SetToolTip(autoFanButton, "Return both fans to the firmware's automatic curve.");
        toolTips.SetToolTip(maxFanButton, "Request the firmware's maximum cooling mode.");

        Panel powerCard = new Panel
        {
            Location = new Point(18, 344), Size = new Size(824, 228),
            BackColor = Color.White, BorderStyle = BorderStyle.None, Tag = "card"
        };
        performancePage.Controls.Add(powerCard);
        powerCard.Controls.Add(new Label
        {
            Text = "WINDOWS POWER PLAN", Font = new Font("Segoe UI Semibold", 10.5F),
            ForeColor = Color.FromArgb(232, 237, 243), Location = new Point(18, 16), Size = new Size(360, 24), Tag = "section-heading"
        });
        powerCard.Controls.Add(new Label
        {
            Text = "Choose separate Windows plans for AC and battery power.",
            Font = new Font("Segoe UI", 9F), ForeColor = HintBlue,
            Location = new Point(18, 44), Size = new Size(784, 20), Tag = "hint"
        });
        Panel acPlanPanel = new Panel
        {
            Location = new Point(18, 78), Size = new Size(388, 120),
            BackColor = Color.FromArgb(43, 51, 61), BorderStyle = BorderStyle.None, Tag = "status-card"
        };
        Panel batteryPlanPanel = new Panel
        {
            Location = new Point(418, 78), Size = new Size(388, 120),
            BackColor = Color.FromArgb(43, 51, 61), BorderStyle = BorderStyle.None, Tag = "status-card"
        };
        powerCard.Controls.Add(acPlanPanel);
        powerCard.Controls.Add(batteryPlanPanel);
        acPlanPanel.Controls.Add(new Label
        {
            Text = "AC POWER (PLUGGED IN)", Font = new Font("Segoe UI Semibold", 8.5F),
            ForeColor = SharedHeadingColor, Location = new Point(14, 12), Size = new Size(255, 18), Tag = "section-heading"
        });
        batteryPlanPanel.Controls.Add(new Label
        {
            Text = "BATTERY POWER (ON BATTERY)", Font = new Font("Segoe UI Semibold", 8.5F),
            ForeColor = SharedHeadingColor, Location = new Point(14, 12), Size = new Size(260, 18), Tag = "section-heading"
        });
        string[] planNames = new string[] { "Power Saver", "Balanced", "High Performance" };
        for (int sourceIndex = 0; sourceIndex < 2; sourceIndex++)
        {
            Panel sourcePanel = sourceIndex == 0 ? acPlanPanel : batteryPlanPanel;
            for (int planIndex = 0; planIndex < planNames.Length; planIndex++)
            {
                int capturedSource = sourceIndex;
                int capturedPlan = planIndex;
                int buttonIndex = sourceIndex * planNames.Length + planIndex;
                powerPlanButtons[buttonIndex] = new RoundedButton
                {
                    Name = sourceIndex == 0 ? "ac-power-plan-option" : "battery-power-plan-option",
                    Text = planNames[planIndex], Location = new Point(14 + planIndex * 122, 43),
                    Size = new Size(116, 38), FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(49, 59, 70), ForeColor = Color.FromArgb(245, 247, 250),
                    Font = new Font("Segoe UI", 8F), UseVisualStyleBackColor = false,
                    Tag = "state-off", TextAlign = ContentAlignment.MiddleCenter
                };
                powerPlanButtons[buttonIndex].Click += delegate
                {
                    showingBatteryPlans = capturedSource == 1;
                    SelectPowerPlan(capturedPlan);
                };
                sourcePanel.Controls.Add(powerPlanButtons[buttonIndex]);
            }
        }
        performancePage.Controls.Add(new Label
        {
            Text = "Keep NitroHub open for fan control and automatic power plan switching; fans reset to Auto on exit.",
            Font = new Font("Segoe UI", 8.5F), ForeColor = HintBlue,
            Location = new Point(18, 590), Size = new Size(784, 18), Tag = "hint"
        });

        Panel monitorCard = new Panel
        {
            Location = new Point(18, 20), Size = new Size(810, 580),
            BackColor = Color.White, BorderStyle = BorderStyle.None, Tag = "card"
        };
        monitorPage.Controls.Add(monitorCard);
        monitorCard.Controls.Add(new Label
        {
            Text = "LIVE HARDWARE MONITOR", Font = new Font("Segoe UI Semibold", 10.5F),
            ForeColor = Color.FromArgb(232, 237, 243), Location = new Point(18, 11), Size = new Size(350, 24), Tag = "section-heading"
        });
        CheckBox fahrenheitCheck = new CheckBox
        {
            Text = "Show temperatures in Fahrenheit", Location = new Point(18, 244), Size = new Size(300, 24),
            ForeColor = Color.FromArgb(39, 57, 74), Checked = false, AutoSize = false
        };
        fahrenheitCheck.CheckedChanged += delegate { useFahrenheit = fahrenheitCheck.Checked; RefreshTelemetry(); temperatureChart.Invalidate(); };
        monitorCard.Controls.Add(fahrenheitCheck);
        cpuStatusValueLabel = AddMonitorStatusCard(monitorCard, "CPU", 18, 90, 250, HintBlue);
        gpuStatusValueLabel = AddMonitorStatusCard(monitorCard, "GPU", 278, 90, 250, HintBlue);
        ramStatusValueLabel = AddMonitorStatusCard(monitorCard, "RAM", 538, 90, 250, HintBlue);
        systemStatusValueLabel = AddMonitorStatusCard(monitorCard, "SYSTEM", 18, 164, 250, HintBlue);
        cpuFanStatusValueLabel = AddMonitorStatusCard(monitorCard, "CPU FAN", 278, 164, 250, HintBlue);
        gpuFanStatusValueLabel = AddMonitorStatusCard(monitorCard, "GPU FAN SPEED", 538, 164, 250, HintBlue);
        telemetryDetailsLabel = new Label
        {
            Text = "Temperatures and fan speeds come from Acer sensors. CPU/GPU load uses Windows counters; sampling and GPU engine aggregation may differ from Task Manager. RAM reports committed memory, not physical RAM usage.", Font = new Font("Segoe UI", 8.5F),
            ForeColor = HintBlue, Location = new Point(18, 42), Size = new Size(770, 36), AutoEllipsis = false, Tag = "hint"
        };
        monitorCard.Controls.Add(telemetryDetailsLabel);
        temperatureChart = new BufferedPanel
        {
            Location = new Point(18, 280), Size = new Size(380, 270),
            BackColor = Color.FromArgb(250, 252, 254), BorderStyle = BorderStyle.None, Tag = "chart"
        };
        temperatureChart.Paint += DrawTemperatureHistory;
        temperatureChart.MouseMove += delegate(object sender, MouseEventArgs e)
        {
            int count = Math.Max(cpuTemperatureHistory.Count, Math.Max(gpuTemperatureHistory.Count, systemTemperatureHistory.Count));
            int nextIndex = GetChartHoverIndex(temperatureChart, e.X, e.Y, count);
            if (nextIndex != tempChartHoverIndex)
            {
                tempChartHoverIndex = nextIndex;
                temperatureChart.Invalidate();
            }
        };
        temperatureChart.MouseLeave += delegate
        {
            if (tempChartHoverIndex != -1)
            {
                tempChartHoverIndex = -1;
                temperatureChart.Invalidate();
            }
        };
        monitorCard.Controls.Add(temperatureChart);
        usageChart = new BufferedPanel
        {
            Location = new Point(412, 280), Size = new Size(380, 270),
            BackColor = Color.FromArgb(250, 252, 254), BorderStyle = BorderStyle.None, Tag = "chart"
        };
        usageChart.Paint += DrawUsageHistory;
        usageChart.MouseMove += delegate(object sender, MouseEventArgs e)
        {
            int count = Math.Max(cpuUsageHistory.Count, gpuUsageHistory.Count);
            int nextIndex = GetChartHoverIndex(usageChart, e.X, e.Y, count);
            if (nextIndex != usageChartHoverIndex)
            {
                usageChartHoverIndex = nextIndex;
                usageChart.Invalidate();
            }
        };
        usageChart.MouseLeave += delegate
        {
            if (usageChartHoverIndex != -1)
            {
                usageChartHoverIndex = -1;
                usageChart.Invalidate();
            }
        };
        monitorCard.Controls.Add(usageChart);

        Panel keyboardCard = new Panel
        {
            Location = new Point(18, 20), Size = new Size(810, 420),
            BackColor = Color.White, BorderStyle = BorderStyle.None, Tag = "card"
        };
        lightingPage.Controls.Add(keyboardCard);
        keyboardCard.Controls.Add(new Label
        {
            Text = "KEYBOARD LIGHTING", Font = new Font("Segoe UI Semibold", 10.5F),
            ForeColor = Color.FromArgb(232, 237, 243), Location = new Point(18, 14), Size = new Size(300, 24), Tag = "section-heading"
        });
        keyboardCapabilityLabel = new Label
        {
            Text = "Detecting keyboard firmware capabilities...",
            Font = new Font("Segoe UI", 8.5F), ForeColor = Muted,
            Location = new Point(18, 45), Size = new Size(770, 20), AutoEllipsis = false, Tag = "hint"
        };
        keyboardCard.Controls.Add(keyboardCapabilityLabel);
        KeyboardCapabilityReport keyboardCapabilities = DetectKeyboardCapabilities();
        keyboardEffectControlAvailable = keyboardCapabilities.CanSetEffect && keyboardCapabilities.Effects.Length > 0;
        keyboardCapabilityLabel.Text = keyboardCapabilities.Status;
        keyboardCard.Controls.Add(new Label
        {
            Text = "VISUAL PREVIEW", Location = new Point(18, 78), Size = new Size(250, 18),
            ForeColor = Color.FromArgb(126, 166, 214), Font = new Font("Segoe UI Semibold", 8.5F), Tag = "section-heading"
        });
        keyboardLightingPreview = new KeyboardLightingPreview
        {
            Location = new Point(18, 100), Size = new Size(466, 246),
            AccessibleName = "Animated keyboard lighting preview",
            AccessibleDescription = "Illustrative keyboard lighting preview; firmware controls lighting effects for the keyboard as a whole."
        };
        keyboardCard.Controls.Add(keyboardLightingPreview);
        lightingStatusLabel = new Label
        {
            Text = String.Empty, Location = new Point(18, 408), Size = new Size(770, 24),
            ForeColor = Muted, Font = new Font("Segoe UI", 8.5F), AutoEllipsis = true
        };
        keyboardCard.Controls.Add(lightingStatusLabel);

        keyboardCard.Controls.Add(new Label
        {
            Text = "LIGHTING EFFECT", Location = new Point(506, 78), Size = new Size(240, 18),
            ForeColor = Muted, Font = new Font("Segoe UI Semibold", 8.5F), Tag = "section-heading"
        });
        effectCombo = new ThemedComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(506, 100), Size = new Size(286, 36),
            Font = new Font("Segoe UI", 9F)
        };
        effectCombo.Items.AddRange(keyboardCapabilities.Effects);
        effectCombo.Enabled = keyboardCapabilities.CanSetEffect && keyboardCapabilities.Effects.Length > 0;
        if (effectCombo.Items.Count > 0) effectCombo.SelectedIndex = 0;
        effectCombo.SelectedIndexChanged += delegate { UpdateLightingColorControl(); UpdateKeyboardPreview(); };
        keyboardCard.Controls.Add(effectCombo);
        toolTips.SetToolTip(effectCombo, "Choose one of the lighting effects supported by the firmware.");

        keyboardCard.Controls.Add(new Label
        {
            Text = "EFFECT COLOR", Location = new Point(506, 146), Size = new Size(180, 18),
            ForeColor = Muted, Font = new Font("Segoe UI Semibold", 8.5F), Tag = "section-heading"
        });
        lightingColorButton = new RoundedButton
        {
            Text = "Choose color", Location = new Point(506, 170), Size = new Size(124, 34),
            FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(34, 41, 49), ForeColor = Color.FromArgb(232, 237, 243),
            Font = new Font("Segoe UI Semibold", 9F), UseVisualStyleBackColor = false
        };
        lightingColorButton.FlatAppearance.BorderColor = Color.FromArgb(198, 209, 220);
        lightingColorButton.Click += delegate { ChooseLightingColor(); };
        keyboardCard.Controls.Add(lightingColorButton);
        lightingColorPreview = new Panel
        {
            Location = new Point(640, 173), Size = new Size(124, 28), BackColor = lightingColor,
            BorderStyle = BorderStyle.FixedSingle, Tag = "color-preview"
        };
        keyboardCard.Controls.Add(lightingColorPreview);
        lightingColorStatusLabel = new Label
        {
            Text = String.Empty, Location = new Point(506, 210), Size = new Size(286, 16),
            ForeColor = HintBlue, Font = new Font("Segoe UI", 8F), AutoEllipsis = true, Tag = "hint"
        };
        keyboardCard.Controls.Add(lightingColorStatusLabel);

        brightnessTitleLabel = new Label
        {
            Text = "BRIGHTNESS", Location = new Point(506, 227), Size = new Size(200, 18),
            ForeColor = Muted, Font = new Font("Segoe UI Semibold", 8.5F), Tag = "section-heading"
        };
        keyboardCard.Controls.Add(brightnessTitleLabel);
        brightnessValueLabel = new Label
        {
            Text = "70%", Location = new Point(742, 227), Size = new Size(50, 18),
            TextAlign = ContentAlignment.MiddleRight, ForeColor = HintBlue, Font = new Font("Segoe UI", 8.5F), Tag = "hint"
        };
        keyboardCard.Controls.Add(brightnessValueLabel);
        brightnessTrack = new TrackBar
        {
            Minimum = 0, Maximum = 100, Value = 70, TickFrequency = 25,
            Location = new Point(500, 247), Size = new Size(296, 42)
        };
        brightnessTrack.ValueChanged += delegate { brightnessValueLabel.Text = brightnessTrack.Value + "%"; UpdateKeyboardPreview(); };
        keyboardCard.Controls.Add(brightnessTrack);
        toolTips.SetToolTip(brightnessTrack, "Preview the brightness, then apply it to the keyboard.");

        effectSpeedTitleLabel = new Label
        {
            Text = "EFFECT SPEED", Location = new Point(506, 291), Size = new Size(200, 18),
            ForeColor = Muted, Font = new Font("Segoe UI Semibold", 8.5F), Tag = "section-heading"
        };
        keyboardCard.Controls.Add(effectSpeedTitleLabel);
        speedValueLabel = new Label
        {
            Text = "5 / 9", Location = new Point(742, 291), Size = new Size(50, 18),
            TextAlign = ContentAlignment.MiddleRight, ForeColor = HintBlue, Font = new Font("Segoe UI", 8.5F), Tag = "hint"
        };
        keyboardCard.Controls.Add(speedValueLabel);
        speedTrack = new TrackBar
        {
            Minimum = 0, Maximum = 9, Value = 5, TickFrequency = 3,
            Location = new Point(500, 311), Size = new Size(296, 42)
        };
        speedTrack.ValueChanged += delegate { speedValueLabel.Text = speedTrack.Value + " / 9"; UpdateKeyboardPreview(); };
        keyboardCard.Controls.Add(speedTrack);
        toolTips.SetToolTip(speedTrack, "Preview the effect speed, then apply it to the keyboard.");

        applyKeyboardButton = new RoundedButton
        {
            Text = "Apply Effect", Location = new Point(569, 360), Size = new Size(160, 42),
            FlatStyle = FlatStyle.Flat, BackColor = Teal, ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 9F), UseVisualStyleBackColor = false
        };
        applyKeyboardButton.Tag = "accent";
        applyKeyboardButton.FlatAppearance.BorderSize = 0;
        applyKeyboardButton.Click += delegate { ApplyKeyboardSettings(); };
        keyboardCard.Controls.Add(applyKeyboardButton);
        toolTips.SetToolTip(applyKeyboardButton, "Apply the selected firmware effect to the keyboard.");
        UpdateLightingColorControl();
        bool keyboardEnabled = keyboardCapabilities.CanSetEffect && keyboardCapabilities.Effects.Length > 0;
        lightingColorButton.Enabled = keyboardEnabled && effectCombo.SelectedIndex >= 0 && ((KeyboardEffectProfile)effectCombo.SelectedItem).SupportsCustomColor;
        brightnessTrack.Enabled = keyboardEnabled;
        speedTrack.Enabled = keyboardEnabled;
        applyKeyboardButton.Enabled = keyboardEnabled;
        keyboardLightingPreview.Visible = keyboardEnabled;
        if (!keyboardEnabled)
        {
            lightingStatusLabel.Text = "Preview is hidden until this model has a verified keyboard effect profile.";
            lightingStatusLabel.Location = new Point(18, 185);
            lightingStatusLabel.Size = new Size(466, 40);
            lightingStatusLabel.TextAlign = ContentAlignment.MiddleCenter;
        }
        UpdateKeyboardPreview();

        Panel timeoutCard = new Panel
        {
            Location = new Point(18, 458), Size = new Size(810, 140),
            BackColor = Color.White, BorderStyle = BorderStyle.None, Tag = "card"
        };
        lightingPage.Controls.Add(timeoutCard);
        timeoutCard.Controls.Add(new Label
        {
            Text = "BACKLIGHT TIMEOUT", Font = new Font("Segoe UI Semibold", 10.5F),
            ForeColor = Color.FromArgb(232, 237, 243), Location = new Point(18, 14), Size = new Size(300, 24), Tag = "section-heading"
        });
        keyboardTimeoutAccent = new Panel
        {
            Location = new Point(18, 54), Size = new Size(4, 48),
            BackColor = Color.FromArgb(92, 105, 119), BorderStyle = BorderStyle.None, Tag = "accent-strip"
        };
        timeoutCard.Controls.Add(keyboardTimeoutAccent);
        timeoutCard.Controls.Add(new Label
        {
            Text = "Keyboard backlight auto-off",
            Location = new Point(34, 52), Size = new Size(580, 24),
            ForeColor = Color.FromArgb(232, 237, 243), Font = new Font("Segoe UI Semibold", 10F)
        });
        timeoutCard.Controls.Add(new Label
        {
            Text = "Turn off lighting after 30 seconds of inactivity.",
            Location = new Point(34, 79), Size = new Size(610, 20),
            ForeColor = HintBlue, Font = new Font("Segoe UI", 8.5F), Tag = "hint"
        });
        keyboardTimeoutStateLabel = new Label
        {
            Text = "OFF", Location = new Point(678, 60), Size = new Size(46, 30),
            TextAlign = ContentAlignment.MiddleRight, ForeColor = HintBlue,
            Font = new Font("Segoe UI Semibold", 9F), Visible = false
        };
        timeoutCard.Controls.Add(keyboardTimeoutStateLabel);
        keyboardTimeoutToggleButton = new ToggleSwitch
        {
            Name = "keyboard-timeout-toggle", Location = new Point(724, 60), Size = new Size(68, 30),
            AccessibleName = "Keyboard backlight timeout", AccessibleDescription = "Turn the 30-second keyboard backlight timeout on or off."
        };
        keyboardTimeoutToggleButton.Toggled += delegate
        {
            keyboardTimeoutEnabled = keyboardTimeoutToggleButton.Checked;
            UpdateKeyboardTimeoutToggleStyle();
            SetKeyboardTimeoutFromUi(keyboardTimeoutEnabled);
        };
        timeoutCard.Controls.Add(keyboardTimeoutToggleButton);
        UpdateKeyboardTimeoutToggleStyle();
        toolTips.SetToolTip(keyboardTimeoutToggleButton, "Toggle the keyboard backlight's 30-second timeout. Unsupported firmware may reject changes.");

        supportPagePanel = supportPage;
        Panel acerSupportCard = new Panel
        {
            Location = new Point(18, 20), Size = new Size(810, 122),
            BackColor = Color.White, BorderStyle = BorderStyle.None, Tag = "card"
        };
        supportPage.Controls.Add(acerSupportCard);
        acerSupportCard.Controls.Add(new Label
        {
            Text = "ACER DRIVERS & MANUALS", Font = new Font("Segoe UI Semibold", 10.5F),
            ForeColor = Color.FromArgb(232, 237, 243), Location = new Point(18, 14), Size = new Size(400, 24),
            Tag = "section-heading", UseMnemonic = false, TextAlign = ContentAlignment.MiddleLeft
        });
        supportSerialValue = initialHw.Serial;
        supportModelValue = initialHw.AcerModelCode;
        supportSkuValue = initialHw.AcerSku;
        string supportModelText = String.IsNullOrWhiteSpace(supportModelValue) ? "Unavailable" : supportModelValue;
        string supportSerialText = String.IsNullOrWhiteSpace(supportSerialValue) ? "Unavailable" : supportSerialValue;
        Font supportValueFont = new Font("Segoe UI Semibold", 9.5F);

        acerSupportCard.Controls.Add(new Label
        {
            Text = "MODEL", Font = new Font("Segoe UI", 7.5F), ForeColor = Muted,
            Location = new Point(18, 54), Size = new Size(220, 15), UseMnemonic = false
        });
        supportModelLabel = new Label
        {
            Text = supportModelText, Font = supportValueFont, ForeColor = Color.FromArgb(245, 247, 250),
            Location = new Point(18, 72), Size = new Size(220, 22), AutoEllipsis = true, UseMnemonic = false,
            TextAlign = ContentAlignment.MiddleLeft
        };
        acerSupportCard.Controls.Add(supportModelLabel);
        acerSupportCard.Controls.Add(new Label
        {
            Text = "SERIAL NUMBER", Font = new Font("Segoe UI", 7.5F), ForeColor = Muted,
            Location = new Point(262, 54), Size = new Size(260, 15), UseMnemonic = false
        });
        int serialMeasured96 = (int)Math.Round(TextRenderer.MeasureText(supportSerialText, supportValueFont).Width / UiDpi.Scale);
        int serialWidth = Math.Min(250, serialMeasured96 + 4);
        supportSerialLabel = new Label
        {
            Text = supportSerialText, Font = supportValueFont, ForeColor = Color.FromArgb(245, 247, 250),
            Location = new Point(262, 72), Size = new Size(serialWidth, 22), AutoEllipsis = true, UseMnemonic = false,
            TextAlign = ContentAlignment.MiddleLeft
        };
        acerSupportCard.Controls.Add(supportSerialLabel);
        supportCopyButton = new CopyIconButton
        {
            Name = "overview-copy", Text = String.Empty, Location = new Point(262 + serialWidth + 3, 74),
            Size = new Size(18, 18), FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(49, 59, 70), ForeColor = Color.FromArgb(245, 247, 250),
            Font = new Font("Segoe UI Semibold", 8.5F), UseVisualStyleBackColor = false,
            Tag = "state-off", AccessibleName = "Copy serial number",
            Visible = supportSerialText != "Unavailable" && supportSerialText != "Loading..."
        };
        supportCopyButton.FlatAppearance.BorderColor = Color.FromArgb(112, 127, 142);
        supportCopyButton.FlatAppearance.BorderSize = 1;
        supportCopyButton.Click += delegate
        {
            string val = String.IsNullOrWhiteSpace(supportSerialValue) ? "Unavailable" : supportSerialValue;
            if (val == "Unavailable" || val == "Loading...") return;
            try { Clipboard.SetText(val); toolTips.SetToolTip(supportCopyButton, "Serial number copied."); }
            catch { }
        };
        toolTips.SetToolTip(supportCopyButton, "Copy the serial number to the clipboard.");
        acerSupportCard.Controls.Add(supportCopyButton);
        Button acerDriversButton = new RoundedButton
        {
            Name = "support-link", Text = "Open Drivers and Manuals",
            Location = new Point(590, 58), Size = new Size(200, 40),
            FlatStyle = FlatStyle.Flat, BackColor = Teal, ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", ActionButtonFontSize), UseVisualStyleBackColor = false, Tag = "accent"
        };
        acerDriversButton.FlatAppearance.BorderSize = 0;
        acerDriversButton.Click += delegate
        {
            OpenSupportUrl(GetAcerProductSupportUrl(supportModelValue, supportSkuValue, supportSerialValue));
        };
        acerSupportCard.Controls.Add(acerDriversButton);

        List<string> graphicsAdapters = initialHw.GraphicsList ?? new List<string>();
        List<string> nvidiaAdapters = FindGraphicsAdapters(graphicsAdapters, "NVIDIA", "GeForce", "RTX", "GTX");
        List<string> amdAdapters = FindGraphicsAdapters(graphicsAdapters, "AMD", "Radeon");
        List<string> intelAdapters = FindGraphicsAdapters(graphicsAdapters, "Intel");
        List<string> otherAdapters = FindOtherGraphicsAdapters(graphicsAdapters);
        int vendorCount = (nvidiaAdapters.Count > 0 ? 1 : 0) + (amdAdapters.Count > 0 ? 1 : 0) + (intelAdapters.Count > 0 ? 1 : 0);
        int otherRowCount = vendorCount == 0 || otherAdapters.Count > 0 ? 1 : 0;
        int totalGraphicsRows = Math.Max(1, vendorCount + otherRowCount);
        int graphicsCardHeight = 68 + totalGraphicsRows * 74;
        Panel graphicsSupportCard = new Panel
        {
            Location = new Point(18, 158), Size = new Size(810, graphicsCardHeight),
            BackColor = Color.White, BorderStyle = BorderStyle.None, Tag = "graphics-support-panel"
        };
        supportPage.Controls.Add(graphicsSupportCard);
        graphicsSupportCard.Controls.Add(new Label
        {
            Text = "GRAPHICS DRIVERS", Font = new Font("Segoe UI Semibold", 10.5F),
            ForeColor = Color.FromArgb(232, 237, 243), Location = new Point(18, 14), Size = new Size(400, 24),
            Tag = "section-heading", TextAlign = ContentAlignment.MiddleLeft
        });
        int graphicsRow = 0;
        if (nvidiaAdapters.Count > 0)
            AddGraphicsSupportRow(graphicsSupportCard, "NVIDIA", nvidiaAdapters, graphicsRow++, "NVIDIA App", "https://www.nvidia.com/en-us/software/nvidia-app/");
        if (amdAdapters.Count > 0)
            AddGraphicsSupportRow(graphicsSupportCard, "AMD", amdAdapters, graphicsRow++, "AMD Drivers", "https://www.amd.com/en/support/download/drivers.html");
        if (intelAdapters.Count > 0)
            AddGraphicsSupportRow(graphicsSupportCard, "Intel", intelAdapters, graphicsRow++, "Intel Driver Assistant", "https://www.intel.com/content/www/us/en/support/detect.html");
        if (graphicsRow == 0 || otherAdapters.Count > 0)
            AddGraphicsSupportRow(graphicsSupportCard, "Other graphics", otherAdapters.Count == 0 ? graphicsAdapters : otherAdapters, graphicsRow, String.Empty, String.Empty);

        BuildFeaturesPage(featuresPage);
        BuildSettingsPage(settingsPage);

        telemetryTimer = new Timer { Interval = 2000 };
        telemetryTimer.Tick += delegate
        {
            if (uiClosing) return;
            try { CheckPowerSourceTransition(); } catch { }
            // Keep polling at the throttled 5s interval even when minimized/in tray so the tray tooltip stays live.
            try { RefreshTelemetry(); } catch { }
        };
        Shown += delegate
        {
            // Paint the window before deferring background startup work and page handle prewarming.
            Refresh();
            Timer startupDelay = new Timer { Interval = 150 };
            startupDelay.Tick += delegate
            {
                startupDelay.Stop();
                startupDelay.Dispose();
                InitializeStartupRoutine();
                StartPagePrewarm();
            };
            startupDelay.Start();
        };
        if (startMinimizedToTray)
        {
            Timer delayInit = new Timer { Interval = 2500 };
            delayInit.Tick += delegate
            {
                delayInit.Stop();
                delayInit.Dispose();
                InitializeStartupRoutine();
                StartPagePrewarm();
            };
            delayInit.Start();
        }
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += SystemUserPreferenceChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged += SystemPowerModeChanged;
        FormClosed += delegate
        {
            uiClosing = true;
            telemetryTimer.Stop();
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= SystemUserPreferenceChanged;
            Microsoft.Win32.SystemEvents.PowerModeChanged -= SystemPowerModeChanged;
            if (resumeReapplyTimer != null)
            {
                resumeReapplyTimer.Stop();
                resumeReapplyTimer.Dispose();
                resumeReapplyTimer = null;
            }
            ShutdownBluelightShield();
            // SetDeviceGammaRamp changes outlive the process: unless the user chose to keep the filter, put the screen colors
            // back to normal on a real exit. The saved level stays on disk, so it is re-applied on the next launch.
            bool keepBluelight = settingsPanel != null && settingsPanel.CurrentConfig != null && settingsPanel.CurrentConfig.KeepBluelightOnExit;
            if (!keepBluelight)
            {
                try { bool ignoredSoftened; BluelightShield.Apply(0, out ignoredSoftened); } catch { }
            }
            if (trayIcon != null)
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
            }
            if (trayMenu != null) trayMenu.Dispose();
            if (trayOpenItemFont != null) { trayOpenItemFont.Dispose(); trayOpenItemFont = null; }
            if (trayMenuFont != null) { trayMenuFont.Dispose(); trayMenuFont = null; }
            lock (counterLock)
            {
                if (cpuLoadCounter != null) { cpuLoadCounter.Dispose(); cpuLoadCounter = null; }
                if (memoryLoadCounter != null) { memoryLoadCounter.Dispose(); memoryLoadCounter = null; }
            }
            if (headerLogoImage != null) headerLogoImage.Dispose();
            toolTips.Dispose();
        };
        FormClosing += delegate(object sender, FormClosingEventArgs e)
        {
            if (!allowExit && e.CloseReason == CloseReason.UserClosing)
            {
                string closeMode = (settingsPanel != null && settingsPanel.CurrentConfig != null)
                    ? settingsPanel.CurrentConfig.CloseBehavior
                    : "Quit application";
                if (String.Equals(closeMode, "Quit application", StringComparison.OrdinalIgnoreCase))
                {
                    allowExit = true;
                }
                else
                {
                    e.Cancel = true;
                    HideToTray();
                    return;
                }
            }
            if (settingsPanel != null)
            {
                try { settingsPanel.UnregisterGlobalHotkey(); }
                catch { }
            }
            ResetFanToAutoBeforeExit();
            uiClosing = true;
        };
        Resize += delegate
        {
            UpdateTelemetryPollingRate();
        };
        ApplyDpiLayout();
        ConvertPanelsToRounded(this);
        MakeTextBackgroundsTransparent(this);
        ApplyUniformButtonMetrics(this);
        lightingColorButton.Width = UiDpi.S(124);
        lightingColorButton.Height = UiDpi.S(34);
        lightingColorPreview.Width = UiDpi.S(124);
        lightingColorPreview.Height = UiDpi.S(28);
        InitializeSystemTray();
        // Apply the saved theme before first paint to avoid a light-to-dark flash.
        ApplyTheme();
        CaptureShellBase();
    }

    // Every control is positioned in 96-DPI pixels. Scale the whole tree once to the system DPI so text, buttons and cards keep
    // their proportions at 125%-300%, and keep the window inside the work area on small or heavily scaled screens.
    private void ApplyDpiLayout()
    {
        UiDpi.MarkApplied();
        SuspendLayout();
        try
        {
            UiDpi.ScaleChildren(this);
            ClientSize = new Size(UiDpi.S(1028), UiDpi.S(760));
            MinimumSize = new Size(UiDpi.S(680), UiDpi.S(500));
            FitWindowToWorkingArea();
        }
        finally { ResumeLayout(true); }
        if (settingsPanel != null) settingsPanel.RelayoutAfterDpi();
    }

    private void FitWindowToWorkingArea()
    {
        try
        {
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            int overflow = Height - area.Height;
            if (overflow <= 0 || pageContainer == null || pageList == null) return;
            int shrink = Math.Min(overflow, Math.Max(0, pageContainer.Height - UiDpi.S(300)));
            if (shrink <= 0) return;
            pageContainer.Height -= shrink;
            ClientSize = new Size(ClientSize.Width, ClientSize.Height - shrink);
            // The pages keep their full design height and scroll; the Settings page (last) has its own scrolling.
            for (int i = 0; i < pageList.Length - 1; i++)
            {
                if (pageList[i] == null) continue;
                pageList[i].AutoScroll = true;
                pageList[i].AutoScrollMinSize = new Size(0, UiDpi.S(620));
            }
        }
        catch { }
    }

    private Size baseClient, baseNavSize, basePageSize;
    private Point baseNavLoc, basePageLoc;
    private int baseHeaderBottom;
    private bool shellCaptured;
    private float zoomFactor = 1F;
    private Dictionary<Control, ZoomState> zoomMap;
    private static readonly Dictionary<string, Font> zoomFonts = new Dictionary<string, Font>();

    private sealed class ZoomState
    {
        public Rectangle Bounds;
        public Font Font;
        public Padding Margin;
        public Padding Padding;
        public DockStyle Dock;
        public bool AutoSize;
    }

    private void CaptureShellBase()
    {
        if (navigationPanel == null || pageContainer == null || appHeader == null) return;
        baseClient = ClientSize;
        baseNavLoc = navigationPanel.Location;
        basePageLoc = pageContainer.Location;
        baseNavSize = navigationPanel.Size;
        basePageSize = pageContainer.Size;
        baseHeaderBottom = appHeader.Bottom;
        shellCaptured = true;
    }

    private void LayoutShell()
    {
        if (!shellCaptured || appHeader == null) return;
        int w = ClientSize.Width, h = ClientSize.Height;
        if (w <= 0 || h <= 0) return;
        SuspendLayout();
        appHeader.Width = w;
        if (Math.Abs(zoomFactor - 1F) <= 0.001F)
        {
            int dx = Math.Max(0, (w - baseClient.Width) / 2);
            navigationPanel.Location = new Point(baseNavLoc.X + dx, baseNavLoc.Y);
            pageContainer.Location = new Point(basePageLoc.X + dx, basePageLoc.Y);
        }
        else
        {
            // Zoomed (maximized or snapped): keep design proportions, center the block horizontally, start right below the header.
            float f = zoomFactor;
            int blockWidth = (int)Math.Round((basePageLoc.X + basePageSize.Width - baseNavLoc.X) * f);
            int x0 = Math.Max(0, (w - blockWidth) / 2);
            int top = baseHeaderBottom;
            navigationPanel.Location = new Point(x0, top + (int)Math.Round((baseNavLoc.Y - top) * f));
            pageContainer.Location = new Point(x0 + (int)Math.Round((basePageLoc.X - baseNavLoc.X) * f),
                top + (int)Math.Round((basePageLoc.Y - top) * f));
        }
        ResumeLayout(false);
        appHeader.Invalidate();
    }

    private float ComputeZoom()
    {
        int availableHeight = ClientSize.Height - baseHeaderBottom;
        int baseHeight = baseClient.Height - baseHeaderBottom;
        if (availableHeight <= 0 || baseHeight <= 0 || baseClient.Width <= 0) return 1F;
        float f = Math.Min(ClientSize.Width / (float)baseClient.Width, availableHeight / (float)baseHeight);
        if (f >= 0.98F && f <= 1.05F) return 1F;
        f = Math.Max(0.65F, Math.Min(2.5F, f));
        return (float)Math.Floor(f * 100F) / 100F;
    }

    private static void CollectZoom(Control parent, Dictionary<Control, ZoomState> map)
    {
        foreach (Control child in parent.Controls)
        {
            map[child] = StateOf(child);
            CollectZoom(child, map);
        }
    }

    private static ZoomState StateOf(Control c)
    {
        return new ZoomState { Bounds = c.Bounds, Font = c.Font, Margin = c.Margin, Padding = c.Padding, Dock = c.Dock, AutoSize = c.AutoSize };
    }

    private static Font ZoomedFont(Font original, float f)
    {
        if (original == null || Math.Abs(f - 1F) < 0.001F) return original;
        float pt = Math.Max(6F, original.SizeInPoints * f);
        string key = original.FontFamily.Name + "|" + pt.ToString("0.##") + "|" + (int)original.Style;
        Font font;
        if (!zoomFonts.TryGetValue(key, out font))
        {
            font = new Font(original.FontFamily, pt, original.Style, GraphicsUnit.Point);
            zoomFonts[key] = font;
        }
        return font;
    }

    private static int Z(int value, float f) { return (int)Math.Round(value * f); }

    private static void ZoomControl(Control c, ZoomState z, float f)
    {
        if (c.IsDisposed) return;
        Font scaledFont = ZoomedFont(z.Font, f);
        if (scaledFont != null && !ReferenceEquals(c.Font, scaledFont)) c.Font = scaledFont;
        if (z.Dock == DockStyle.None)
        {
            if (z.AutoSize) c.Location = new Point(Z(z.Bounds.X, f), Z(z.Bounds.Y, f));
            else c.SetBounds(Z(z.Bounds.X, f), Z(z.Bounds.Y, f), Z(z.Bounds.Width, f), Z(z.Bounds.Height, f));
        }
        else if (z.Dock == DockStyle.Top || z.Dock == DockStyle.Bottom) c.Height = Z(z.Bounds.Height, f);
        else if (z.Dock == DockStyle.Left || z.Dock == DockStyle.Right) c.Width = Z(z.Bounds.Width, f);
        c.Margin = new Padding(Z(z.Margin.Left, f), Z(z.Margin.Top, f), Z(z.Margin.Right, f), Z(z.Margin.Bottom, f));
        if (c is ScrollableControl)
            c.Padding = new Padding(Z(z.Padding.Left, f), Z(z.Padding.Top, f), Z(z.Padding.Right, f), Z(z.Padding.Bottom, f));
    }

    // Controls created while the window was zoomed are not in the snapshot: scale them approximately.
    private void ScaleLooseControls(Control parent, float f)
    {
        foreach (Control child in parent.Controls)
        {
            if (zoomMap == null || !zoomMap.ContainsKey(child))
            {
                ZoomState state = StateOf(child);
                state.Font = null; // fonts created while zoomed were never scaled
                ZoomControl(child, state, f);
            }
            ScaleLooseControls(child, f);
        }
    }

    private void ApplyZoom(float f)
    {
        if (navigationPanel == null || pageContainer == null) return;
        SuspendLayout();
        navigationPanel.SuspendLayout();
        pageContainer.SuspendLayout();
        bool redrawOff = navigationPanel.IsHandleCreated && pageContainer.IsHandleCreated;
        if (redrawOff)
        {
            SendMessage(navigationPanel.Handle, 0x000B, 0, 0); // WM_SETREDRAW off
            SendMessage(pageContainer.Handle, 0x000B, 0, 0);
        }
        try
        {
            if (Math.Abs(f - 1F) > 0.001F)
            {
                if (zoomMap == null)
                {
                    zoomMap = new Dictionary<Control, ZoomState>();
                    CollectZoom(navigationPanel, zoomMap);
                    CollectZoom(pageContainer, zoomMap);
                }
                else if (Math.Abs(f - zoomFactor) > 0.001F)
                {
                    float ratio = f / zoomFactor;
                    ScaleLooseControls(navigationPanel, ratio);
                    ScaleLooseControls(pageContainer, ratio);
                }
                UiDpi.SetUserZoom(f);
                navigationPanel.Size = new Size(Z(baseNavSize.Width, f), Z(baseNavSize.Height, f));
                pageContainer.Size = new Size(Z(basePageSize.Width, f), Z(basePageSize.Height, f));
                foreach (KeyValuePair<Control, ZoomState> pair in zoomMap) ZoomControl(pair.Key, pair.Value, f);
                zoomFactor = f;
            }
            else
            {
                if (zoomMap != null)
                {
                    foreach (KeyValuePair<Control, ZoomState> pair in zoomMap) ZoomControl(pair.Key, pair.Value, 1F);
                    float back = 1F / zoomFactor;
                    ScaleLooseControls(navigationPanel, back);
                    ScaleLooseControls(pageContainer, back);
                    zoomMap = null;
                }
                UiDpi.SetUserZoom(1F);
                navigationPanel.Size = baseNavSize;
                pageContainer.Size = basePageSize;
                zoomFactor = 1F;
            }
        }
        finally
        {
            pageContainer.ResumeLayout(true);
            navigationPanel.ResumeLayout(true);
            ResumeLayout(true);
            if (redrawOff)
            {
                SendMessage(navigationPanel.Handle, 0x000B, 1, 0); // WM_SETREDRAW on
                SendMessage(pageContainer.Handle, 0x000B, 1, 0);
                navigationPanel.Invalidate(true);
                pageContainer.Invalidate(true);
            }
        }
        if (settingsPanel != null)
        {
            try { settingsPanel.RelayoutAfterDpi(); } catch { }
        }
        ReflowSerialCopyButtons();
    }

    // Re-measure serial number labels with their current (possibly zoomed or language-switched) font so
    // they never truncate with "..." when maximized and the Copy button stays snugly aligned on the same row.
    private void ReflowSerialCopyButtons()
    {
        if (overviewSerialLabel != null && overviewSerialCopyButton != null && overviewSerialLabel.Parent != null)
        {
            int maxW = Math.Max(UiDpi.S(60), overviewSerialLabel.Parent.ClientSize.Width - overviewSerialCopyButton.Width - UiDpi.S(6));
            int measuredW = TextRenderer.MeasureText(overviewSerialLabel.Text ?? String.Empty, overviewSerialLabel.Font).Width + UiDpi.S(4);
            int targetW = Math.Min(maxW, measuredW);
            overviewSerialLabel.Width = targetW;
            overviewSerialCopyButton.Left = overviewSerialLabel.Left + targetW + UiDpi.S(3);
            overviewSerialCopyButton.Top = overviewSerialLabel.Top + (overviewSerialLabel.Height - overviewSerialCopyButton.Height) / 2;
        }
        if (supportSerialLabel != null && supportCopyButton != null)
        {
            int maxW = UiDpi.S(260);
            int measuredW = TextRenderer.MeasureText(supportSerialLabel.Text ?? String.Empty, supportSerialLabel.Font).Width + UiDpi.S(4);
            int targetW = Math.Min(maxW, measuredW);
            supportSerialLabel.Width = targetW;
            supportCopyButton.Left = supportSerialLabel.Left + targetW + UiDpi.S(3);
            supportCopyButton.Top = supportSerialLabel.Top + (supportSerialLabel.Height - supportCopyButton.Height) / 2;
        }
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (!shellCaptured) return;
        if (WindowState != FormWindowState.Minimized)
        {
            try
            {
                float target = ComputeZoom();
                if (Math.Abs(target - zoomFactor) > 0.001F) ApplyZoom(target);
            }
            catch { }
            LayoutShell();
        }
    }

    [DllImport("user32.dll")]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr hWnd, int msg, int action, IntPtr changeInfo);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyTitleBarTheme(isDarkTheme);
        FlushPendingUiActions();
        // Let a second (possibly non-elevated) launch wake this elevated instance.
        try { ChangeWindowMessageFilterEx(Handle, Program.WmShowNitroHub, 1, IntPtr.Zero); }
        catch { }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0084) // WM_NCHITTEST
        {
            base.WndProc(ref m);
            if (WindowState == FormWindowState.Normal)
            {
                int hit = m.Result.ToInt32();
                // Prevent accidental manual edge-drag resizing while keeping WS_THICKFRAME active for Win11 Snap Layouts & Aero Snap.
                if (hit >= 10 && hit <= 17) // HTLEFT..HTBOTTOMRIGHT
                    m.Result = (IntPtr)1; // HTCLIENT
            }
            return;
        }
        if (m.Msg == Program.WmShowNitroHub ||
            (m.Msg == ModernSettingsPanel.WM_HOTKEY && m.WParam.ToInt32() == ModernSettingsPanel.HOTKEY_ID))
        {
            // Defer until WndProc returns so RestoreFromTray can safely restore visibility.
            try { BeginInvoke(new MethodInvoker(RestoreFromTray)); }
            catch { RestoreFromTray(); }
            return;
        }
        base.WndProc(ref m);
    }

    protected override void SetVisibleCore(bool value)
    {
        if (startMinimizedToTray && !initialHiddenApplied && value)
        {
            initialHiddenApplied = true;
            if (!IsHandleCreated) CreateHandle();
            value = false;
        }
        base.SetVisibleCore(value);
    }

    private void InitializeStartupRoutine()
    {
        if (startupInitialized) return;
        startupInitialized = true;

        // Sync saved firmware and scheduled-task state on a worker thread, then stagger UI setup on timer ticks.
        IntPtr ensureHandle = Handle;
        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                if (ReadSavedChargeLimitPreference().HasValue)
                    ApplySavedChargeLimitAtStartup();
            }
            catch { }
            try
            {
                if (IsStartupTaskInstalled())
                    EnsureChargeLimitStartupTask();
            }
            catch { }
            try { BeginInvoke(new MethodInvoker(RunStartupSteps)); }
            catch { }
        });
    }

    private void RunStartupSteps()
    {
        if (IsDisposed) return;
        List<MethodInvoker> steps = new List<MethodInvoker>();
        steps.Add(delegate { RefreshHardwareInventoryAsync(); });
        steps.Add(delegate { BeginLoadLaptopPhoto(); });
        steps.Add(delegate { InitializePowerPlanModes(); });
        steps.Add(delegate { RefreshStatus(); });
        steps.Add(delegate { RefreshBatteryCalibrationState(); });
        steps.Add(delegate { RefreshBatteryHealthSummary(); });
        steps.Add(delegate { InitializeLoadMonitoring(); });
        steps.Add(delegate { LoadSavedFanAndKeyboardSettings(); });
        steps.Add(delegate { RefreshTelemetry(); });
        steps.Add(delegate { CaptureKeyboardTimeoutState(); });
        steps.Add(delegate { RefreshPowerOffUsbChargingState(); });
        steps.Add(delegate { InitializeBluelightShield(); });
        steps.Add(delegate { UpdateTelemetryPollingRate(); telemetryTimer.Start(); });
        int index = 0;
        Timer stepTimer = new Timer { Interval = 30 };
        stepTimer.Tick += delegate
        {
            if (IsDisposed || index >= steps.Count)
            {
                stepTimer.Stop();
                stepTimer.Dispose();
                return;
            }
            MethodInvoker step = steps[index++];
            try { step(); } catch { }
            if (index >= steps.Count)
            {
                stepTimer.Stop();
                stepTimer.Dispose();
            }
        };
        stepTimer.Start();
    }

    private void UpdateTelemetryPollingRate()
    {
        if (telemetryTimer == null) return;
        bool isBackground = !Visible || WindowState == FormWindowState.Minimized;
        int targetInterval = isBackground ? 5000 : 2000;
        if (telemetryTimer.Interval != targetInterval)
            telemetryTimer.Interval = targetInterval;
    }

    private void HideToTray()
    {
        // Calling Hide() (SW_HIDE) already removes the window from the taskbar and Alt+Tab.
        // Avoid flipping ShowInTaskbar here: changing ShowInTaskbar forces WinForms RecreateHandle(),
        // which re-shows a Maximized window (SW_SHOWMAXIMIZED) and forces the user to click Close twice.
        Hide();
        UpdateTelemetryPollingRate();
    }

    private void RestoreFromTray()
    {
        if (!ShowInTaskbar) ShowInTaskbar = true;
        Show();
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
        TopMost = true;
        TopMost = false;
        UpdateTelemetryPollingRate();
        RefreshTelemetry();
    }

    private void ExitApplication()
    {
        allowExit = true;
        Close();
        Application.Exit();
    }

    private sealed class ThemeAwareTrayRenderer : ToolStripProfessionalRenderer
    {
        private readonly Func<bool> getDark;
        public ThemeAwareTrayRenderer(Func<bool> darkGetter) : base(new ThemeAwareColorTable(darkGetter))
        {
            getDark = darkGetter;
        }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            bool dark = getDark == null || getDark();
            if (!e.Item.Enabled)
                e.TextColor = dark ? Color.FromArgb(120, 132, 146) : Color.FromArgb(145, 155, 165);
            else
                e.TextColor = e.Item.Selected
                ? Color.White
                : (dark ? Color.FromArgb(236, 241, 246) : Color.FromArgb(28, 36, 46));
            base.OnRenderItemText(e);
        }
        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            bool dark = getDark == null || getDark();
            Color line = dark ? Color.FromArgb(56, 66, 78) : Color.FromArgb(222, 228, 235);
            int y = e.Item.Height / 2;
            using (Pen pen = new Pen(line, 1F))
                e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
        }
    }

    private sealed class ThemeAwareColorTable : ProfessionalColorTable
    {
        private readonly Func<bool> getDark;
        public ThemeAwareColorTable(Func<bool> darkGetter) { getDark = darkGetter; }
        private bool Dark { get { return getDark == null || getDark(); } }

        public override Color ToolStripDropDownBackground { get { return Dark ? Color.FromArgb(30, 36, 44) : Color.White; } }
        public override Color ImageMarginGradientBegin { get { return Dark ? Color.FromArgb(30, 36, 44) : Color.FromArgb(245, 247, 250); } }
        public override Color ImageMarginGradientMiddle { get { return Dark ? Color.FromArgb(30, 36, 44) : Color.FromArgb(245, 247, 250); } }
        public override Color ImageMarginGradientEnd { get { return Dark ? Color.FromArgb(30, 36, 44) : Color.FromArgb(245, 247, 250); } }
        public override Color MenuBorder { get { return Dark ? Color.FromArgb(62, 74, 88) : Color.FromArgb(205, 212, 220); } }
        public override Color MenuItemBorder { get { return Color.FromArgb(210, 48, 66); } }
        public override Color MenuItemSelected { get { return Color.FromArgb(210, 48, 66); } }
        public override Color MenuItemSelectedGradientBegin { get { return Color.FromArgb(210, 48, 66); } }
        public override Color MenuItemSelectedGradientEnd { get { return Color.FromArgb(182, 38, 56); } }
        public override Color MenuStripGradientBegin { get { return Dark ? Color.FromArgb(30, 36, 44) : Color.White; } }
        public override Color MenuStripGradientEnd { get { return Dark ? Color.FromArgb(30, 36, 44) : Color.White; } }
        public override Color CheckBackground { get { return Dark ? Color.FromArgb(52, 62, 74) : Color.FromArgb(232, 238, 245); } }
        public override Color CheckSelectedBackground { get { return Color.FromArgb(210, 48, 66); } }
    }

    private void ApplyTrayTheme()
    {
        if (trayMenu == null) return;
        trayMenu.BackColor = isDarkTheme ? Color.FromArgb(30, 36, 44) : Color.White;
        trayMenu.ForeColor = isDarkTheme ? Color.FromArgb(236, 241, 246) : Color.FromArgb(28, 36, 46);
        trayMenu.Invalidate();
    }

    private void InitializeSystemTray()
    {
        trayMenu = new ContextMenuStrip
        {
            Renderer = new ThemeAwareTrayRenderer(delegate { return isDarkTheme; }),
            ShowImageMargin = true
        };
        ApplyTrayTheme();
        trayMenu.Opening += delegate { ApplyTrayTheme(); RebuildTrayContextMenu(); };

        trayIcon = new NotifyIcon
        {
            Icon = Icon ?? SystemIcons.Application,
            Text = "NitroHub",
            ContextMenuStrip = trayMenu,
            Visible = true
        };
        trayIcon.DoubleClick += delegate { RestoreFromTray(); };
        RebuildTrayContextMenu();
    }

    private Font trayOpenItemFont;
    private Font trayMenuFont;

    private void RebuildTrayContextMenu()
    {
        // Clear() does not dispose the removed items, so dispose them explicitly (this also disposes their drop-downs).
        List<ToolStripItem> oldItems = new List<ToolStripItem>();
        foreach (ToolStripItem oldItem in trayMenu.Items) oldItems.Add(oldItem);
        trayMenu.Items.Clear();
        foreach (ToolStripItem oldItem in oldItems)
        {
            try { oldItem.Dispose(); } catch { }
        }
        if (trayOpenItemFont == null) trayOpenItemFont = new Font("Segoe UI Semibold", 9F);

        ToolStripMenuItem openItem = new ToolStripMenuItem(Tr("Open NitroHub", "打开 NitroHub")) { Font = trayOpenItemFont };
        openItem.Click += delegate { RestoreFromTray(); };
        trayMenu.Items.Add(openItem);
        if (!String.IsNullOrEmpty(lastTrayStatusLine))
        {
            ToolStripMenuItem statusItem = new ToolStripMenuItem(lastTrayStatusLine) { Enabled = false };
            trayMenu.Items.Add(statusItem);
        }
        trayMenu.Items.Add(new ToolStripSeparator());

        // Use the last known state (kept fresh by RefreshStatus) and refresh it in the background for the next time the menu opens.
        bool chargeLimitOn = cachedChargeLimitOn ?? false;
        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            try { cachedChargeLimitOn = ReadChargeLimitState(); } catch { }
        });
        ToolStripMenuItem chargeLimitItem = new ToolStripMenuItem(Tr("Battery 80% Charge Limit", "电池 80% 充电上限")) { Checked = chargeLimitOn };
        chargeLimitItem.Click += delegate { ApplyLimit(!chargeLimitOn); };
        trayMenu.Items.Add(chargeLimitItem);

        ToolStripMenuItem fanMenu = new ToolStripMenuItem(Tr("Fan Control", "风扇控制"));
        ToolStripMenuItem fanAutoItem = new ToolStripMenuItem(Tr("Auto Mode", "自动模式")) { Checked = currentFanMode == 1 };
        fanAutoItem.Click += delegate { SetFanMode(1); };
        ToolStripMenuItem fanMaxItem = new ToolStripMenuItem(Tr("Max Cooling", "强冷模式")) { Checked = currentFanMode == 2 };
        fanMaxItem.Click += delegate { SetFanMode(2); };
        // Use the speeds that were last applied with the Apply button, not slider positions that were never applied.
        int trayCpuSpeed = appliedCustomCpuSpeed >= 0 ? appliedCustomCpuSpeed : cpuFanTrack.Value;
        int trayGpuSpeed = appliedCustomGpuSpeed >= 0 ? appliedCustomGpuSpeed : gpuFanTrack.Value;
        ToolStripMenuItem fanCustomItem = new ToolStripMenuItem(Tr("Custom", "自定义") + " (" + trayCpuSpeed + "% / " + trayGpuSpeed + "%)") { Checked = currentFanMode == 0 };
        fanCustomItem.Click += delegate { SetCustomFanSpeeds((byte)trayCpuSpeed, (byte)trayGpuSpeed); };
        fanMenu.DropDownItems.AddRange(new ToolStripItem[] { fanAutoItem, fanMaxItem, fanCustomItem });
        trayMenu.Items.Add(fanMenu);

        bool isBattery = SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline;
        string sourceLabel = isBattery ? Tr("Power Plan (On Battery)", "电源计划（电池）") : Tr("Power Plan (Plugged In)", "电源计划（插电）");
        string currentScheme = GetConfiguredPowerPlan(isBattery);
        ToolStripMenuItem powerMenu = new ToolStripMenuItem(sourceLabel);
        string[] planLabels = new string[] { Tr("Power Saver", "节能模式"), Tr("Balanced", "平衡模式"), Tr("High Performance", "高性能") };
        for (int i = 0; i < PowerPlanSchemes.Length; i++)
        {
            int idx = i;
            ToolStripMenuItem planItem = new ToolStripMenuItem(planLabels[i])
            {
                Checked = String.Equals(currentScheme, PowerPlanSchemes[i], StringComparison.OrdinalIgnoreCase)
            };
            planItem.Click += delegate
            {
                showingBatteryPlans = isBattery;
                SelectPowerPlan(idx);
            };
            powerMenu.DropDownItems.Add(planItem);
        }
        trayMenu.Items.Add(powerMenu);

        if (keyboardEffectControlAvailable && effectCombo != null && effectCombo.Items.Count > 0)
        {
            ToolStripMenuItem kbMenu = new ToolStripMenuItem(Tr("Keyboard Lighting", "键盘灯效"));
            for (int i = 0; i < effectCombo.Items.Count; i++)
            {
                int idx = i;
                KeyboardEffectProfile profile = (KeyboardEffectProfile)effectCombo.Items[i];
                ToolStripMenuItem effItem = new ToolStripMenuItem(profile.Name)
                {
                    Checked = effectCombo.SelectedIndex == idx && brightnessTrack.Value > 0
                };
                effItem.Click += delegate
                {
                    if (brightnessTrack.Value == 0) brightnessTrack.Value = 70;
                    effectCombo.SelectedIndex = idx;
                    ApplyKeyboardSettings();
                };
                kbMenu.DropDownItems.Add(effItem);
            }
            kbMenu.DropDownItems.Add(new ToolStripSeparator());
            ToolStripMenuItem kbOffItem = new ToolStripMenuItem(Tr("Turn Off Backlight", "关闭背光")) { Checked = brightnessTrack.Value == 0 };
            kbOffItem.Click += delegate
            {
                brightnessTrack.Value = 0;
                ApplyKeyboardSettings();
            };
            kbMenu.DropDownItems.Add(kbOffItem);
            trayMenu.Items.Add(kbMenu);
        }

        if (usbChargeToggle != null && usbChargeToggle.Enabled)
        {
            ToolStripMenuItem usbMenu = new ToolStripMenuItem(TrKey("Power-off USB Charging"));
            string[] usbLabels = new string[] { TrKey("Off"), TrKey("On (Stop at 10%)"), TrKey("On (Stop at 20%)"), TrKey("On (Stop at 30%)") };
            int currentUsbIdx = usbChargeToggle.Checked ? (usbChargeSegment.SelectedIndex + 1) : 0;
            for (int i = 0; i < usbLabels.Length; i++)
            {
                int idx = i;
                ToolStripMenuItem usbItem = new ToolStripMenuItem(usbLabels[i]) { Checked = currentUsbIdx == idx };
                usbItem.Click += delegate { SetPowerOffUsbChargingFromUi(idx); };
                usbMenu.DropDownItems.Add(usbItem);
            }
            trayMenu.Items.Add(usbMenu);
        }

        ToolStripMenuItem blueMenu = new ToolStripMenuItem(TrKey("Bluelight Shield"));
        for (int i = 0; i < BluelightShield.LevelCount; i++)
        {
            int lvl = i;
            ToolStripMenuItem blueItem = new ToolStripMenuItem(TrKey(BluelightShield.GetName(lvl))) { Checked = bluelightLevel == lvl };
            blueItem.Click += delegate { SetBluelightLevelFromUi(lvl); };
            blueMenu.DropDownItems.Add(blueItem);
        }
        trayMenu.Items.Add(blueMenu);

        trayMenu.Items.Add(new ToolStripSeparator());
        ToolStripMenuItem exitItem = new ToolStripMenuItem(Tr("Exit", "退出"));
        exitItem.Click += delegate { ExitApplication(); };
        trayMenu.Items.Add(exitItem);
    }

    private static Font uniformButtonFont;

    private static void ApplyUniformButtonMetrics(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            Button button = control as Button;
            bool customSizeButton = button != null &&
                (button.Name == "power-source" || button.Name == "power-plan-option" ||
                button.Name == "ac-power-plan-option" || button.Name == "battery-power-plan-option" ||
                button.Name == "support-link" || button.Name == "overview-copy" ||
                button.Name == "fan-mode" || button.Name == "fan-custom-apply" ||
                button.Name == "settings-action");
            if (button != null && Convert.ToString(button.Tag) != "navigation" && !customSizeButton)
            {
                button.Width = UiDpi.S(140);
                button.Height = UiDpi.S(ActionButtonHeight);
                if (uniformButtonFont == null) uniformButtonFont = new Font("Segoe UI Semibold", ActionButtonFontSize);
                button.Font = uniformButtonFont;
            }
            ApplyUniformButtonMetrics(control);
        }
    }

    // Text controls take no background of their own, so nothing flashes behind the text while the theme changes.
    private static void MakeTextBackgroundsTransparent(Control parent)
    {
        foreach (Control c in parent.Controls)
        {
            if (c is Label || c is CheckBox || c is RadioButton)
                c.BackColor = Color.Transparent;
            MakeTextBackgroundsTransparent(c);
        }
    }

    private static void ConvertPanelsToRounded(Control parent)
    {
        for (int i = 0; i < parent.Controls.Count; i++)
        {
            Control control = parent.Controls[i];

            Panel panel = control as Panel;
            if (panel != null && !(panel is RoundedPanel) && !(panel is VersionBadgePanel) &&
                !(panel is FlowLayoutPanel) && !(parent is FlowLayoutPanel))
            {
                string tag = Convert.ToString(panel.Tag);
                bool shouldRound = tag == "card" || tag == "status-card" || tag == "graphics-support-panel";
                if (shouldRound && panel.BackColor != Color.Transparent)
                {
                    int radius = (tag == "status-card") ? UiDpi.S(8) : UiDpi.S(12);
                    RoundedPanel rounded = new RoundedPanel
                    {
                        Name = panel.Name,
                        Location = panel.Location,
                        Size = panel.Size,
                        Anchor = panel.Anchor,
                        Dock = panel.Dock,
                        BackColor = panel.BackColor,
                        Tag = panel.Tag,
                        Visible = true,
                        Radius = radius
                    };
                    Control[] children = new Control[panel.Controls.Count];
                    panel.Controls.CopyTo(children, 0);
                    panel.Controls.Clear();
                    rounded.Controls.AddRange(children);

                    parent.Controls.RemoveAt(i);
                    panel.Dispose();
                    parent.Controls.Add(rounded);
                    parent.Controls.SetChildIndex(rounded, i);
                    control = rounded;
                }
            }
            ConvertPanelsToRounded(control);
        }
    }

    private static Image LoadHeaderLogo()
    {
        Stream stream = typeof(ChargeLimitForm).Assembly.GetManifestResourceStream("NitroHubLogo.png");
        if (stream == null) return null;
        using (stream)
        using (Bitmap source = new Bitmap(stream))
            return new Bitmap(source);
    }

    internal static string GetApplicationVersion()
    {
        Assembly assembly = typeof(Program).Assembly;
        AssemblyInformationalVersionAttribute info =
            (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
            assembly, typeof(AssemblyInformationalVersionAttribute));
        string version = info == null
            ? assembly.GetName().Version.ToString(3)
            : info.InformationalVersion;
        return "v" + version;
    }

    private void UpdateNavigationStyles()
    {
        for (int i = 0; i < navigationButtons.Length; i++)
        {
            Button button = navigationButtons[i];
            if (button == null) continue;
            bool selected = i == currentPageIndex;
            button.BackColor = selected ? Teal : (isDarkTheme ? Color.FromArgb(34, 41, 49) : Color.FromArgb(244, 247, 251));
            button.ForeColor = selected ? Color.White : (isDarkTheme ? Color.FromArgb(222, 229, 236) : Color.FromArgb(55, 68, 82));
            button.FlatAppearance.MouseOverBackColor = isDarkTheme ? Color.FromArgb(54, 63, 73) : Color.FromArgb(231, 237, 243);
        }
        if (navigationPanel != null) navigationPanel.Invalidate();
    }

    private void RefreshStatus()
    {
        bool enabled = false;
        RunHardwareAsync(delegate { enabled = ReadChargeLimitState(); }, delegate(Exception ex)
        {
            ApplyChargeLimitStatusUi(enabled, ex, null);
        });
    }

    private void ApplyChargeLimitStatusUi(bool enabled, Exception ex, string detailOverride)
    {
        if (ex == null)
        {
            cachedChargeLimitOn = enabled;
            if (!originalChargeLimitState.HasValue) originalChargeLimitState = enabled;
            stateTitle.Text = enabled ? "Protection is ON" : "Protection is OFF";
            stateTitle.ForeColor = enabled ? ProtectionGreen : (isDarkTheme ? Color.FromArgb(239, 243, 248) : Color.FromArgb(39, 57, 74));
            chargeStatusAccent.BackColor = enabled ? ProtectionGreen : Teal;
            stateDetail.Text = !String.IsNullOrEmpty(detailOverride)
                ? detailOverride
                : (enabled ? "Charging should stop at about 80%." : "The battery may charge up to 100%.");
            enableButton.Enabled = true;
            disableButton.Enabled = true;
            enableButton.Tag = enabled ? "accent" : "state-off";
            disableButton.Tag = enabled ? "state-off" : "accent";
            ApplyStateButtonAppearance(enableButton);
            ApplyStateButtonAppearance(disableButton);
        }
        else
        {
            stateTitle.Text = "Status unavailable";
            stateTitle.ForeColor = LightError;
            chargeStatusAccent.BackColor = LightError;
            stateDetail.Text = ex.Message;
            enableButton.Enabled = false;
            disableButton.Enabled = false;
        }
    }

    // WMI/firmware writes run one at a time, in the order requested, on a worker thread so the UI never blocks on slow hardware.
    private readonly Queue<Action> hardwareQueue = new Queue<Action>();
    private bool hardwareWorkerRunning;
    private bool hardwareShuttingDown;
    private readonly object hardwareExecLock = new object();
    private readonly object counterLock = new object();
    private volatile bool fanTouched; // set on the worker thread, before a non-automatic fan mode is written
    private volatile bool uiClosing; // the window is really closing: worker results must no longer touch the UI
    private bool fanExitResetDone;
    private readonly List<Action> pendingUiActions = new List<Action>();

    // The only way worker threads (and system-event threads) reach the UI. It never throws, drops work once the window is closing,
    // and keeps work posted while the handle is being recreated (hiding to the tray) until the new handle exists.
    private void PostToUi(Action action)
    {
        if (uiClosing) return;
        try
        {
            if (IsDisposed || Disposing) return;
            if (!IsHandleCreated)
            {
                lock (pendingUiActions) pendingUiActions.Add(action);
                return;
            }
            BeginInvoke((MethodInvoker)delegate
            {
                if (uiClosing || IsDisposed) return;
                action();
            });
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException)
        {
            lock (pendingUiActions) pendingUiActions.Add(action);
        }
    }

    private void FlushPendingUiActions()
    {
        Action[] items;
        lock (pendingUiActions)
        {
            if (pendingUiActions.Count == 0) return;
            items = pendingUiActions.ToArray();
            pendingUiActions.Clear();
        }
        try
        {
            BeginInvoke((MethodInvoker)delegate
            {
                foreach (Action item in items)
                {
                    if (uiClosing || IsDisposed) return;
                    try { item(); } catch { }
                }
            });
        }
        catch { }
    }

    private void RunHardwareAsync(Action work, Action<Exception> completed)
    {
        EnqueueHardwareJob(delegate
        {
            Exception error = null;
            try { work(); }
            catch (Exception ex) { error = ex; }
            PostToUi(delegate { completed(error); });
        }, false);
    }

    private bool EnqueueHardwareJob(Action job, bool final)
    {
        lock (hardwareQueue)
        {
            if (hardwareShuttingDown && !final) return false;
            if (final) hardwareShuttingDown = true;
            hardwareQueue.Enqueue(job);
            if (hardwareWorkerRunning) return true;
            hardwareWorkerRunning = true;
        }
        System.Threading.ThreadPool.QueueUserWorkItem(delegate { HardwareWorkerLoop(); });
        return true;
    }

    private void HardwareWorkerLoop()
    {
        while (true)
        {
            Action next;
            lock (hardwareQueue)
            {
                if (hardwareQueue.Count == 0) { hardwareWorkerRunning = false; return; }
                next = hardwareQueue.Dequeue();
            }
            lock (hardwareExecLock)
            {
                try { next(); } catch { }
            }
        }
    }

    // Puts the fans back to automatic as the LAST hardware job. It goes through the same queue as every other write, so a custom-speed
    // command that is still running or waiting can never run after (and undo) the reset, and nothing new is accepted afterwards.
    private void ResetFanToAutoBeforeExit()
    {
        if (fanExitResetDone) return;
        fanExitResetDone = true;
        bool needReset = fanControlChanged || fanTouched;
        System.Threading.ManualResetEvent done = new System.Threading.ManualResetEvent(false);
        bool queued = EnqueueHardwareJob(delegate
        {
            try
            {
                if (needReset)
                {
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        try { WriteFanMode(1); break; }
                        catch { System.Threading.Thread.Sleep(200); }
                    }
                }
            }
            finally { done.Set(); }
        }, true);
        // Do not leave the window frozen for long when the firmware is slow: 3 s is enough for a normal fan reset.
        if (queued) done.WaitOne(3000);
        fanControlChanged = false;
    }

    private void ApplyLimit(bool enabled)
    {
        enableButton.Enabled = false;
        disableButton.Enabled = false;
        stateDetail.Text = "Applying setting to Acer firmware...";
        string startupTaskError = null;
        bool confirmedState = enabled;
        RunHardwareAsync(delegate
        {
            WriteChargeLimitState(enabled);
            SaveChargeLimitPreference(enabled);
            startupTaskError = EnsureChargeLimitStartupTask();
            confirmedState = ReadChargeLimitState();
        }, delegate(Exception ex)
        {
            if (ex == null)
            {
                try
                {
                    string detailOverride = !String.IsNullOrEmpty(startupTaskError)
                        ? (enabled
                        ? "80% limit is active, but automatic sign-in restore could not be set up."
                        : "100% charging is active, but automatic sign-in restore could not be set up.")
                        : null;
                    ApplyChargeLimitStatusUi(confirmedState, null, detailOverride);
                }
                catch (Exception uiEx) { ex = uiEx; }
            }
            if (ex != null)
            {
                stateTitle.Text = "Setting failed";
                stateTitle.ForeColor = LightError;
                stateDetail.Text = ex.Message;
                enableButton.Enabled = true;
                disableButton.Enabled = true;
            }
        });
    }

    private void CreateBatteryHealthReport()
    {
        if (batteryReportBusy) return;
        batteryReportBusy = true;
        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                string folder = Path.Combine(GetAppDataDirectory(), "reports");
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, "battery-health-report.html");
                ProcessStartInfo start = new ProcessStartInfo
                {
                    FileName = "powercfg.exe",
                    Arguments = "/batteryreport /output \"" + path + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (Process process = Process.Start(start))
                {
                    if (process == null) throw new InvalidOperationException("Windows could not start powercfg.");
                    if (!process.WaitForExit(15000))
                    {
                        try { process.Kill(); } catch { }
                        throw new InvalidOperationException("Windows battery report generation timed out.");
                    }
                    if (process.ExitCode != 0 || !File.Exists(path))
                        throw new InvalidOperationException("Windows could not create the battery report. Check that this device has a supported battery.");
                }
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                string message = ex.Message;
                PostToUi(delegate
                {
                    MessageBox.Show(message, Tr("Battery health report unavailable", "电池健康报告不可用"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                });
            }
            finally
            {
                batteryReportBusy = false;
            }
        });
    }

    private void RefreshBatteryHealthSummary()
    {
        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            ulong designCapacity = 0;
            ulong fullCapacity = 0;
            List<string> batteryNames = new List<string>();
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    new ManagementScope("root\\WMI"), new ObjectQuery("SELECT * FROM BatteryStaticData"), WmiOpts.Create()))
                    using (ManagementObjectCollection items = searcher.Get())
                {
                    foreach (ManagementObject item in items)
                        using (item)
                    {
                        object capacity = GetBatteryProperty(item, "DesignedCapacity");
                        if (capacity != null) designCapacity += Convert.ToUInt64(capacity);
                        string deviceName = Convert.ToString(GetBatteryProperty(item, "DeviceName"));
                        string manufacturer = Convert.ToString(GetBatteryProperty(item, "ManufactureName"));
                        string name = String.IsNullOrWhiteSpace(deviceName) ? manufacturer : deviceName;
                        if (!String.IsNullOrWhiteSpace(name) && !batteryNames.Contains(name)) batteryNames.Add(name);
                    }
                }
            }
            catch { }
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                    new ManagementScope("root\\WMI"), new ObjectQuery("SELECT * FROM BatteryFullChargedCapacity"), WmiOpts.Create()))
                    using (ManagementObjectCollection items = searcher.Get())
                {
                    foreach (ManagementObject item in items)
                        using (item)
                    {
                        object capacity = GetBatteryProperty(item, "FullChargedCapacity");
                        if (capacity != null) fullCapacity += Convert.ToUInt64(capacity);
                    }
                }
            }
            catch { }

            // Some battery drivers expose capacities through Win32_Battery instead of root\\WMI.
            if (designCapacity == 0 || fullCapacity == 0 || batteryNames.Count == 0)
            {
                try
                {
                    using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                        new ManagementScope(@"root\CIMV2"),
                        new ObjectQuery("SELECT Name, Caption, DesignCapacity, FullChargeCapacity FROM Win32_Battery"),
                        WmiOpts.Create()))
                    using (ManagementObjectCollection items = searcher.Get())
                    {
                        foreach (ManagementObject item in items)
                            using (item)
                        {
                            if (designCapacity == 0)
                            {
                                object capacity = GetBatteryProperty(item, "DesignCapacity");
                                if (capacity != null) designCapacity += Convert.ToUInt64(capacity);
                            }
                            if (fullCapacity == 0)
                            {
                                object capacity = GetBatteryProperty(item, "FullChargeCapacity");
                                if (capacity != null) fullCapacity += Convert.ToUInt64(capacity);
                            }
                            string name = Convert.ToString(GetBatteryProperty(item, "Name"));
                            if (String.IsNullOrWhiteSpace(name)) name = Convert.ToString(GetBatteryProperty(item, "Caption"));
                            if (!String.IsNullOrWhiteSpace(name) && !batteryNames.Contains(name)) batteryNames.Add(name);
                        }
                    }
                }
                catch { }
            }

            string model = batteryNames.Count == 0 ? "Not reported by firmware" : String.Join(", ", batteryNames.ToArray());
            string summary;
            if (designCapacity > 0 && fullCapacity > 0)
            {
                double health = fullCapacity * 100.0 / designCapacity;
                summary = "Battery model: " + model + Environment.NewLine + Environment.NewLine +
                    "Estimated health: " + health.ToString("0.0") + "%  (" + fullCapacity.ToString("N0") + " / " + designCapacity.ToString("N0") + " mWh)";
            }
            else
            {
                summary = "Battery model: " + model + Environment.NewLine + Environment.NewLine +
                    "Estimated health: unavailable (Windows did not report both capacities).";
            }
            PostToUi(delegate { batteryHealthSummaryLabel.Text = summary; });
        });
    }

    private static object GetBatteryProperty(ManagementBaseObject item, string propertyName)
    {
        if (item == null || item.Properties[propertyName] == null) return null;
        return item[propertyName];
    }

    private void SetFanMode(byte mode)
    {
        bool quiet = restoringSavedFanSettings; // captured now: the startup flag is cleared before the async result arrives
        RunHardwareAsync(delegate
        {
            if (!IsAcerGamingInterfaceAvailable())
                throw new InvalidOperationException(MissingInterfaceMessage("Fan control", "gaming WMI"));
            if (mode != 1) fanTouched = true;
            WriteFanMode(mode);
        }, delegate(Exception ex)
        {
            if (ex == null)
            {
                try
                {
                    fanControlChanged = mode != 1;
                    if (mode == 1)
                    {
                        cpuFanTrack.Value = 70;
                        gpuFanTrack.Value = 70;
                    }
                    else if (mode == 2)
                    {
                        cpuFanTrack.Value = 100;
                        gpuFanTrack.Value = 100;
                    }
                    UpdateFanModeButtons(mode);
                    SaveFanAndKeyboardSettings();
                }
                catch (Exception uiEx) { ex = uiEx; }
            }
            if (ex != null)
            {
                if (quiet) WriteChargeLimitStartupLog("Could not restore saved fan mode: " + ex.Message);
                else MessageBox.Show(ex.Message, Tr("Fan control unavailable", "风扇控制不可用"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        });
    }

    // A page's native controls are only created the first time it is shown, which made the first visit to each page stutter.
    // Create them in the background, one page per tick, after startup.
    private void StartPagePrewarm()
    {
        if (pageList == null) return;
        int next = 0;
        Timer prewarm = new Timer { Interval = 220 };
        prewarm.Tick += delegate
        {
            while (next < pageList.Length && next == currentPageIndex) next++;
            if (next >= pageList.Length || IsDisposed)
            {
                prewarm.Stop();
                prewarm.Dispose();
                return;
            }
            try { CreateChildHandles(pageList[next]); } catch { }
            next++;
        };
        prewarm.Start();
    }

    private static void CreateChildHandles(Control control)
    {
        if (!control.IsHandleCreated) { IntPtr ignored = control.Handle; }
        foreach (Control child in control.Controls) CreateChildHandles(child);
    }

    private void RestoreAutoFanModeAtStartup()
    {
        RunHardwareAsync(delegate
        {
            if (!IsAcerGamingInterfaceAvailable()) return;
            WriteFanMode(1);
        }, delegate(Exception ex)
        {
            if (ex != null) { WriteChargeLimitStartupLog("Could not restore automatic fan mode: " + ex.Message); return; }
            try { UpdateFanModeButtons(1); } catch { }
        });
    }

    private static void WriteFanMode(byte mode)
    {
        using (ManagementObject device = GetGamingFunction())
        using (ManagementBaseObject input = device.GetMethodParameters("SetGamingFanBehavior"))
        {
            // Acer WMI bitmap: CPU fan bit 0, GPU fan bit 3; mode fields at 16 and 22.
            ulong packed = 0x09UL | ((ulong)mode << 16) | ((ulong)mode << 22);
            input["gmInput"] = packed;
            using (ManagementBaseObject result = device.InvokeMethod("SetGamingFanBehavior", input, null))
            {
                if (result == null || result["gmOutput"] == null || Convert.ToUInt64(result["gmOutput"]) != 0)
                    throw new InvalidOperationException("Acer firmware did not confirm the fan setting.");
            }
        }
    }

    private static ManagementObject GetGamingFunction()
    {
        ManagementObjectSearcher searcher = new ManagementObjectSearcher(
            new ManagementScope("root\\WMI"), new ObjectQuery("SELECT * FROM AcerGamingFunction"), WmiOpts.Create());
        using (searcher)
        using (ManagementObjectCollection items = searcher.Get())
        {
            foreach (ManagementObject item in items)
                return (ManagementObject)item.Clone();
        }
        throw new InvalidOperationException("This computer does not expose Acer's gaming WMI interface.");
    }

    private void SetCustomFanSpeeds()
    {
        SetCustomFanSpeeds((byte)cpuFanTrack.Value, (byte)gpuFanTrack.Value);
    }

    private void SetCustomFanSpeeds(byte cpuSpeed, byte gpuSpeed)
    {
        bool quiet = restoringSavedFanSettings;
        bool customModeWritten = false;
        RunHardwareAsync(delegate
        {
            if (!IsAcerGamingInterfaceAvailable())
                throw new InvalidOperationException(MissingInterfaceMessage("Custom fan control", "gaming WMI"));
            fanTouched = true;

            using (ManagementObject device = GetGamingFunction())
            using (ManagementBaseObject input = device.GetMethodParameters("SetGamingFanBehavior"))
            {
                ulong packed = 0x09UL | (3UL << 16) | (3UL << 22);
                input["gmInput"] = packed;
                using (ManagementBaseObject result = device.InvokeMethod("SetGamingFanBehavior", input, null))
                {
                    if (result == null || result["gmOutput"] == null || Convert.ToUInt64(result["gmOutput"]) != 0)
                        throw new InvalidOperationException("Firmware rejected custom fan mode.");
                }
            }

            customModeWritten = true;
            SetFanSpeed(0x01, cpuSpeed);
            SetFanSpeed(0x04, gpuSpeed);
        }, delegate(Exception ex)
        {
            if (ex == null)
            {
                try
                {
                    fanControlChanged = true;
                    appliedCustomCpuSpeed = cpuSpeed;
                    appliedCustomGpuSpeed = gpuSpeed;
                    cpuFanTrack.Value = cpuSpeed;
                    gpuFanTrack.Value = gpuSpeed;
                    UpdateFanModeButtons(0);
                    SaveFanAndKeyboardSettings();
                }
                catch (Exception uiEx) { ex = uiEx; }
            }
            if (ex != null)
            {
                if (fanControlChanged || customModeWritten)
                {
                    // Do not leave the fans in a half-applied custom mode: go back to automatic.
                    fanControlChanged = false;
                    RunHardwareAsync(delegate { WriteFanMode(1); }, delegate(Exception ignored) { });
                }
                if (quiet) WriteChargeLimitStartupLog("Could not restore saved custom fan speeds: " + ex.Message);
                else MessageBox.Show(ex.Message, Tr("Custom fan control unavailable", "自定义风扇控制不可用"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        });
    }

    private static void SetFanSpeed(byte fanId, byte speed)
    {
        using (ManagementObject device = GetGamingFunction())
        using (ManagementBaseObject input = device.GetMethodParameters("SetGamingFanSpeed"))
        {
            input["gmInput"] = (ulong)(fanId | ((ulong)speed << 8));
            using (ManagementBaseObject result = device.InvokeMethod("SetGamingFanSpeed", input, null))
            {
                if (result == null || result["gmOutput"] == null || (Convert.ToUInt64(result["gmOutput"]) & 0xFF) != 0)
                    throw new InvalidOperationException("Firmware rejected a fan speed setting.");
            }
        }
    }

    private Label AddMonitorStatusCard(Panel parent, string caption, int x, int y, int width, Color valueColor)
    {
        Panel statusCard = new Panel
        {
            Location = new Point(x, y), Size = new Size(width, 64),
            BackColor = Color.FromArgb(43, 51, 61), BorderStyle = BorderStyle.None, Tag = "status-card"
        };
        statusCard.Controls.Add(new Label
        {
            Text = caption, Location = new Point(12, 7), Size = new Size(width - 24, 17),
            Font = new Font("Segoe UI Semibold", 8.5F), ForeColor = SharedHeadingColor, Tag = "section-heading"
        });
        Label value = new Label
        {
            Text = "Reading...", Location = new Point(12, 28), Size = new Size(width - 24, 25),
            Font = new Font("Segoe UI", 10F), ForeColor = valueColor, AutoEllipsis = true
        };
        statusCard.Controls.Add(value);
        parent.Controls.Add(statusCard);
        return value;
    }

    private sealed class TelemetrySnapshot
    {
        public string Cpu, Gpu, CpuFan, GpuFan, System, Memory, CpuLoad, GpuLoad;
        public string CpuCelsius, GpuCelsius, SystemCelsius;
        public float? CpuUsage, GpuUsage;
        public Exception Error;
    }

    private int telemetryRefreshPending;

    private void RefreshTelemetry()
    {
        if (System.Threading.Interlocked.CompareExchange(ref telemetryRefreshPending, 1, 0) != 0) return;
        bool fahrenheit = useFahrenheit;
        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            TelemetrySnapshot snapshot = new TelemetrySnapshot();
            try
            {
                // Read-only monitor: no model allow-list. The Acer WMI interface and the firmware's own sensor map decide what is shown.
                ulong sensorMap = ReadGamingSystemInfo(0);
                uint supported = (uint)((sensorMap >> 24) & 0xFFFF);
                snapshot.Cpu = ReadSensor(supported, 0x01, "CPU", "°C");
                snapshot.CpuFan = ReadSensor(supported, 0x02, "CPU fan", " RPM");
                snapshot.System = ReadSensor(supported, 0x03, "System", "°C");
                snapshot.GpuFan = ReadSensor(supported, 0x06, "GPU fan", " RPM");
                snapshot.Gpu = ReadSensor(supported, 0x0A, "GPU", "°C");
                snapshot.CpuCelsius = snapshot.Cpu;
                snapshot.GpuCelsius = snapshot.Gpu;
                snapshot.SystemCelsius = snapshot.System;
                snapshot.Memory = ReadMemoryLoad();
                snapshot.CpuLoad = ReadCpuLoad(out snapshot.CpuUsage);
                snapshot.GpuLoad = ReadGpuLoad(out snapshot.GpuUsage);
                if (fahrenheit)
                {
                    snapshot.Cpu = ConvertTemperatureToFahrenheit(snapshot.Cpu);
                    snapshot.Gpu = ConvertTemperatureToFahrenheit(snapshot.Gpu);
                    snapshot.System = ConvertTemperatureToFahrenheit(snapshot.System);
                }
            }
            catch (Exception ex) { snapshot.Error = ex; }

            if (uiClosing) { System.Threading.Interlocked.Exchange(ref telemetryRefreshPending, 0); return; }
            PostToUi(delegate
            {
                try { ApplyTelemetrySnapshot(snapshot); }
                finally { System.Threading.Interlocked.Exchange(ref telemetryRefreshPending, 0); }
            });
        });
    }

    private void ApplyTelemetrySnapshot(TelemetrySnapshot snapshot)
    {
        if (IsDisposed) return;
        if (snapshot.Error != null)
        {
            cpuStatusValueLabel.Text = gpuStatusValueLabel.Text = ramStatusValueLabel.Text = "Unavailable";
            systemStatusValueLabel.Text = cpuFanStatusValueLabel.Text = gpuFanStatusValueLabel.Text = "Unavailable";
            cpuStatusValueLabel.ForeColor = gpuStatusValueLabel.ForeColor = ramStatusValueLabel.ForeColor = LightError;
            systemStatusValueLabel.ForeColor = cpuFanStatusValueLabel.ForeColor = gpuFanStatusValueLabel.ForeColor = LightError;
            telemetryDetailsLabel.Text = "Monitor data unavailable: " + snapshot.Error.Message;
            telemetryDetailsLabel.ForeColor = LightError;
            return;
        }

        AddTemperatureSample(snapshot.CpuCelsius ?? snapshot.Cpu, cpuTemperatureHistory);
        AddTemperatureSample(snapshot.GpuCelsius ?? snapshot.Gpu, gpuTemperatureHistory);
        AddTemperatureSample(snapshot.SystemCelsius ?? snapshot.System, systemTemperatureHistory);
        AddUsageSample(snapshot.CpuUsage, cpuUsageHistory);
        AddUsageSample(snapshot.GpuUsage, gpuUsageHistory);
        cpuStatusValueLabel.Text = "Temp " + GetSensorValue(snapshot.Cpu) + "  ·  Load " + snapshot.CpuLoad;
        gpuStatusValueLabel.Text = "Temp " + GetSensorValue(snapshot.Gpu) + "  ·  Load " + snapshot.GpuLoad.Replace(" (peak engine)", String.Empty);
        ramStatusValueLabel.Text = snapshot.Memory;
        systemStatusValueLabel.Text = GetSensorValue(snapshot.System);
        cpuFanStatusValueLabel.Text = GetSensorValue(snapshot.CpuFan);
        gpuFanStatusValueLabel.Text = GetSensorValue(snapshot.GpuFan);
        cpuStatusValueLabel.ForeColor = gpuStatusValueLabel.ForeColor = ramStatusValueLabel.ForeColor = HintBlue;
        systemStatusValueLabel.ForeColor = cpuFanStatusValueLabel.ForeColor = gpuFanStatusValueLabel.ForeColor = HintBlue;
        UpdateTrayTooltip(snapshot);
        if (Visible && WindowState != FormWindowState.Minimized)
        {
            temperatureChart.Invalidate();
            usageChart.Invalidate();
        }
    }

    private void UpdateTrayTooltip(TelemetrySnapshot snapshot)
    {
        if (trayIcon == null) return;
        if (snapshot == null || snapshot.Error != null)
        {
            lastTrayStatusLine = null;
            try { trayIcon.Text = "NitroHub"; } catch { }
            return;
        }
        string cpu = GetSensorValue(snapshot.Cpu);
        string gpu = GetSensorValue(snapshot.Gpu);
        string cpuFan = GetSensorValue(snapshot.CpuFan).Replace(" RPM", String.Empty).Trim();
        string gpuFan = GetSensorValue(snapshot.GpuFan).Replace(" RPM", String.Empty).Trim();
        bool cpuFanOk = !String.IsNullOrEmpty(cpuFan) && !String.Equals(cpuFan, "unavailable", StringComparison.OrdinalIgnoreCase);
        bool gpuFanOk = !String.IsNullOrEmpty(gpuFan) && !String.Equals(gpuFan, "unavailable", StringComparison.OrdinalIgnoreCase);
        string fanSummary = (!cpuFanOk && !gpuFanOk)
            ? String.Empty
            : (cpuFanOk && gpuFanOk ? (cpuFan + "/" + gpuFan + " RPM") : ((cpuFanOk ? cpuFan : gpuFan) + " RPM"));
        lastTrayStatusLine = String.IsNullOrEmpty(fanSummary)
            ? ("CPU " + cpu + " · GPU " + gpu)
            : ("CPU " + cpu + " · GPU " + gpu + " · " + fanSummary);

        string tip = String.IsNullOrEmpty(fanSummary)
            ? ("NitroHub\nCPU " + cpu + " | GPU " + gpu)
            : ("NitroHub\nCPU " + cpu + " | GPU " + gpu + "\nFan " + fanSummary);
        if (tip.Length > 63) tip = tip.Substring(0, 63);
        try { trayIcon.Text = tip; } catch { }
    }

    private static string GetSensorValue(string reading)
    {
        int separator = reading.IndexOf(": ", StringComparison.Ordinal);
        return separator < 0 ? reading : reading.Substring(separator + 2);
    }

    private static void AddTemperatureSample(string sensor, List<float> history)
    {
        if (String.IsNullOrEmpty(sensor))
        {
            history.Add(Single.NaN);
            if (history.Count > HistoryCapacity) history.RemoveAt(0);
            return;
        }
        int start = sensor.IndexOf(": ", StringComparison.Ordinal);
        bool isC = sensor.EndsWith("°C", StringComparison.Ordinal);
        bool isF = sensor.EndsWith("°F", StringComparison.Ordinal);
        if (start < 0 || (!isC && !isF))
        {
            history.Add(Single.NaN);
            if (history.Count > HistoryCapacity) history.RemoveAt(0);
            return;
        }
        float value;
        if (!Single.TryParse(sensor.Substring(start + 2, sensor.Length - start - 4),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value))
        {
            value = Single.NaN;
        }
        else if (isF)
        {
            value = (value - 32F) * 5F / 9F;
        }
        history.Add(value);
        if (history.Count > HistoryCapacity) history.RemoveAt(0);
    }

    private volatile bool loadCountersInitializing;

    private void InitializeLoadMonitoring()
    {
        loadCountersInitializing = true;
        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            PerformanceCounter cpu = null;
            PerformanceCounter mem = null;
            try
            {
                try
                {
                    cpu = new PerformanceCounter("Processor", "% Processor Time", "_Total", true);
                    cpu.NextValue();
                }
                catch
                {
                    if (cpu != null) { try { cpu.Dispose(); } catch { } cpu = null; }
                }
                try
                {
                    mem = new PerformanceCounter("Memory", "% Committed Bytes In Use", true);
                    mem.NextValue();
                }
                catch
                {
                    if (mem != null) { try { mem.Dispose(); } catch { } mem = null; }
                }
                lock (counterLock)
                {
                    if (uiClosing)
                    {
                        if (cpu != null) { try { cpu.Dispose(); } catch { } }
                        if (mem != null) { try { mem.Dispose(); } catch { } }
                        return;
                    }
                    cpuLoadCounter = cpu;
                    memoryLoadCounter = mem;
                    cpuLoadWarmupTick = Environment.TickCount;
                }
            }
            finally
            {
                loadCountersInitializing = false;
            }
        });
    }

    private string ReadCpuLoad(out float? value)
    {
        value = null;
        if (cpuLoadCounter == null) return loadCountersInitializing ? "measuring..." : "unavailable";
        // The counter needs a real interval between two reads; a sample taken right after creation is meaningless, so skip it.
        if (unchecked(Environment.TickCount - cpuLoadWarmupTick) < 1500) return "measuring...";
        try
        {
            float sample;
            lock (counterLock)
            {
                if (cpuLoadCounter == null) return "unavailable";
                sample = Math.Max(0F, Math.Min(100F, cpuLoadCounter.NextValue()));
            }
            value = sample;
            return sample.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%";
        }
        catch { return "unavailable"; }
    }

    private static Dictionary<string, CounterSample> gpuPreviousSamples = new Dictionary<string, CounterSample>();
    private static bool gpuFastPathUnavailable;

    // One performance-counter category read per poll instead of a WMI query over every process's GPU engines.
    // Needs a previous sample, so the first call (or a localized Windows without these counter names) returns false and the WMI path is used.
    private static bool TryReadGpuPeakFast(out float peak)
    {
        peak = 0F;
        if (gpuFastPathUnavailable) return false;
        try
        {
            PerformanceCounterCategory category = new PerformanceCounterCategory("GPU Engine");
            InstanceDataCollectionCollection all = category.ReadCategory();
            InstanceDataCollection counter = all["Utilization Percentage"];
            if (counter == null) { gpuFastPathUnavailable = true; return false; }
            Dictionary<string, CounterSample> current = new Dictionary<string, CounterSample>();
            bool found = false;
            foreach (InstanceData data in counter.Values)
            {
                current[data.InstanceName] = data.Sample;
                CounterSample previous;
                if (!gpuPreviousSamples.TryGetValue(data.InstanceName, out previous)) continue;
                float reading = CounterSample.Calculate(previous, data.Sample);
                if (reading > peak) peak = reading;
                found = true;
            }
            gpuPreviousSamples = current;
            return found;
        }
        catch
        {
            gpuFastPathUnavailable = true;
            return false;
        }
    }

    private static string ReadGpuLoad(out float? value)
    {
        value = null;
        try
        {
            float peak = 0F;
            bool found = false;
            if (TryReadGpuPeakFast(out peak)) found = true;
            else
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                new ManagementScope("root\\CIMV2"),
                new ObjectQuery("SELECT UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine"), WmiOpts.Create()))
                using (ManagementObjectCollection items = searcher.Get())
            {
                foreach (ManagementObject item in items)
                    using (item)
                {
                    if (item["UtilizationPercentage"] == null) continue;
                    float reading = Convert.ToSingle(item["UtilizationPercentage"]);
                    if (reading > peak) peak = reading;
                    found = true;
                }
            }
            if (!found) return "unavailable";
            peak = Math.Max(0F, Math.Min(100F, peak));
            value = peak;
            return peak.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "% (peak engine)";
        }
        catch { return "unavailable"; }
    }

    private string ReadMemoryLoad()
    {
        if (memoryLoadCounter == null) return loadCountersInitializing ? "measuring..." : "unavailable";
        try
        {
            float sample;
            lock (counterLock)
            {
                if (memoryLoadCounter == null) return "unavailable";
                sample = Math.Max(0F, Math.Min(100F, memoryLoadCounter.NextValue()));
            }
            return sample.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "% committed";
        }
        catch { return "unavailable"; }
    }

    private static void AddUsageSample(float? sample, List<float> history)
    {
        history.Add(sample.HasValue ? Math.Max(0F, Math.Min(100F, sample.Value)) : Single.NaN);
        if (history.Count > HistoryCapacity) history.RemoveAt(0);
    }

    private static string GetHistoryValue(List<float> history, int index, int totalCount)
    {
        int offset = totalCount - history.Count;
        int valueIndex = index - offset;
        if (valueIndex < 0 || valueIndex >= history.Count || Single.IsNaN(history[valueIndex])) return "—";
        return history[valueIndex].ToString("0", System.Globalization.CultureInfo.InvariantCulture);
    }

    private string GetTemperatureHistoryValue(List<float> history, int index, int totalCount)
    {
        string value = GetHistoryValue(history, index, totalCount);
        float celsius;
        if (!useFahrenheit || value == "—" || !Single.TryParse(value, out celsius)) return value;
        return ((celsius * 9F / 5F) + 32F).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static Rectangle GetChartPlotRect(Control chart)
    {
        int left = UiDpi.S(46);
        int rightPad = UiDpi.S(14);
        int top = UiDpi.S(54);
        int bottomPad = UiDpi.S(24);
        return new Rectangle(
            left,
            top,
            Math.Max(10, chart.ClientSize.Width - left - rightPad),
            Math.Max(10, chart.ClientSize.Height - top - bottomPad));
    }

    private static int GetChartHoverIndex(Control chart, int mouseX, int mouseY, int count)
    {
        if (count < 1 || chart == null) return -1;
        Rectangle plot = GetChartPlotRect(chart);
        if (mouseX < plot.Left - UiDpi.S(6) || mouseX > plot.Right + UiDpi.S(6) ||
            mouseY < plot.Top - UiDpi.S(8) || mouseY > plot.Bottom + UiDpi.S(8))
            return -1;
        if (count == 1) return 0;
        double ratio = (mouseX - plot.Left) / (double)Math.Max(1, plot.Width);
        int idx = (int)Math.Round(ratio * (count - 1));
        return Math.Max(0, Math.Min(count - 1, idx));
    }

    private static GraphicsPath CreateChartRoundedPath(RectangleF bounds, float radius)
    {
        GraphicsPath path = new GraphicsPath();
        float d = Math.Min(radius * 2F, Math.Min(bounds.Width, bounds.Height));
        if (d <= 0.5F) { path.AddRectangle(bounds); return path; }
        path.AddArc(bounds.Left, bounds.Top, d, d, 180F, 90F);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270F, 90F);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0F, 90F);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90F, 90F);
        path.CloseFigure();
        return path;
    }

    private void DrawChartSurfaceAndFrame(Graphics g, Control chart, string title, out Rectangle plot)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        Rectangle bounds = chart.ClientRectangle;
        g.Clear(UiPaint.ParentBack(chart, BackColor));

        Color surfaceColor = isDarkTheme ? Color.FromArgb(22, 27, 34) : Color.FromArgb(248, 250, 253);
        Color borderColor = isDarkTheme ? Color.FromArgb(46, 56, 68) : Color.FromArgb(214, 224, 235);
        Color titleColor = isDarkTheme ? Color.FromArgb(232, 238, 246) : Color.FromArgb(30, 44, 58);

        RectangleF cardRect = new RectangleF(0.5F, 0.5F, Math.Max(1F, bounds.Width - 1F), Math.Max(1F, bounds.Height - 1F));
        using (GraphicsPath cardPath = CreateChartRoundedPath(cardRect, UiDpi.S(10)))
        using (SolidBrush bgBrush = new SolidBrush(surfaceColor))
        using (Pen borderPen = new Pen(borderColor, 1F))
        {
            g.FillPath(bgBrush, cardPath);
            g.DrawPath(borderPen, cardPath);
        }

        Font titleFont = UiLang.GetCachedFont(UiLang.GetUiFontFamily(currentLanguage), 8.5F, FontStyle.Regular);
        TextRenderer.DrawText(g, title, titleFont,
            new Rectangle(UiDpi.S(14), UiDpi.S(9), Math.Max(10, bounds.Width - UiDpi.S(28)), UiDpi.S(18)),
            titleColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        plot = GetChartPlotRect(chart);
    }

    private void DrawChartLegend(Graphics g, Control chart, string[] names, string[] values, Color[] colors)
    {
        string uiFamily = UiLang.GetUiFontFamily(currentLanguage);
        Font labelFont = UiLang.GetCachedFont(uiFamily, 8F, FontStyle.Regular);
        Font valFont = UiLang.GetCachedFont(uiFamily, 8F, FontStyle.Regular);
        Color labelColor = isDarkTheme ? Color.FromArgb(166, 179, 194) : Color.FromArgb(92, 108, 124);
        Color valColor = isDarkTheme ? Color.FromArgb(236, 241, 248) : Color.FromArgb(28, 42, 56);

        int x = UiDpi.S(14);
        int y = UiDpi.S(30);
        int dotSize = UiDpi.S(7);
        int rowH = UiDpi.S(16);
        for (int i = 0; i < names.Length; i++)
        {
            if (x > chart.ClientSize.Width - UiDpi.S(40)) break;
            int dotY = y + (rowH - dotSize) / 2;
            using (SolidBrush halo = new SolidBrush(Color.FromArgb(70, colors[i])))
                g.FillEllipse(halo, x - 1.5F, dotY - 1.5F, dotSize + 3F, dotSize + 3F);
            using (SolidBrush dot = new SolidBrush(colors[i]))
                g.FillEllipse(dot, x, dotY, dotSize, dotSize);
            x += dotSize + UiDpi.S(5);

            string nameText = names[i];
            Size nameSize = TextRenderer.MeasureText(g, nameText, labelFont, Size.Empty, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, nameText, labelFont, new Rectangle(x, y, nameSize.Width + 2, rowH),
                labelColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            x += nameSize.Width + UiDpi.S(4);

            string valText = values[i];
            if (!String.IsNullOrEmpty(valText) && valText != "—")
            {
                Size valSize = TextRenderer.MeasureText(g, valText, valFont, Size.Empty, TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, valText, valFont, new Rectangle(x, y, valSize.Width + 2, rowH),
                    valColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                x += valSize.Width + UiDpi.S(14);
            }
            else
            {
                x += UiDpi.S(12);
            }
        }
    }

    private void DrawChartAxes(Graphics g, Rectangle plot, int minimum, int maximum, int step, string unit, int sampleCount)
    {
        Color gridColor = isDarkTheme ? Color.FromArgb(46, 148, 168, 192) : Color.FromArgb(90, 175, 192, 210);
        Color baseLineColor = isDarkTheme ? Color.FromArgb(70, 84, 100) : Color.FromArgb(196, 208, 220);
        Color axisTextColor = isDarkTheme ? Color.FromArgb(142, 156, 172) : Color.FromArgb(104, 118, 134);
        Font axisFont = UiLang.GetCachedFont(UiLang.GetUiFontFamily(currentLanguage), 7.5F, FontStyle.Regular);

        using (Pen gridPen = new Pen(gridColor, 1F))
        using (Pen basePen = new Pen(baseLineColor, 1F))
        {
            gridPen.DashPattern = new float[] { 3F, 3F };
            for (int v = minimum; v <= maximum; v += step)
            {
                int y = plot.Bottom - (int)Math.Round((v - minimum) * (double)plot.Height / (maximum - minimum));
                g.DrawLine(v == minimum ? basePen : gridPen, plot.Left, y, plot.Right, y);
                TextRenderer.DrawText(g, v + unit, axisFont,
                    new Rectangle(UiDpi.S(4), y - UiDpi.S(8), plot.Left - UiDpi.S(10), UiDpi.S(16)),
                    axisTextColor, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        int intervalSec = Math.Max(1, (telemetryTimer != null ? telemetryTimer.Interval : 2000) / 1000);
        int totalSec = Math.Max(0, (Math.Max(1, sampleCount) - 1) * intervalSec);
        string leftTime = totalSec == 0 ? "0s" : ("-" + FormatCompactDuration(totalSec));
        int xAxisY = plot.Bottom + UiDpi.S(4);
        TextRenderer.DrawText(g, leftTime, axisFont,
            new Rectangle(plot.Left, xAxisY, UiDpi.S(70), UiDpi.S(15)),
            axisTextColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, Tr("Now", "当前"), axisFont,
            new Rectangle(plot.Right - UiDpi.S(50), xAxisY, UiDpi.S(50), UiDpi.S(15)),
            axisTextColor, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private static string FormatCompactDuration(int seconds)
    {
        if (seconds < 60) return seconds + "s";
        int mins = seconds / 60;
        int rem = seconds % 60;
        if (mins < 60) return rem == 0 ? (mins + "m") : (mins + "m " + rem + "s");
        int hrs = mins / 60;
        int remMins = mins % 60;
        return remMins == 0 ? (hrs + "h") : (hrs + "h " + remMins + "m");
    }

    private void DrawSmoothAreaSeries(Graphics g, List<float> history, int totalCount, Color color, Rectangle plot,
        float minimum, float maximum, bool convertToFahrenheit, bool fillArea, bool drawLiveDot)
    {
        if (history == null || history.Count < 1 || totalCount < 1) return;
        int offset = totalCount - history.Count;
        float width = plot.Width;
        float height = plot.Height;
        List<PointF> segment = new List<PointF>();
        PointF? lastValidPoint = null;

        GraphicsState savedState = g.Save();
        try
        {
            g.SetClip(new Rectangle(plot.Left - 2, plot.Top - 2, plot.Width + 4, plot.Height + 4));
            using (Pen linePen = new Pen(color, 2.0F) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                Action flushSegment = delegate
                {
                    if (segment.Count == 0) return;
                    PointF[] pts = segment.ToArray();
                    if (pts.Length >= 2)
                    {
                        if (fillArea)
                        {
                            using (GraphicsPath area = new GraphicsPath())
                            {
                                area.AddCurve(pts, 0.22F);
                                area.AddLine(pts[pts.Length - 1].X, pts[pts.Length - 1].Y, pts[pts.Length - 1].X, plot.Bottom);
                                area.AddLine(pts[pts.Length - 1].X, plot.Bottom, pts[0].X, plot.Bottom);
                                area.CloseFigure();
                                int topAlpha = isDarkTheme ? 56 : 42;
                                using (LinearGradientBrush grad = new LinearGradientBrush(
                                    new PointF(0F, plot.Top), new PointF(0F, plot.Bottom + 1F),
                                    Color.FromArgb(topAlpha, color), Color.FromArgb(0, color)))
                                {
                                    g.FillPath(grad, area);
                                }
                            }
                        }
                        g.DrawCurve(linePen, pts, 0.22F);
                    }
                    else if (pts.Length == 1)
                    {
                        using (SolidBrush dot = new SolidBrush(color))
                            g.FillEllipse(dot, pts[0].X - 2.5F, pts[0].Y - 2.5F, 5F, 5F);
                    }
                    segment.Clear();
                };

                for (int i = 0; i < history.Count; i++)
                {
                    if (Single.IsNaN(history[i]))
                    {
                        flushSegment();
                        continue;
                    }
                    float value = history[i];
                    if (convertToFahrenheit) value = value * 9F / 5F + 32F;
                    value = Math.Max(minimum, Math.Min(maximum, value));
                    int globalIdx = offset + i;
                    float x = totalCount == 1 ? plot.Right : (plot.Left + width * globalIdx / (totalCount - 1));
                    float y = plot.Bottom - (value - minimum) * height / (maximum - minimum);
                    PointF pt = new PointF(x, y);
                    segment.Add(pt);
                    lastValidPoint = pt;
                }
                flushSegment();
            }
        }
        finally
        {
            g.Restore(savedState);
        }

        if (drawLiveDot && lastValidPoint.HasValue)
        {
            PointF p = lastValidPoint.Value;
            using (SolidBrush glow = new SolidBrush(Color.FromArgb(72, color)))
                g.FillEllipse(glow, p.X - 5F, p.Y - 5F, 10F, 10F);
            using (SolidBrush core = new SolidBrush(color))
                g.FillEllipse(core, p.X - 2.5F, p.Y - 2.5F, 5F, 5F);
        }
    }

    private void DrawChartHoverHud(Graphics g, Control chart, Rectangle plot, int hoverIndex, int totalCount,
        List<float>[] seriesList, string[] names, Color[] colors, float minimum, float maximum, bool convertToFahrenheit, string unit)
    {
        if (hoverIndex < 0 || hoverIndex >= totalCount || totalCount < 1) return;

        float x = totalCount == 1 ? plot.Right : (plot.Left + plot.Width * (float)hoverIndex / (totalCount - 1));
        Color guideColor = isDarkTheme ? Color.FromArgb(115, 200, 216, 234) : Color.FromArgb(120, 82, 100, 120);
        using (Pen guidePen = new Pen(guideColor, 1F))
        {
            guidePen.DashPattern = new float[] { 3F, 3F };
            g.DrawLine(guidePen, x, plot.Top, x, plot.Bottom);
        }

        Color ringColor = isDarkTheme ? Color.FromArgb(22, 27, 34) : Color.White;
        string[] formattedValues = new string[seriesList.Length];
        for (int s = 0; s < seriesList.Length; s++)
        {
            List<float> history = seriesList[s];
            int offset = totalCount - history.Count;
            int idx = hoverIndex - offset;
            if (idx >= 0 && idx < history.Count && !Single.IsNaN(history[idx]))
            {
                float raw = history[idx];
                float val = convertToFahrenheit ? (raw * 9F / 5F + 32F) : raw;
                formattedValues[s] = val.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + unit;
                float clamped = Math.Max(minimum, Math.Min(maximum, val));
                float y = plot.Bottom - (clamped - minimum) * plot.Height / (maximum - minimum);
                using (SolidBrush halo = new SolidBrush(Color.FromArgb(85, colors[s])))
                    g.FillEllipse(halo, x - 6F, y - 6F, 12F, 12F);
                using (SolidBrush ring = new SolidBrush(ringColor))
                    g.FillEllipse(ring, x - 4F, y - 4F, 8F, 8F);
                using (SolidBrush core = new SolidBrush(colors[s]))
                    g.FillEllipse(core, x - 2.6F, y - 2.6F, 5.2F, 5.2F);
            }
            else
            {
                formattedValues[s] = "—";
            }
        }

        int intervalSec = Math.Max(1, (telemetryTimer != null ? telemetryTimer.Interval : 2000) / 1000);
        int secondsAgo = Math.Max(0, (totalCount - 1 - hoverIndex) * intervalSec);
        string header = secondsAgo == 0
            ? Tr("Just now", "刚刚")
            : (FormatCompactDuration(secondsAgo) + Tr(" ago", "前"));

        string uiFamily = UiLang.GetUiFontFamily(currentLanguage);
        Font hdrFont = UiLang.GetCachedFont(uiFamily, 7.5F, FontStyle.Regular);
        Font rowFont = UiLang.GetCachedFont(uiFamily, 8F, FontStyle.Regular);
        Font valFont = UiLang.GetCachedFont(uiFamily, 8F, FontStyle.Regular);

        int padX = UiDpi.S(10);
        int padY = UiDpi.S(7);
        int lineH = UiDpi.S(16);
        int boxW = UiDpi.S(126);
        int boxH = padY * 2 + UiDpi.S(15) + seriesList.Length * lineH;

        int boxX = (int)Math.Round(x) + UiDpi.S(10);
        if (boxX + boxW > plot.Right) boxX = (int)Math.Round(x) - boxW - UiDpi.S(10);
        boxX = Math.Max(plot.Left + UiDpi.S(2), Math.Min(plot.Right - boxW - UiDpi.S(2), boxX));
        int boxY = plot.Top + UiDpi.S(4);

        Color tipBg = isDarkTheme ? Color.FromArgb(236, 15, 20, 27) : Color.FromArgb(244, 255, 255, 255);
        Color tipBorder = isDarkTheme ? Color.FromArgb(72, 88, 108) : Color.FromArgb(196, 208, 222);
        Color tipHeaderColor = isDarkTheme ? Color.FromArgb(160, 174, 190) : Color.FromArgb(96, 112, 128);
        Color tipTextColor = isDarkTheme ? Color.FromArgb(234, 240, 248) : Color.FromArgb(28, 42, 56);

        RectangleF tipRect = new RectangleF(boxX, boxY, boxW, boxH);
        using (GraphicsPath tipPath = CreateChartRoundedPath(tipRect, UiDpi.S(7)))
        using (SolidBrush bg = new SolidBrush(tipBg))
        using (Pen border = new Pen(tipBorder, 1F))
        {
            g.FillPath(bg, tipPath);
            g.DrawPath(border, tipPath);
        }

        int curY = boxY + padY;
        TextRenderer.DrawText(g, header, hdrFont,
            new Rectangle(boxX + padX, curY, boxW - padX * 2, UiDpi.S(14)),
            tipHeaderColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        curY += UiDpi.S(15);

        int dotSize = UiDpi.S(6);
        for (int s = 0; s < seriesList.Length; s++)
        {
            using (SolidBrush dot = new SolidBrush(colors[s]))
                g.FillEllipse(dot, boxX + padX, curY + (lineH - dotSize) / 2, dotSize, dotSize);
            TextRenderer.DrawText(g, names[s], rowFont,
                new Rectangle(boxX + padX + dotSize + UiDpi.S(5), curY, UiDpi.S(52), lineH),
                tipHeaderColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, formattedValues[s], valFont,
                new Rectangle(boxX + padX, curY, boxW - padX * 2, lineH),
                tipTextColor, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            curY += lineH;
        }
    }

    private void DrawTemperatureHistory(object sender, PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        int minimum = useFahrenheit ? 68 : 20;
        int maximum = useFahrenheit ? 212 : 100;
        int step = useFahrenheit ? 36 : 20;
        string unit = useFahrenheit ? "°F" : "°C";

        Rectangle plot;
        DrawChartSurfaceAndFrame(g, temperatureChart,
            Tr("Temperature history (" + unit + ") · last hour", "温度历史 (" + unit + ") · 最近 1 小时"), out plot);

        Color cpuColor = Color.FromArgb(255, 102, 122);
        Color gpuColor = Color.FromArgb(74, 172, 255);
        Color sysColor = Color.FromArgb(62, 214, 152);

        int totalCount = Math.Max(cpuTemperatureHistory.Count, Math.Max(gpuTemperatureHistory.Count, systemTemperatureHistory.Count));
        string cpuLatest = totalCount > 0 ? GetTemperatureHistoryValue(cpuTemperatureHistory, totalCount - 1, totalCount) : "—";
        string gpuLatest = totalCount > 0 ? GetTemperatureHistoryValue(gpuTemperatureHistory, totalCount - 1, totalCount) : "—";
        string sysLatest = totalCount > 0 ? GetTemperatureHistoryValue(systemTemperatureHistory, totalCount - 1, totalCount) : "—";
        DrawChartLegend(g, temperatureChart,
            new string[] { "CPU", "GPU", "System" },
            new string[] {
            cpuLatest == "—" ? "—" : (cpuLatest + unit),
            gpuLatest == "—" ? "—" : (gpuLatest + unit),
            sysLatest == "—" ? "—" : (sysLatest + unit)
        },
            new Color[] { cpuColor, gpuColor, sysColor });

        DrawChartAxes(g, plot, minimum, maximum, step, unit, totalCount);

        bool hovering = tempChartHoverIndex >= 0 && tempChartHoverIndex < totalCount;
        DrawSmoothAreaSeries(g, systemTemperatureHistory, totalCount, sysColor, plot, minimum, maximum, useFahrenheit, false, !hovering);
        DrawSmoothAreaSeries(g, gpuTemperatureHistory, totalCount, gpuColor, plot, minimum, maximum, useFahrenheit, true, !hovering);
        DrawSmoothAreaSeries(g, cpuTemperatureHistory, totalCount, cpuColor, plot, minimum, maximum, useFahrenheit, true, !hovering);

        if (hovering)
        {
            DrawChartHoverHud(g, temperatureChart, plot, tempChartHoverIndex, totalCount,
                new List<float>[] { cpuTemperatureHistory, gpuTemperatureHistory, systemTemperatureHistory },
                new string[] { "CPU", "GPU", "System" },
                new Color[] { cpuColor, gpuColor, sysColor },
                minimum, maximum, useFahrenheit, unit);
        }
    }

    private void DrawUsageHistory(object sender, PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        Rectangle plot;
        DrawChartSurfaceAndFrame(g, usageChart,
            Tr("Processor and GPU load (%) · last hour", "处理器与显卡负载 (%) · 最近 1 小时"), out plot);

        Color cpuColor = Color.FromArgb(245, 158, 66);
        Color gpuColor = Color.FromArgb(84, 156, 255);

        int totalCount = Math.Max(cpuUsageHistory.Count, gpuUsageHistory.Count);
        string cpuLatest = totalCount > 0 ? GetHistoryValue(cpuUsageHistory, totalCount - 1, totalCount) : "—";
        string gpuLatest = totalCount > 0 ? GetHistoryValue(gpuUsageHistory, totalCount - 1, totalCount) : "—";
        DrawChartLegend(g, usageChart,
            new string[] { "CPU", "GPU" },
            new string[] {
            cpuLatest == "—" ? "—" : (cpuLatest + "%"),
            gpuLatest == "—" ? "—" : (gpuLatest + "%")
        },
            new Color[] { cpuColor, gpuColor });

        DrawChartAxes(g, plot, 0, 100, 25, "%", totalCount);

        bool hovering = usageChartHoverIndex >= 0 && usageChartHoverIndex < totalCount;
        DrawSmoothAreaSeries(g, gpuUsageHistory, totalCount, gpuColor, plot, 0F, 100F, false, true, !hovering);
        DrawSmoothAreaSeries(g, cpuUsageHistory, totalCount, cpuColor, plot, 0F, 100F, false, true, !hovering);

        if (hovering)
        {
            DrawChartHoverHud(g, usageChart, plot, usageChartHoverIndex, totalCount,
                new List<float>[] { cpuUsageHistory, gpuUsageHistory },
                new string[] { "CPU", "GPU" },
                new Color[] { cpuColor, gpuColor },
                0F, 100F, false, "%");
        }
    }

    private static string ReadSensor(uint sensorMap, uint sensorId, string name, string suffix)
    {
        if ((sensorMap & (1U << (int)(sensorId - 1))) == 0)
            return name + ": unsupported";
        ulong raw = ReadGamingSystemInfo(0x01U | (sensorId << 8));
        if ((raw & 0xFF) != 0)
            return name + ": unavailable";
        uint value = (uint)((raw >> 8) & 0xFFFF);
        return name + ": " + value + suffix;
    }

    private static string ConvertTemperatureToFahrenheit(string sensor)
    {
        int start = sensor.IndexOf(": ", StringComparison.Ordinal);
        if (start < 0 || !sensor.EndsWith("°C", StringComparison.Ordinal)) return sensor;
        int celsius;
        if (!Int32.TryParse(sensor.Substring(start + 2, sensor.Length - start - 4), out celsius)) return sensor;
        return sensor.Substring(0, start + 2) + ((celsius * 9 / 5) + 32) + "°F";
    }

    private void ApplyTheme()
    {
        UiPaint.BeginRedrawBatch(this);
        SuspendLayout();
        try { ApplyThemeCore(); }
        finally
        {
            ResumeLayout(false);
            UiPaint.EndRedrawBatch(this);
        }
        // Update DWM title bar after client redraw completes so SWP_FRAMECHANGED does not disturb WM_SETREDRAW batching.
        ApplyTitleBarTheme(isDarkTheme);
    }

    private void ApplyThemeCore()
    {
        Color background = isDarkTheme ? Color.FromArgb(21, 26, 32) : Color.FromArgb(244, 247, 251);
        Color card = isDarkTheme ? Color.FromArgb(34, 41, 49) : Color.White;
        Color text = isDarkTheme ? Color.FromArgb(232, 237, 243) : Color.FromArgb(39, 57, 74);
        Color muted = isDarkTheme ? Color.FromArgb(202, 211, 221) : Muted;
        Color border = isDarkTheme ? Color.FromArgb(92, 105, 119) : Color.FromArgb(198, 209, 220);
        BackColor = background;
        navigationPanel.BackColor = background;
        pageContainer.BackColor = background;
        foreach (Control page in pageContainer.Controls) page.BackColor = background;
        ApplyThemeToChildren(this, background, card, text, muted, border);
        appHeader.BackColor = isDarkTheme ? Color.FromArgb(23, 29, 36) : Color.FromArgb(222, 231, 242);
        foreach (Control headerChild in appHeader.Controls)
        {
            if (headerChild is BrandTitleControl)
                ((BrandTitleControl)headerChild).NitroColor = isDarkTheme ? Color.White : Color.FromArgb(24, 47, 70);
            else if (headerChild is Label)
                headerChild.ForeColor = isDarkTheme ? Color.FromArgb(166, 179, 192) : Color.FromArgb(72, 90, 110);
            else if (headerChild is VersionBadgePanel)
            {
                headerChild.BackColor = isDarkTheme ? Color.FromArgb(34, 41, 49) : Color.FromArgb(200, 213, 228);
                foreach (Control badgeChild in headerChild.Controls)
                    badgeChild.ForeColor = isDarkTheme ? Color.FromArgb(166, 179, 192) : Color.FromArgb(50, 70, 92);
                headerChild.Invalidate();
            }
        }
        if (laptopPhotoStatus != null)
        {
            laptopPhotoStatus.BackColor = Color.Transparent;
            laptopPhotoStatus.ForeColor = isDarkTheme ? Color.FromArgb(183, 205, 228) : Color.FromArgb(88, 104, 122);
        }
        Color chartBackground = isDarkTheme ? Color.FromArgb(25, 31, 38) : Color.FromArgb(250, 252, 254);
        temperatureChart.BackColor = chartBackground;
        usageChart.BackColor = chartBackground;
        if (keyboardLightingPreview != null) keyboardLightingPreview.ApplyTheme(isDarkTheme);
        if (laptopProductPhoto != null)
            laptopProductPhoto.BackColor = Color.Transparent;
        if (settingsPanel != null) settingsPanel.ApplyVisualTheme(false);
        UpdateNavigationStyles();
        ApplyTrayTheme();
        temperatureChart.Invalidate();
        usageChart.Invalidate();
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;
    private const int DWMWCP_ROUND = 2;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    // Seamless DWM title bar styling: keeps 100% genuine Windows native caption buttons (Minimize/Maximize/Close),
    // native animations, Win11 rounded corners, and Win11 Snap Layouts while matching the title bar color to appHeader.
    internal static void ApplyNativeTitleBarTheme(IntPtr handle, bool dark, Color? captionColor, Color? textColor)
    {
        if (handle == IntPtr.Zero) return;
        try
        {
            int useDark = dark ? 1 : 0;
            if (DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref useDark, sizeof(int));

            int corner = DWMWCP_ROUND;
            DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

            if (captionColor.HasValue)
            {
                int captionRef = ColorTranslator.ToWin32(captionColor.Value);
                DwmSetWindowAttribute(handle, DWMWA_CAPTION_COLOR, ref captionRef, sizeof(int));
            }
            if (textColor.HasValue)
            {
                int textRef = ColorTranslator.ToWin32(textColor.Value);
                DwmSetWindowAttribute(handle, DWMWA_TEXT_COLOR, ref textRef, sizeof(int));
            }

            SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }
        catch { }
    }

    private void ApplyTitleBarTheme(bool dark)
    {
        if (!IsHandleCreated) return;
        Color barBg = dark ? Color.FromArgb(23, 29, 36) : Color.FromArgb(222, 231, 242);
        Color barFg = dark ? Color.FromArgb(220, 226, 234) : Color.FromArgb(39, 57, 74);
        ApplyNativeTitleBarTheme(Handle, dark, barBg, barFg);
    }

    private void ApplyThemeToChildren(Control parent, Color background, Color card, Color text, Color muted, Color border)
    {
        foreach (Control control in parent.Controls)
        {
            if (control == appHeader || control == settingsPanel) continue;
            if (control is Panel)
            {
                Panel panel = (Panel)control;
                if (Convert.ToString(panel.Tag) == "card") panel.BackColor = card;
                else if (Convert.ToString(panel.Tag) == "graphics-support-panel") panel.BackColor = card;
                else if (Convert.ToString(panel.Tag) == "graphics-support-row")
                {
                    panel.BackColor = isDarkTheme ? Color.FromArgb(28, 34, 41) : Color.FromArgb(234, 240, 247);
                    RoundedPanel supportRow = panel as RoundedPanel;
                    if (supportRow != null)
                    {
                        supportRow.BorderColor = isDarkTheme ? Color.FromArgb(58, 69, 81) : Color.FromArgb(208, 218, 229);
                        supportRow.Invalidate();
                    }
                }
                else if (Convert.ToString(panel.Tag) == "status-card") panel.BackColor = isDarkTheme ? Color.FromArgb(43, 51, 61) : Color.FromArgb(234, 240, 247);
                else if (Convert.ToString(panel.Tag) == "monitor-metric-card") panel.BackColor = isDarkTheme ? Color.FromArgb(8, 25, 31) : Color.FromArgb(234, 240, 247);
                else if (Convert.ToString(panel.Tag) == "flat-card-content") panel.BackColor = card;
                else if (Convert.ToString(panel.Tag) == "accent-strip" || panel == chargeStatusAccent) { }
                else if (Convert.ToString(panel.Tag) == "color-preview") { }
                else if (panel != temperatureChart && panel != usageChart) panel.BackColor = background;
            }
            if (control is RichTextBox)
            {
                control.BackColor = card;
            }
            else if (control is Label)
            {
                control.BackColor = Color.Transparent;
                if (control.Parent != appHeader)
                {
                    string labelRole = Convert.ToString(control.Tag);
                    if (labelRole == "monitor-value") control.ForeColor = MonitorCyan;
                    else if (labelRole == "hint") control.ForeColor = HintBlue;
                    else if (labelRole == "section-heading" || labelRole.StartsWith("section-"))
                        control.ForeColor = GetSectionTitleColor(labelRole);
                    else if (control.ForeColor.ToArgb() == ProtectionGreen.ToArgb() ||
                        control.ForeColor.ToArgb() == Color.FromArgb(30, 138, 84).ToArgb())
                        control.ForeColor = isDarkTheme ? ProtectionGreen : Color.FromArgb(30, 138, 84);
                    else if (labelRole == "hint") control.ForeColor = HintBlue;
                    else if (control.ForeColor.ToArgb() == Teal.ToArgb()) control.ForeColor = isDarkTheme ? Color.FromArgb(255, 100, 120) : Teal;
                    else if (control.ForeColor.ToArgb() == LightError.ToArgb())
                        control.ForeColor = isDarkTheme ? LightError : Color.FromArgb(174, 72, 66);
                    else if (control.ForeColor.ToArgb() == Muted.ToArgb() ||
                        control.ForeColor.ToArgb() == Color.FromArgb(105, 121, 138).ToArgb() ||
                        control.ForeColor.ToArgb() == Color.FromArgb(91, 108, 124).ToArgb()) control.ForeColor = muted;
                    else control.ForeColor = text;
                }
            }
            else if (control is ToggleSwitch)
            {
                ToggleSwitch toggle = (ToggleSwitch)control;
                toggle.OnColor = Color.FromArgb(210, 48, 66);
                toggle.OffColor = Color.FromArgb(198, 202, 207);
                toggle.StateTextColor = isDarkTheme ? Color.FromArgb(183, 205, 228) : Color.FromArgb(88, 104, 122);
                toggle.Invalidate();
            }
            else if (control is SegmentedControl)
            {
                SegmentedControl segment = (SegmentedControl)control;
                segment.TrackColor = isDarkTheme ? Color.FromArgb(43, 51, 61) : Color.FromArgb(226, 232, 240);
                segment.UnselectedTextColor = isDarkTheme ? Color.FromArgb(210, 219, 228) : Color.FromArgb(88, 104, 122);
                segment.Invalidate();
            }
            else if (control is Button)
            {
                Button button = (Button)control;
                if (Convert.ToString(button.Tag) == "navigation")
                {
                    bool selected = Array.IndexOf(navigationButtons, button) == currentPageIndex;
                    button.BackColor = selected ? Teal : card;
                    button.ForeColor = selected ? Color.White : text;
                }
                else if (Convert.ToString(button.Tag) == "accent")
                {
                    button.BackColor = Teal;
                    button.ForeColor = Color.White;
                }
                else if (Convert.ToString(button.Tag) == "state-off")
                {
                    button.BackColor = isDarkTheme ? Color.FromArgb(49, 59, 70) : Color.White;
                    button.ForeColor = isDarkTheme ? Color.FromArgb(245, 247, 250) : Color.FromArgb(39, 57, 74);
                }
                else
                {
                    button.BackColor = card;
                    button.ForeColor = text;
                }
                button.FlatAppearance.BorderColor = border;
                button.FlatAppearance.MouseOverBackColor = button.Name == "support-link"
                    ? border
                    : (isDarkTheme ? Color.FromArgb(57, 67, 78) : Color.FromArgb(231, 237, 243));
                button.FlatAppearance.MouseDownBackColor = isDarkTheme
                    ? Color.FromArgb(72, 83, 95) : Color.FromArgb(218, 227, 236);
            }
            else if (control is CheckBox || control is RadioButton)
            {
                control.BackColor = Color.Transparent;
                control.ForeColor = text;
            }
            else if (control is ComboBox || control is TextBox)
            {
                control.BackColor = isDarkTheme ? Color.FromArgb(42, 50, 59) : Color.White;
                control.ForeColor = text;
                if (control is ThemedComboBox) { ((ThemedComboBox)control).BorderColor = border; control.Invalidate(); }
            }
            ApplyThemeToChildren(control, background, card, text, muted, border);
        }
    }

    private Color GetSectionTitleColor(string role)
    {
        return SharedHeadingColor;
    }

    private void UpdateKeyboardTimeoutToggleStyle()
    {
        if (keyboardTimeoutToggleButton == null) return;
        keyboardTimeoutToggleButton.Checked = keyboardTimeoutEnabled;
        keyboardTimeoutStateLabel.Text = keyboardTimeoutEnabled ? "ON" : "OFF";
        keyboardTimeoutStateLabel.ForeColor = HintBlue;
        keyboardTimeoutToggleButton.OnColor = Color.FromArgb(210, 48, 66);
        keyboardTimeoutToggleButton.OffColor = Color.FromArgb(198, 202, 207);
        if (keyboardTimeoutAccent != null)
            keyboardTimeoutAccent.BackColor = keyboardTimeoutEnabled ? Teal : Color.FromArgb(92, 105, 119);
        keyboardTimeoutToggleButton.Invalidate();
    }

    private void ApplyStateButtonAppearance(Button button)
    {
        bool active = Convert.ToString(button.Tag) == "accent";
        button.BackColor = active ? Teal : (isDarkTheme ? Color.FromArgb(49, 59, 70) : Color.White);
        button.ForeColor = active || !isDarkTheme ? (active ? Color.White : Color.FromArgb(39, 57, 74)) : Color.FromArgb(245, 247, 250);
        button.FlatAppearance.BorderColor = isDarkTheme ? Color.FromArgb(112, 127, 142) : Color.FromArgb(198, 209, 220);
        button.FlatAppearance.BorderSize = active ? 0 : 1;
    }

    private void UpdateFanModeButtons(byte selectedMode)
    {
        currentFanMode = selectedMode;
        if (autoFanButton == null || maxFanButton == null) return;
        autoFanButton.Tag = selectedMode == 1 ? "accent" : "state-off";
        maxFanButton.Tag = selectedMode == 2 ? "accent" : "state-off";
        ApplyStateButtonAppearance(autoFanButton);
        ApplyStateButtonAppearance(maxFanButton);
        if (selectedMode == 0)
        {
            SetFanModeButtonAvailableStyle(autoFanButton);
            SetFanModeButtonAvailableStyle(maxFanButton);
        }
    }

    private void SetFanModeButtonAvailableStyle(Button button)
    {
        button.BackColor = isDarkTheme ? Color.FromArgb(61, 72, 84) : Color.White;
        button.ForeColor = isDarkTheme ? Color.FromArgb(245, 247, 250) : Color.FromArgb(39, 57, 74);
        button.FlatAppearance.BorderColor = isDarkTheme ? Color.FromArgb(129, 145, 161) : Color.FromArgb(198, 209, 220);
        button.FlatAppearance.BorderSize = 1;
    }

    private static ulong ReadGamingSystemInfo(uint command)
    {
        using (ManagementObject device = GetGamingFunction())
        using (ManagementBaseObject input = device.GetMethodParameters("GetGamingSysInfo"))
        {
            input["gmInput"] = command;
            using (ManagementBaseObject result = device.InvokeMethod("GetGamingSysInfo", input, null))
            {
                if (result == null || result["gmOutput"] == null)
                    throw new InvalidOperationException("Acer firmware returned no sensor data.");
                return Convert.ToUInt64(result["gmOutput"]);
            }
        }
    }

    private void ChooseLightingColor()
    {
        using (ColorDialog picker = new ColorDialog { Color = lightingColor, FullOpen = true })
        {
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            lightingColor = picker.Color;
            lightingColorPreview.BackColor = picker.Color;
            UpdateKeyboardPreview();
        }
    }

    private void UpdateLightingColorControl()
    {
        KeyboardEffectProfile selected = effectCombo.SelectedItem as KeyboardEffectProfile;
        bool canChangeColor = selected != null && selected.SupportsCustomColor;
        lightingColorButton.Visible = canChangeColor;
        lightingColorButton.Enabled = keyboardEffectControlAvailable && canChangeColor;
        lightingColorPreview.Visible = canChangeColor;
        lightingColorPreview.BackColor = canChangeColor ? lightingColor : Color.FromArgb(68, 76, 84);
        lightingColorStatusLabel.Text = selected == null ? String.Empty : (canChangeColor ? String.Empty : "Effect color cannot be changed for this effect.");
        lightingColorStatusLabel.Location = new Point(UiDpi.S(506), UiDpi.S(canChangeColor ? 210 : 170));
        int compactOffset = canChangeColor ? 0 : UiDpi.S(26);
        brightnessTitleLabel.Location = new Point(UiDpi.S(506), UiDpi.S(227) - compactOffset);
        brightnessValueLabel.Location = new Point(UiDpi.S(742), UiDpi.S(227) - compactOffset);
        brightnessTrack.Location = new Point(UiDpi.S(500), UiDpi.S(247) - compactOffset);
        effectSpeedTitleLabel.Location = new Point(UiDpi.S(506), UiDpi.S(291) - compactOffset);
        speedValueLabel.Location = new Point(UiDpi.S(742), UiDpi.S(291) - compactOffset);
        speedTrack.Location = new Point(UiDpi.S(500), UiDpi.S(311) - compactOffset);
        applyKeyboardButton.Location = new Point(UiDpi.S(569), UiDpi.S(360) - compactOffset);
        UpdateKeyboardPreview();
    }

    private void UpdateKeyboardPreview()
    {
        if (keyboardLightingPreview == null) return;
        string selectedEffect = effectCombo == null ? "Neon" : Convert.ToString(effectCombo.SelectedItem);
        int brightness = brightnessTrack == null ? 100 : brightnessTrack.Value;
        int speed = speedTrack == null ? 5 : speedTrack.Value;
        keyboardLightingPreview.SetPreview(selectedEffect, lightingColor, brightness, speed);
    }

    private static string ReadBiosVersion()
    {
        try
        {
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(new ManagementScope(@"root\CIMV2"), new ObjectQuery("SELECT SMBIOSBIOSVersion, BIOSVersion FROM Win32_BIOS"), WmiOpts.Create()))
            using (ManagementObjectCollection items = searcher.Get())
            {
                foreach (ManagementObject item in items)
                    using (item)
                {
                    string version = Convert.ToString(item["SMBIOSBIOSVersion"]);
                    if (!String.IsNullOrWhiteSpace(version)) return version;
                    Array versions = item["BIOSVersion"] as Array;
                    if (versions != null && versions.Length > 0) return Convert.ToString(versions.GetValue(0));
                }
            }
        }
        catch { }
        return "Unavailable";
    }

    private void CaptureKeyboardTimeoutState()
    {
        bool? timeoutEnabled = null;
        RunHardwareAsync(delegate
        {
            if (!IsAcerApgeInterfaceAvailable())
                throw new InvalidOperationException(MissingInterfaceMessage("Keyboard timeout control", "APGeAction"));
            timeoutEnabled = ReadKeyboardBacklightTimeout();
            if (!timeoutEnabled.HasValue)
                throw new InvalidOperationException("Acer firmware did not return a readable timeout state. Use the BIOS setting if available.");
        }, delegate(Exception ex)
        {
            if (ex == null && timeoutEnabled.HasValue)
                keyboardTimeoutEnabled = timeoutEnabled.Value;
            UpdateKeyboardTimeoutToggleStyle();
        });
    }

    private void SetKeyboardTimeoutFromUi(bool requested)
    {
        bool previous = !requested;
        RunHardwareAsync(delegate
        {
            SetKeyboardBacklightTimeout(requested);
            ulong rawState = InvokeApgeFunction("GetFunction", 0x88401UL);
            bool? confirmed = rawState == 0 ? (bool?)null : (rawState == 0x1E0000080000UL);
            if (!confirmed.HasValue || confirmed.Value != requested)
                throw new InvalidOperationException("Firmware did not confirm the selected timeout state (it reported 0x" + rawState.ToString("X") + ").");
        }, delegate(Exception ex)
        {
            if (ex == null) return;
            keyboardTimeoutEnabled = previous;
            UpdateKeyboardTimeoutToggleStyle();
            MessageBox.Show(ex.Message, Tr("Keyboard backlight timeout unavailable", "键盘背光超时不可用"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        });
    }

    private static bool? ReadKeyboardBacklightTimeout()
    {
        ulong output = InvokeApgeFunction("GetFunction", 0x88401UL);
        if (output == 0) return null;
        return output == 0x1E0000080000UL;
    }

    private static void SetKeyboardBacklightTimeout(bool enabled)
    {
        if (!IsAcerApgeInterfaceAvailable())
            throw new InvalidOperationException(MissingInterfaceMessage("Keyboard timeout control", "APGeAction"));
        ulong input = enabled ? 0x1E0000088402UL : 0x88402UL;
        // The value SetFunction returns is not a reliable success flag (many Acer WMI setters return 0 on success, and the USB
        // charging setter ignores it too). The real check is the read-back in SetKeyboardTimeoutFromUi.
        InvokeApgeFunction("SetFunction", input);
    }

    private static ulong InvokeApgeFunction(string method, ulong inputValue)
    {
        using (ManagementObject device = GetApgeAction())
        using (ManagementBaseObject input = device.GetMethodParameters(method))
        {
            PropertyData inputProperty = null;
            foreach (PropertyData property in input.Properties)
            {
                if (!property.Name.StartsWith("__", StringComparison.Ordinal)) { inputProperty = property; break; }
            }
            if (inputProperty == null)
                throw new InvalidOperationException("Acer firmware did not expose the APGeAction input for " + method + ".");
            input[inputProperty.Name] = ConvertWmiUInt64(inputValue, inputProperty.Type);
            using (ManagementBaseObject result = device.InvokeMethod(method, input, null))
            {
                if (result == null)
                    throw new InvalidOperationException("Acer firmware returned no APGeAction result for " + method + ".");
                foreach (PropertyData property in result.Properties)
                {
                    if (property.Name == "ReturnValue" || property.Name.StartsWith("__", StringComparison.Ordinal)) continue;
                    if (property.Value == null) break;
                    return Convert.ToUInt64(property.Value);
                }
            }
        }
        throw new InvalidOperationException("Acer firmware returned no APGeAction output for " + method + ".");
    }

    private static object ConvertWmiUInt64(ulong value, CimType type)
    {
        if (type == CimType.UInt16) return Convert.ToUInt16(value);
        if (type == CimType.UInt32) return Convert.ToUInt32(value);
        if (type == CimType.SInt32) return Convert.ToInt32(value);
        if (type == CimType.SInt64) return Convert.ToInt64(value);
        return value;
    }

    private static ManagementObject GetApgeAction()
    {
        ManagementObjectSearcher searcher = new ManagementObjectSearcher(
            new ManagementScope("root\\WMI"), new ObjectQuery("SELECT * FROM APGeAction"), WmiOpts.Create());
        using (searcher)
        using (ManagementObjectCollection items = searcher.Get())
        {
            foreach (ManagementObject item in items)
                return (ManagementObject)item.Clone();
        }
        throw new InvalidOperationException("This computer does not expose Acer's APGeAction interface.");
    }

    private void ApplyKeyboardSettings()
    {
        KeyboardEffectProfile selected = effectCombo.SelectedItem as KeyboardEffectProfile;
        if (selected == null)
        {
            ReportKeyboardFailure("checking the computer model", new InvalidOperationException("No verified keyboard lighting profile is available for this computer."));
            return;
        }
        if (!effectCombo.Enabled)
        {
            ReportKeyboardFailure("checking the computer model", new InvalidOperationException("Acer's keyboard lighting interface is not available."));
            return;
        }
        int mode = selected.FirmwareMode;
        int direction = (mode == 3 || mode == 4) ? 1 : 0;
        int speed = speedTrack.Value;
        int brightness = brightnessTrack.Value;
        Color color = lightingColor;
        RunHardwareAsync(delegate
        {
            WriteKeyboardMode(mode, speed, brightness, color, direction);
        }, delegate(Exception ex)
        {
            if (ex == null)
            {
                try
                {
                    lightingStatusLabel.Text = String.Empty;
                    SaveFanAndKeyboardSettings();
                    return;
                }
                catch (Exception uiEx) { ex = uiEx; }
            }
            ReportKeyboardFailure("applying the selected effect", ex);
        });
    }

    private void ReportKeyboardFailure(string step, Exception ex)
    {
        string detail = ex is NullReferenceException
            ? "Acer firmware returned an incomplete WMI object. " + ex.Message
            : ex.Message;
        string diagnosticPath = SaveKeyboardDiagnostic(step, ex);
        if (!String.IsNullOrEmpty(diagnosticPath))
            detail += Environment.NewLine + "Diagnostic details saved to: " + diagnosticPath;
        lightingStatusLabel.Text = "Could not complete " + step + ": " + detail;
        lightingStatusLabel.ForeColor = LightError;
    }

    private static KeyboardCapabilityReport DetectKeyboardCapabilities()
    {
        KeyboardCapabilityReport report = new KeyboardCapabilityReport();
        report.Model = ReadComputerModel();
        report.Effects = new KeyboardEffectProfile[0];
        try
        {
            using (ManagementObject device = GetGamingFunction())
            {
                report.InterfaceFound = true;
                report.CanSetEffect = HasWmiMethod(device, "SetGamingKBBacklight");
            }
        }
        catch { report.InterfaceFound = false; }

        // Acer's WMI interface does not publish an effect-ID list, so the standard Acer effect IDs are used
        // on any computer that exposes the keyboard lighting method.
        if (report.CanSetEffect)
        {
            report.Effects = new KeyboardEffectProfile[] {
                new KeyboardEffectProfile("Neon", 2, false),
                new KeyboardEffectProfile("Wave", 3, false),
                new KeyboardEffectProfile("Shifting", 4, true),
                new KeyboardEffectProfile("Zoom", 5, true)
            };
            report.Status = report.Model + " · Acer keyboard setting interface detected · 4 effects. Current effect readback is not used.";
        }
        else
        {
            report.Status = "No Acer keyboard lighting control interface was detected for " + report.Model + ".";
        }
        return report;
    }

    private static bool HasWmiMethod(ManagementObject device, string method)
    {
        try
        {
            using (ManagementBaseObject parameters = device.GetMethodParameters(method))
                return parameters != null;
        }
        catch { return false; }
    }

    private static string SaveKeyboardDiagnostic(string step, Exception error)
    {
        try
        {
            string folder = Path.Combine(GetAppDataDirectory(), "logs");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "keyboard-errors.log");
            string entry = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " | " + step + Environment.NewLine +
                error.ToString() + Environment.NewLine + Environment.NewLine;
            File.AppendAllText(path, entry);
            return path;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteKeyboardMode(int mode, int speed, int brightness, Color color, int direction)
    {
        byte[] payload = new byte[16];
        payload[0] = (byte)mode;
        payload[1] = (byte)speed;
        payload[2] = (byte)brightness;
        payload[3] = (byte)(mode == 3 ? 8 : 0);
        payload[4] = (byte)direction;
        payload[5] = color.R;
        payload[6] = color.G;
        payload[7] = color.B;
        payload[8] = 0;
        payload[9] = 1;
        InvokeAcerArrayOrUInt64("SetGamingKBBacklight", payload, PackFirstEight(payload));
    }

    private static void InvokeAcerArrayOrUInt64(string method, byte[] bytes, ulong packed)
    {
        using (ManagementObject device = GetGamingFunction())
        using (ManagementBaseObject input = GetRequiredMethodParameters(device, method))
        {
            PropertyData property = input.Properties["gmInput"];
            if (property == null)
                throw new InvalidOperationException("Acer WMI does not expose the expected " + method + " input.");
            input["gmInput"] = property.IsArray ? (object)bytes : (object)packed;
            using (ManagementBaseObject result = device.InvokeMethod(method, input, null))
            {
                if (result == null)
                    throw new InvalidOperationException("Acer firmware returned no result for " + method + ".");
                if (result["gmOutput"] != null && Convert.ToUInt64(result["gmOutput"]) != 0)
                    throw new InvalidOperationException("Acer firmware rejected " + method + ".");
            }
        }
    }

    private static ManagementBaseObject GetRequiredMethodParameters(ManagementObject device, string method)
    {
        if (device == null)
            throw new InvalidOperationException("Acer did not provide a gaming WMI device for " + method + ".");
        ManagementBaseObject input = device.GetMethodParameters(method);
        if (input == null)
            throw new InvalidOperationException("This Acer firmware does not expose the " + method + " keyboard control.");
        return input;
    }

    private static ulong PackFirstEight(byte[] bytes)
    {
        ulong result = 0;
        for (int i = 0; i < bytes.Length && i < 8; i++)
            result |= (ulong)bytes[i] << (i * 8);
        return result;
    }

    private volatile bool powerPlanTouchedByUser;

    private void InitializePowerPlanModes()
    {
        bool loaded = LoadPowerPlanSettings();
        lastPowerLineStatus = SystemInformation.PowerStatus.PowerLineStatus;
        hasPowerSourceState = true;
        showingBatteryPlans = lastPowerLineStatus == PowerLineStatus.Offline;
        if (loaded) UpdatePowerPlanChoiceButtons();
        string desiredPlan = loaded ? GetConfiguredPowerPlan(showingBatteryPlans) : null;

        // powercfg.exe is slow; run it off the UI thread so startup never blocks user clicks.
        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            string activePlan = null;
            try { activePlan = ReadActivePowerPlan(); }
            catch { }
            if (uiClosing) return;

            if (!loaded)
            {
                string initialPlan = IsKnownPowerPlan(activePlan) ? activePlan : PowerPlanSchemes[0];
                PostToUi(delegate
                {
                    if (powerPlanTouchedByUser) return;
                    acPowerPlanScheme = initialPlan;
                    batteryPowerPlanScheme = initialPlan;
                    SavePowerPlanSettings();
                    UpdatePowerPlanChoiceButtons();
                });
            }
            else if (activePlan != null && !String.IsNullOrEmpty(desiredPlan) &&
                !String.Equals(activePlan, desiredPlan, StringComparison.OrdinalIgnoreCase))
            {
                if (!powerPlanTouchedByUser)
                    SetWindowsPowerPlan(desiredPlan, false);
            }
        });
    }

    private static bool IsKnownPowerPlan(string scheme)
    {
        if (String.IsNullOrEmpty(scheme)) return false;
        for (int i = 0; i < PowerPlanSchemes.Length; i++)
            if (String.Equals(scheme, PowerPlanSchemes[i], StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string GetAppDataDirectory()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NitroHub");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string GetPowerPlanSettingsPath()
    {
        return Path.Combine(GetAppDataDirectory(), "power-plans.ini");
    }

    private void SaveFanAndKeyboardSettings()
    {
        try
        {
            SafeFile.WriteAllLines(Path.Combine(GetAppDataDirectory(), "fan-settings.ini"), new string[]
            {
                "Mode=" + currentFanMode,
                "CpuSpeed=" + cpuFanTrack.Value,
                "GpuSpeed=" + gpuFanTrack.Value
            });
            SafeFile.WriteAllLines(Path.Combine(GetAppDataDirectory(), "keyboard-settings.ini"), new string[]
            {
                "EffectIndex=" + (effectCombo != null ? effectCombo.SelectedIndex : 0),
                "Brightness=" + (brightnessTrack != null ? brightnessTrack.Value : 70),
                "Speed=" + (speedTrack != null ? speedTrack.Value : 5),
                "Color=" + lightingColor.ToArgb()
            });
        }
        catch { }
    }

    private bool? cachedChargeLimitOn;
    private bool restoringSavedFanSettings;

    private void LoadSavedFanAndKeyboardSettings()
    {
        // Startup restore must never block on a dialog (it also runs for silent --minimized logons); errors go to the startup log instead.
        restoringSavedFanSettings = true;
        try
        {
            string fanPath = Path.Combine(GetAppDataDirectory(), "fan-settings.ini");
            if (File.Exists(fanPath))
            {
                byte savedMode = 1;
                int cpuSpd = 70, gpuSpd = 70;
                foreach (string line in File.ReadAllLines(fanPath))
                {
                    string[] kv = line.Split('=');
                    if (kv.Length != 2) continue;
                    if (kv[0] == "Mode") Byte.TryParse(kv[1], out savedMode);
                    else if (kv[0] == "CpuSpeed") Int32.TryParse(kv[1], out cpuSpd);
                    else if (kv[0] == "GpuSpeed") Int32.TryParse(kv[1], out gpuSpd);
                }
                cpuFanTrack.Value = Math.Max(40, Math.Min(100, cpuSpd));
                gpuFanTrack.Value = Math.Max(40, Math.Min(100, gpuSpd));
                if (savedMode == 2) SetFanMode(2);
                else if (savedMode == 0) SetCustomFanSpeeds();
                else RestoreAutoFanModeAtStartup(); // the EC may still be in Max/custom after a crash or power loss
            }

            string kbPath = Path.Combine(GetAppDataDirectory(), "keyboard-settings.ini");
            if (File.Exists(kbPath) && keyboardEffectControlAvailable)
            {
                int effIdx = 0, bright = 70, spd = 5, argb = Color.Red.ToArgb();
                foreach (string line in File.ReadAllLines(kbPath))
                {
                    string[] kv = line.Split('=');
                    if (kv.Length != 2) continue;
                    if (kv[0] == "EffectIndex") Int32.TryParse(kv[1], out effIdx);
                    else if (kv[0] == "Brightness") Int32.TryParse(kv[1], out bright);
                    else if (kv[0] == "Speed") Int32.TryParse(kv[1], out spd);
                    else if (kv[0] == "Color") Int32.TryParse(kv[1], out argb);
                }
                lightingColor = Color.FromArgb(argb);
                if (lightingColorPreview != null) lightingColorPreview.BackColor = lightingColor;
                brightnessTrack.Value = Math.Max(0, Math.Min(100, bright));
                speedTrack.Value = Math.Max(0, Math.Min(9, spd));
                if (effIdx >= 0 && effIdx < effectCombo.Items.Count)
                    effectCombo.SelectedIndex = effIdx;
                UpdateLightingColorControl();
                ApplyKeyboardSettings();
            }
        }
        catch { }
        finally { restoringSavedFanSettings = false; }
    }

    private bool LoadPowerPlanSettings()
    {
        try
        {
            string path = GetPowerPlanSettingsPath();
            if (!File.Exists(path)) return false;
            string acPlan = null;
            string batteryPlan = null;
            string[] lines = File.ReadAllLines(path);
            foreach (string line in lines)
            {
                int separator = line.IndexOf('=');
                if (separator < 0) continue;
                string key = line.Substring(0, separator).Trim();
                string value = line.Substring(separator + 1).Trim();
                if (String.Equals(key, "AC", StringComparison.OrdinalIgnoreCase)) acPlan = value;
                else if (String.Equals(key, "Battery", StringComparison.OrdinalIgnoreCase)) batteryPlan = value;
            }
            if (!IsKnownPowerPlan(acPlan) || !IsKnownPowerPlan(batteryPlan)) return false;
            acPowerPlanScheme = acPlan;
            batteryPowerPlanScheme = batteryPlan;
            return true;
        }
        catch { return false; }
    }

    private void SavePowerPlanSettings()
    {
        try
        {
            string path = GetPowerPlanSettingsPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            SafeFile.WriteAllLines(path, new string[] { "AC=" + acPowerPlanScheme, "Battery=" + batteryPowerPlanScheme });
        }
        catch { }
    }

    private string GetConfiguredPowerPlan(bool battery)
    {
        return battery ? batteryPowerPlanScheme : acPowerPlanScheme;
    }

    private void UpdatePowerPlanChoiceButtons()
    {
        for (int sourceIndex = 0; sourceIndex < 2; sourceIndex++)
        {
            bool battery = sourceIndex == 1;
            string selectedPlan = GetConfiguredPowerPlan(battery);
            for (int planIndex = 0; planIndex < PowerPlanSchemes.Length; planIndex++)
            {
                int buttonIndex = sourceIndex * PowerPlanSchemes.Length + planIndex;
                Button button = powerPlanButtons[buttonIndex];
                if (button == null) continue;
                button.Tag = String.Equals(selectedPlan, PowerPlanSchemes[planIndex], StringComparison.OrdinalIgnoreCase)
                    ? "accent" : "state-off";
                ApplyStateButtonAppearance(button);
            }
        }
    }

    private void SelectPowerPlan(int planIndex)
    {
        powerPlanTouchedByUser = true;
        string selectedPlan = PowerPlanSchemes[planIndex];
        if (showingBatteryPlans) batteryPowerPlanScheme = selectedPlan;
        else acPowerPlanScheme = selectedPlan;
        SavePowerPlanSettings();
        UpdatePowerPlanChoiceButtons();

        PowerLineStatus currentStatus = SystemInformation.PowerStatus.PowerLineStatus;
        bool currentIsBattery = currentStatus == PowerLineStatus.Offline;
        if (currentIsBattery == showingBatteryPlans)
        {
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                SetWindowsPowerPlan(selectedPlan, true);
            });
        }
    }

    private void CheckPowerSourceTransition()
    {
        PowerLineStatus currentStatus = SystemInformation.PowerStatus.PowerLineStatus;
        if (currentStatus != PowerLineStatus.Online && currentStatus != PowerLineStatus.Offline) return;
        if (hasPowerSourceState && currentStatus == lastPowerLineStatus) return;

        hasPowerSourceState = true;
        lastPowerLineStatus = currentStatus;
        showingBatteryPlans = currentStatus == PowerLineStatus.Offline;
        try { UpdatePowerPlanChoiceButtons(); } catch { }
        string planToApply = GetConfiguredPowerPlan(showingBatteryPlans);
        // powercfg.exe is slow; run it off the UI thread so the power-source switch never freezes the window.
        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            try { SetWindowsPowerPlan(planToApply, false); } catch { }
        });
    }

    private static string ReadActivePowerPlan()
    {
        ProcessStartInfo start = new ProcessStartInfo
        {
            FileName = "powercfg.exe", Arguments = "/getactivescheme",
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        using (Process process = Process.Start(start))
        {
            if (process == null) throw new InvalidOperationException("Windows could not start powercfg.exe.");
            if (!process.WaitForExit(5000))
                throw new InvalidOperationException("Windows did not return the active power plan in time.");
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            if (process.ExitCode != 0)
                throw new InvalidOperationException(String.IsNullOrWhiteSpace(error)
                ? "Windows could not read the active power plan."
                : error.Trim());
            Match match = Regex.Match(output, @"[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}");
            if (!match.Success)
                throw new InvalidOperationException("Windows returned an unrecognized active power plan.");
            return match.Value;
        }
    }

    private bool SetWindowsPowerPlan(string scheme, bool showErrors)
    {
        try
        {
            ProcessStartInfo start = new ProcessStartInfo
            {
                FileName = "powercfg.exe",
                Arguments = "/setactive " + scheme,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (Process process = Process.Start(start))
            {
                if (process == null || !process.WaitForExit(5000))
                    throw new InvalidOperationException("Windows did not finish changing the power plan in time.");
                if (!process.HasExited || process.ExitCode != 0)
                    throw new InvalidOperationException("Windows could not activate that power plan. It may not be installed.");
            }
            if (!String.Equals(ReadActivePowerPlan(), scheme, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Windows did not confirm the selected power plan.");
            return true;
        }
        catch (Exception ex)
        {
            if (showErrors)
            {
                string message = ex.Message;
                PostToUi(delegate
                {
                    MessageBox.Show(message, Tr("Power plan unavailable", "电源计划不可用"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                });
            }
            return false;
        }
    }

    private static ManagementObject GetBatteryControl()
    {
        ManagementObjectSearcher searcher = new ManagementObjectSearcher(
            new ManagementScope("root\\WMI"), new ObjectQuery("SELECT * FROM BatteryControl"), WmiOpts.Create());
        using (searcher)
        using (ManagementObjectCollection items = searcher.Get())
        {
            foreach (ManagementObject item in items)
                return (ManagementObject)item.Clone();
        }
        throw new InvalidOperationException("This computer does not expose the Acer BatteryControl interface.");
    }

    private static bool ReadChargeLimitState()
    {
        using (ManagementObject device = GetBatteryControl())
        using (ManagementBaseObject input = device.GetMethodParameters("GetBatteryHealthControlStatus"))
        {
            input["uBatteryNo"] = (byte)1;
            input["uFunctionQuery"] = (byte)1;
            input["uReserved"] = new byte[] { 0, 0 };
            using (ManagementBaseObject result = device.InvokeMethod("GetBatteryHealthControlStatus", input, null))
            {
                if (result == null || result["uFunctionList"] == null)
                    throw new InvalidOperationException("Acer firmware did not return the battery limit status.");
                byte functionList = Convert.ToByte(result["uFunctionList"]);
                if ((functionList & 1) == 0)
                    throw new InvalidOperationException("Firmware reports that this model does not support the 80% limit.");
                Array status = result["uFunctionStatus"] as Array;
                if (status == null || status.Length == 0)
                    throw new InvalidOperationException("Acer firmware returned an invalid battery status.");
                return Convert.ToByte(status.GetValue(0)) != 0;
            }
        }
    }

    private static string ChargeLimitPreferencePath()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NitroHub", "charge-limit-preference.txt");
    }

    private static bool? ReadSavedChargeLimitPreference()
    {
        try
        {
            string path = ChargeLimitPreferencePath();
            if (!File.Exists(path)) return null;
            string value = File.ReadAllText(path).Trim();
            if (value == "1") return true;
            if (value == "0") return false;
        }
        catch { }
        return null;
    }

    private static void SaveChargeLimitPreference(bool enabled)
    {
        string path = ChargeLimitPreferencePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        SafeFile.WriteAllText(path, enabled ? "1" : "0", Encoding.UTF8);
    }

    internal static void ApplySavedChargeLimitAtStartup()
    {
        try
        {
            bool? preferred = ReadSavedChargeLimitPreference();
            if (!preferred.HasValue) return;
            if (ReadChargeLimitState() != preferred.Value)
                WriteChargeLimitState(preferred.Value);
            WriteChargeLimitStartupLog("Saved charge limit preference applied: " + (preferred.Value ? "80% limit ON." : "80% limit OFF."));
        }
        catch (Exception ex)
        {
            WriteChargeLimitStartupLog("Could not apply the saved charge limit preference: " + ex.Message);
        }
    }

    internal static void ApplySavedBluelightAtStartup()
    {
        try
        {
            int level = BluelightShield.ReadSavedLevel();
            if (level <= 0) return;
            bool softened;
            int displays = BluelightShield.Apply(level, out softened);
            WriteChargeLimitStartupLog("Saved Bluelight Shield level " + BluelightShield.GetName(level) +
                " applied to " + displays + " display(s).");
            // Windows and the graphics driver often reset the color curve shortly after sign-in.
            int[] waits = new int[] { 5000, 15000 };
            foreach (int wait in waits)
            {
                System.Threading.Thread.Sleep(wait);
                BluelightShield.Apply(level, out softened);
            }
        }
        catch (Exception ex)
        {
            WriteChargeLimitStartupLog("Could not apply the saved Bluelight Shield level: " + ex.Message);
        }
    }

    private const string StartupTaskName = "NitroHub Startup";

    private static bool IsStartupTaskInstalled()
    {
        try
        {
            ProcessStartInfo start = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                Arguments = "/Query /TN \"" + StartupTaskName + "\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using (Process p = Process.Start(start))
            {
                p.WaitForExit(3000);
                return p.ExitCode == 0;
            }
        }
        catch { return false; }
    }

    private static string EnsureChargeLimitStartupTask()
    {
        string xmlPath = null;
        try
        {
            string executable = Program.GetExecutablePath();
            string workDir = Path.GetDirectoryName(executable) ?? String.Empty;
            string userId;
            try { userId = System.Security.Principal.WindowsIdentity.GetCurrent().Name; }
            catch { userId = Environment.UserDomainName + "\\" + Environment.UserName; }

            // Use Task Scheduler XML so the task can start and stay running on battery power and never stops after 72 hours.
            string xml =
                "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
                "  <Triggers>\r\n" +
                "    <LogonTrigger>\r\n" +
                "      <Enabled>true</Enabled>\r\n" +
                "      <UserId>" + System.Security.SecurityElement.Escape(userId) + "</UserId>\r\n" +
                "    </LogonTrigger>\r\n" +
                "  </Triggers>\r\n" +
                "  <Principals>\r\n" +
                "    <Principal id=\"Author\">\r\n" +
                "      <UserId>" + System.Security.SecurityElement.Escape(userId) + "</UserId>\r\n" +
                "      <LogonType>InteractiveToken</LogonType>\r\n" +
                "      <RunLevel>HighestAvailable</RunLevel>\r\n" +
                "    </Principal>\r\n" +
                "  </Principals>\r\n" +
                "  <Settings>\r\n" +
                "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
                "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
                "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
                "    <AllowHardTerminate>true</AllowHardTerminate>\r\n" +
                "    <StartWhenAvailable>true</StartWhenAvailable>\r\n" +
                "    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>\r\n" +
                "    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>\r\n" +
                "    <AllowStartOnDemand>true</AllowStartOnDemand>\r\n" +
                "    <Enabled>true</Enabled>\r\n" +
                "    <Hidden>false</Hidden>\r\n" +
                "    <RunOnlyIfIdle>false</RunOnlyIfIdle>\r\n" +
                "    <WakeToRun>false</WakeToRun>\r\n" +
                "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n" +
                "    <Priority>7</Priority>\r\n" +
                "  </Settings>\r\n" +
                "  <Actions Context=\"Author\">\r\n" +
                "    <Exec>\r\n" +
                "      <Command>" + System.Security.SecurityElement.Escape(executable) + "</Command>\r\n" +
                "      <Arguments>--minimized</Arguments>\r\n" +
                "      <WorkingDirectory>" + System.Security.SecurityElement.Escape(workDir) + "</WorkingDirectory>\r\n" +
                "    </Exec>\r\n" +
                "  </Actions>\r\n" +
                "</Task>\r\n";

            xmlPath = Path.Combine(Path.GetTempPath(), "NitroHub-Startup-" + Guid.NewGuid().ToString("N") + ".xml");
            File.WriteAllText(xmlPath, xml, Encoding.Unicode);

            string arguments = "/Create /F /TN \"" + StartupTaskName + "\" /XML \"" + xmlPath + "\"";
            ProcessStartInfo start = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (Process process = Process.Start(start))
            {
                var outputTask = System.Threading.Tasks.Task.Factory.StartNew(() => process.StandardOutput.ReadToEnd());
                var errorTask = System.Threading.Tasks.Task.Factory.StartNew(() => process.StandardError.ReadToEnd());
                if (!process.WaitForExit(15000))
                {
                    try { process.Kill(); } catch { }
                    throw new TimeoutException("schtasks.exe did not respond in time.");
                }
                string output = outputTask.Wait(3000) ? outputTask.Result : String.Empty;
                string error = errorTask.Wait(3000) ? errorTask.Result : String.Empty;
                if (process.ExitCode == 0)
                {
                    RemoveScheduledTask("NitroHub Charge Limit");
                    WriteChargeLimitStartupLog("Silent elevated startup task is installed or updated.");
                    return null;
                }
                string detail = (error + " " + output).Trim();
                WriteChargeLimitStartupLog("Could not install Windows sign-in task: " + detail);
                return detail;
            }
        }
        catch (Exception ex)
        {
            WriteChargeLimitStartupLog("Could not install Windows sign-in task: " + ex.Message);
            return ex.Message;
        }
        finally
        {
            if (xmlPath != null)
            {
                try { File.Delete(xmlPath); } catch { }
            }
        }
    }

    private static void RemoveScheduledTask(string taskName)
    {
        try
        {
            ProcessStartInfo start = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                Arguments = "/Delete /F /TN \"" + taskName + "\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using (Process p = Process.Start(start)) { p.WaitForExit(3000); }
        }
        catch { }
    }

    private static void WriteChargeLimitStartupLog(string message)
    {
        try
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NitroHub", "logs");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "charge-limit-startup.log"),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " | " + message + Environment.NewLine, Encoding.UTF8);
        }
        catch { }
    }

    private static bool ReadBatteryCalibrationState()
    {
        using (ManagementObject device = GetBatteryControl())
        using (ManagementBaseObject input = device.GetMethodParameters("GetBatteryHealthControlStatus"))
        {
            input["uBatteryNo"] = (byte)1;
            // Query the full status block; the documented result includes both modes.
            input["uFunctionQuery"] = (byte)1;
            input["uReserved"] = new byte[] { 0, 0 };
            using (ManagementBaseObject result = device.InvokeMethod("GetBatteryHealthControlStatus", input, null))
            {
                if (result == null || result["uFunctionList"] == null || result["uFunctionStatus"] == null)
                    throw new InvalidOperationException("Acer firmware returned no calibration status.");
                if ((Convert.ToByte(result["uFunctionList"]) & 0x02) == 0)
                    throw new InvalidOperationException("This firmware does not advertise battery calibration support.");
                Array status = result["uFunctionStatus"] as Array;
                if (status == null || status.Length < 2)
                    throw new InvalidOperationException("Acer firmware returned an incomplete calibration status.");
                return Convert.ToByte(status.GetValue(1)) != 0;
            }
        }
    }

    private static void WriteBatteryCalibrationState(bool enabled)
    {
        using (ManagementObject device = GetBatteryControl())
        using (ManagementBaseObject input = device.GetMethodParameters("SetBatteryHealthControl"))
        {
            input["uBatteryNo"] = (byte)1;
            input["uFunctionMask"] = (byte)2;
            input["uFunctionStatus"] = (byte)(enabled ? 1 : 0);
            input["uReservedIn"] = new byte[] { 0, 0, 0, 0, 0 };
            using (ManagementBaseObject result = device.InvokeMethod("SetBatteryHealthControl", input, null))
            {
                if (result == null)
                    throw new InvalidOperationException("Acer firmware returned no result for the calibration change.");
            }
        }
        if (ReadBatteryCalibrationState() != enabled)
            throw new InvalidOperationException("Firmware did not confirm the calibration state change.");
    }

    private void RefreshBatteryCalibrationState()
    {
        bool enabled = false;
        RunHardwareAsync(delegate { enabled = ReadBatteryCalibrationState(); }, delegate(Exception ex)
        {
            if (ex == null)
            {
                calibrationStateLabel.Visible = false;
                calibrationToggle.Checked = enabled;
                calibrationToggle.Enabled = true;
            }
            else
            {
                calibrationStateLabel.Text = "N/A";
                calibrationStateLabel.ForeColor = LightError;
                calibrationStateLabel.Visible = true;
                calibrationToggle.Enabled = false;
            }
        });
    }

    private void SetBatteryCalibration(bool enabled)
    {
        bool currentState = false;
        RunHardwareAsync(delegate { currentState = ReadBatteryCalibrationState(); }, delegate(Exception readError)
        {
            if (readError != null) { ReportBatteryCalibrationError(readError); return; }
            if (enabled && !currentState && MessageBox.Show(
                Tr("Battery calibration changes firmware charging behavior. It may temporarily disable the 80% limit and charge to 100%. Start only when you intend to calibrate the battery. Continue?",
                "电池校准会更改固件充电策略，可能会暂时关闭 80% 充电上限并将电池充至 100%。仅在您确实需要校准电池时启动。是否继续？"),
                Tr("Start battery calibration?", "开始电池校准？"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                RefreshBatteryCalibrationState();
                return;
            }
            if (currentState == enabled) { RefreshBatteryCalibrationState(); return; }
            RunHardwareAsync(delegate { WriteBatteryCalibrationState(enabled); }, delegate(Exception writeError)
            {
                if (writeError != null) { ReportBatteryCalibrationError(writeError); return; }
                RefreshBatteryCalibrationState();
            });
        });
    }

    private void ReportBatteryCalibrationError(Exception ex)
    {
        MessageBox.Show(ex.Message, Tr("Battery calibration unavailable", "电池校准不可用"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        try { RefreshBatteryCalibrationState(); } catch { }
    }

    private static void WriteChargeLimitState(bool enabled)
    {
        using (ManagementObject device = GetBatteryControl())
        using (ManagementBaseObject input = device.GetMethodParameters("SetBatteryHealthControl"))
        {
            input["uBatteryNo"] = (byte)1;
            input["uFunctionMask"] = (byte)1;
            input["uFunctionStatus"] = (byte)(enabled ? 1 : 0);
            input["uReservedIn"] = new byte[] { 0, 0, 0, 0, 0 };
            using (ManagementBaseObject result = device.InvokeMethod("SetBatteryHealthControl", input, null))
            {
                if (result == null)
                    throw new InvalidOperationException("Acer firmware returned no result for the setting change.");
            }
        }
        if (ReadChargeLimitState() != enabled)
            throw new InvalidOperationException("Firmware did not confirm that the setting was applied.");
    }

    private static FlowLayoutPanel CreateOverviewRows(Panel card)
    {
        FlowLayoutPanel rows = new FlowLayoutPanel
        {
            Location = new Point(18, 48), Size = new Size(354, 440), AutoSize = false,
            AutoScroll = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, Padding = new Padding(0), Margin = new Padding(0),
            BackColor = Color.Transparent, Tag = "card"
        };
        card.Controls.Add(rows);
        return rows;
    }

    private Label AddOverviewInfoRow(FlowLayoutPanel rows, string caption, string value, bool isModel, bool includeCopyButton = false)
    {
        string displayValue = String.IsNullOrWhiteSpace(value) ? "Unavailable" : value;
        Font valueFont = UiLang.GetCachedFont("Segoe UI", 9F, FontStyle.Regular);
        int valueWidth;
        int copyButtonX;
        int valueHeight;
        if (includeCopyButton)
        {
            int measuredSingleW = (int)Math.Round(TextRenderer.MeasureText(displayValue, valueFont).Width / UiDpi.Scale);
            valueWidth = Math.Min(350 - 24, measuredSingleW + 4);
            copyButtonX = valueWidth + 3;
            valueHeight = 22;
        }
        else
        {
            valueWidth = 350;
            copyButtonX = 350;
            Size measured = TextRenderer.MeasureText(displayValue, valueFont, new Size((int)Math.Round(valueWidth * UiDpi.Scale), 1000), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
            valueHeight = Math.Max(22, (int)Math.Round(measured.Height / UiDpi.Scale) + 2);
        }
        Panel row = new Panel
        {
            Size = new Size(350, 18 + Math.Max(valueHeight, 22) + 4), Margin = new Padding(0, 0, 0, 12),
            Padding = new Padding(0), BackColor = Color.Transparent, Tag = "card"
        };
        row.Controls.Add(new Label
        {
            Text = caption.ToUpperInvariant(), Font = UiLang.GetCachedFont("Segoe UI", 7.5F, FontStyle.Regular),
            ForeColor = Muted, Location = new Point(0, 0), Size = new Size(350, 15)
        });
        Label valueLabel = new Label
        {
            Text = displayValue,
            Font = valueFont,
            ForeColor = Color.FromArgb(39, 57, 74), Location = new Point(0, 18),
            Size = new Size(valueWidth, valueHeight), AutoEllipsis = includeCopyButton, TextAlign = ContentAlignment.MiddleLeft
        };
        row.Controls.Add(valueLabel);
        if (includeCopyButton)
        {
            Button copyButton = new CopyIconButton
            {
                Name = "overview-copy", Text = String.Empty, Location = new Point(copyButtonX, 18 + (valueHeight - 18) / 2),
                Size = new Size(18, 18), FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(49, 59, 70), ForeColor = Color.FromArgb(245, 247, 250),
                Font = UiLang.GetCachedFont("Segoe UI Semibold", 8.5F, FontStyle.Regular), UseVisualStyleBackColor = false,
                Tag = "state-off", Enabled = displayValue != "Unavailable", AccessibleName = "Copy serial number"
            };
            copyButton.FlatAppearance.BorderColor = Color.FromArgb(112, 127, 142);
            copyButton.FlatAppearance.BorderSize = 1;
            copyButton.Click += delegate
            {
                try { Clipboard.SetText(displayValue); toolTips.SetToolTip(copyButton, "Serial number copied."); }
                catch { }
            };
            row.Controls.Add(copyButton);
            toolTips.SetToolTip(copyButton, "Copy the serial number to the clipboard.");
            overviewSerialLabel = valueLabel;
            overviewSerialCopyButton = copyButton;
        }
        rows.Controls.Add(row);
        toolTips.SetToolTip(valueLabel, displayValue);
        return valueLabel;
    }

    private static string ReadWmiString(string query, string propertyName)
    {
        try
        {
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(new ManagementScope(@"root\CIMV2"), new ObjectQuery(query), WmiOpts.Create()))
            using (ManagementObjectCollection items = searcher.Get())
            {
                foreach (ManagementObject item in items)
                    using (item)
                {
                    string value = Convert.ToString(item[propertyName]);
                    if (!String.IsNullOrWhiteSpace(value)) return value.Trim();
                }
            }
        }
        catch { }
        return "Unavailable";
    }

    private static string ReadComputerSerial()
    {
        string serial = ReadWmiString("SELECT SerialNumber FROM Win32_BIOS", "SerialNumber");
        if (IsUnavailableAcerId(serial))
            serial = ReadWmiString("SELECT IdentifyingNumber FROM Win32_ComputerSystemProduct", "IdentifyingNumber");
        return IsUnavailableAcerId(serial) ? "Unavailable" : serial;
    }

    private static string ReadProcessorName()
    {
        return ReadHardwareLines("SELECT Name FROM Win32_Processor", "Name");
    }

    private static string ReadGraphicsNames()
    {
        try
        {
            List<string> names = new List<string>();
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(new ManagementScope(@"root\CIMV2"), new ObjectQuery("SELECT Name FROM Win32_VideoController"), WmiOpts.Create()))
            using (ManagementObjectCollection items = searcher.Get())
            {
                foreach (ManagementObject item in items)
                    using (item)
                {
                    string name = Convert.ToString(item["Name"]);
                    if (!String.IsNullOrWhiteSpace(name)) names.Add(name.Trim());
                }
            }
            return names.Count == 0 ? "Unavailable" : String.Join(Environment.NewLine, names.ToArray());
        }
        catch { return "Unavailable"; }
    }

    private static List<string> ReadGraphicsNameList()
    {
        string names = ReadGraphicsNames();
        if (names == "Unavailable") return new List<string>();
        return new List<string>(names.Split(new string[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries));
    }

    private static List<string> FindGraphicsAdapters(List<string> adapters, params string[] markers)
    {
        List<string> matches = new List<string>();
        foreach (string adapter in adapters)
            foreach (string marker in markers)
            if (adapter.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            matches.Add(adapter);
            break;
        }
        return matches;
    }

    private static List<string> FindOtherGraphicsAdapters(List<string> adapters)
    {
        List<string> other = new List<string>();
        foreach (string adapter in adapters)
        {
            if (FindGraphicsAdapters(new List<string> { adapter }, "NVIDIA", "GeForce", "RTX", "GTX", "AMD", "Radeon", "Intel").Count == 0)
            other.Add(adapter);
        }
        return other;
    }

    private ModernSettingsPanel settingsPanel;

    private string currentLanguage = UiLang.English;

    private string Tr(string en, string zh)
    {
        return UiLang.Pick(currentLanguage, en, zh);
    }

    private string TrKey(string en)
    {
        return TranslateKey(currentLanguage, en) ?? en;
    }

    private static string TranslateKey(string lang, string en)
    {
        if (UiLang.IsEnglish(lang)) return en;
        return UiLang.Lookup(lang, en);
    }

    private void ApplyGlobalLanguage(string lang)
    {
        currentLanguage = lang;
        string[] navEn = new string[] { "Overview", "Monitor", "Battery", "Tuning", "Keyboard", "Support", "Others", "Settings" };
        for (int i = 0; i < navigationButtons.Length && i < navEn.Length; i++)
        {
            if (navigationButtons[i] == null) continue;
            navigationButtons[i].Text = UiLang.IsEnglish(lang) ? navEn[i] : (UiLang.Lookup(lang, navEn[i]) ?? navEn[i]);
        }
        ApplyLanguageRecursive(this, lang);
        if (bluelightSegment != null)
            bluelightSegment.Items = new string[] { TrKey("Low"), TrKey("Medium"), TrKey("High"), TrKey("Long Hour") };
        UiLang.ApplyFontToTree(this, lang);
        if (trayMenu != null)
        {
            Font newTrayFont = new Font(UiLang.GetUiFontFamily(lang), 9F);
            trayMenu.Font = newTrayFont;
            if (trayMenuFont != null) trayMenuFont.Dispose();
            trayMenuFont = newTrayFont;
            RebuildTrayContextMenu();
        }
        if (temperatureChart != null) temperatureChart.Invalidate();
        ReflowSerialCopyButtons();
    }

    private static readonly string[] TranslatablePrefixes = new string[]
    {
        "CPU fan: ", "GPU fan: ", "Model: ", "Serial number: "
    };

    private static readonly Dictionary<string, string> AllToEnglish = BuildAllToEnglish();

    private static Dictionary<string, string> BuildAllToEnglish()
    {
        Dictionary<string, string> rev = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Dictionary<string, string> pack in UiLang.AllPacks)
            foreach (KeyValuePair<string, string> kv in pack)
                if (!rev.ContainsKey(kv.Value)) rev[kv.Value] = kv.Key;
        return rev;
    }

    private readonly Dictionary<Control, KeyValuePair<string, string>> languageState = new Dictionary<Control, KeyValuePair<string, string>>();

    private static string PrefixText(string lang, string prefix)
    {
        if (UiLang.IsEnglish(lang)) return prefix;
        return UiLang.Lookup(lang, prefix) ?? prefix;
    }

    private static bool IsKnownEnglishKey(string key)
    {
        foreach (Dictionary<string, string> pack in UiLang.AllPacks)
            if (pack.ContainsKey(key)) return true;
        return false;
    }

    private void ApplyLanguageRecursive(Control parent, string lang)
    {
        foreach (Control c in parent.Controls)
        {
            if (c == settingsPanel || c == navigationPanel) continue;
            string txt = c.Text;
            if (!String.IsNullOrEmpty(txt))
            {
                bool handledPrefix = false;
                foreach (string prefix in TranslatablePrefixes)
                {
                    foreach (string other in UiLang.Languages)
                    {
                        string op = PrefixText(other, prefix);
                        if (txt.StartsWith(op, StringComparison.Ordinal))
                        {
                            c.Text = PrefixText(lang, prefix) + txt.Substring(op.Length);
                            handledPrefix = true;
                            break;
                        }
                    }
                    if (handledPrefix) break;
                }
                if (!handledPrefix)
                {
                    KeyValuePair<string, string> rec;
                    string key;
                    if (languageState.TryGetValue(c, out rec) && rec.Key == txt) key = rec.Value;
                    else
                    {
                        string mappedKey;
                        key = AllToEnglish.TryGetValue(txt, out mappedKey) ? mappedKey : txt;
                    }
                    if (IsKnownEnglishKey(key))
                    {
                        string target = TranslateKey(lang, key) ?? key;
                        if (target != txt) c.Text = target;
                        languageState[c] = new KeyValuePair<string, string>(c.Text, key);
                    }
                }
            }
            ApplyLanguageRecursive(c, lang);
        }
    }

    private void BuildFeaturesPage(Panel page)
    {
        page.Controls.Add(new Label
        {
            Text = "OTHER FEATURES", Font = new Font("Segoe UI Semibold", 18F),
            ForeColor = Color.FromArgb(39, 57, 74), Location = new Point(20, 18), Size = new Size(770, 34), Tag = "section-heading"
        });
        page.Controls.Add(new Label
        {
            Text = "Device power and display features.",
            Font = new Font("Segoe UI", 9.5F), ForeColor = Muted,
            Location = new Point(22, 62), Size = new Size(770, 22)
        });

        BuildPowerOffUsbCard(page);
        BuildBluelightCard(page);
    }

    // Power-off USB charging (Acer APGeAction firmware interface)

    // Feature cards: same height; each option cell of the segmented controls has the same width in both cards,
    // so the Bluelight card (4 options) is wider than the USB card (3 options).
    private const int FeatureCardHeight = 246;
    private const int FeatureOptionLabelY = 114;
    private const int FeatureOptionSegmentY = 140;
    private const int FeatureCardGap = 24;
    private const int FeatureSegmentCellWidth = 96;
    private const int UsbCardWidth = 376;
    private const int BluelightCardWidth = 424;

    private const ulong UsbChargeQueryInput = 0x4UL;
    private static readonly int[] UsbChargeLevels = new int[] { 0, 10, 20, 30 };
    // Firmware replies to GetFunction(0x4) for: off, 10%, 20%, 30%.
    private static readonly ulong[] UsbChargeReadValues = new ulong[] { 663296UL, 659200UL, 1314560UL, 1969920UL };
    // SetFunction inputs for: off, 10%, 20%, 30%.
    private static readonly ulong[] UsbChargeWriteValues = new ulong[] { 663300UL, 659204UL, 1314564UL, 1969924UL };

    private void BuildPowerOffUsbCard(Panel page)
    {
        Panel card = new Panel
        {
            Location = new Point(18, 96), Size = new Size(UsbCardWidth, FeatureCardHeight),
            BackColor = Color.FromArgb(34, 41, 49), BorderStyle = BorderStyle.None, Tag = "card"
        };
        page.Controls.Add(card);
        card.Controls.Add(new Label
        {
            Text = "POWER-OFF USB CHARGING", Location = new Point(20, 22), Size = new Size(UsbCardWidth - 152, 24),
            Font = new Font("Segoe UI Semibold", 10.5F), ForeColor = SharedHeadingColor, Tag = "section-heading"
        });
        card.Controls.Add(new Label
        {
            Text = "Charges USB devices while the laptop is off.",
            Location = new Point(20, 48), Size = new Size(UsbCardWidth - 40, 20),
            Font = new Font("Segoe UI", 8.5F), ForeColor = Muted
        });

        usbChargeToggle = new ToggleSwitch
        {
            Name = "usb-charge-toggle", Location = new Point(UsbCardWidth - 20 - 68, 19), Size = new Size(68, 30), ShowStateText = true,
            Enabled = false, AccessibleName = "Power-off USB charging"
        };
        usbChargeToggle.Toggled += delegate
        {
            if (updatingUsbChargeUi) return;
            bool isOn = usbChargeToggle.Checked;
            // Optimistic UI update; reverted if the firmware write fails.
            usbChargeSegment.Enabled = isOn;
            int targetIndex = isOn ? lastUsbChargeOptionIndex : 0;
            SetPowerOffUsbChargingFromUi(targetIndex);
        };
        card.Controls.Add(usbChargeToggle);

        card.Controls.Add(new Label
        {
            Text = "Stop at battery level", Location = new Point(20, FeatureOptionLabelY), Size = new Size(UsbCardWidth - 40, 18),
            Font = new Font("Segoe UI Semibold", 8.5F), ForeColor = SharedHeadingColor, Tag = "section-heading"
        });

        usbChargeSegment = new SegmentedControl
        {
            Name = "usb-charge-segment", Location = new Point(20, FeatureOptionSegmentY), Size = new Size(3 * FeatureSegmentCellWidth, 38),
            Items = new string[] { "10%", "20%", "30%" },
            SelectedIndex = 0, Enabled = false
        };
        usbChargeSegment.SelectedIndexChanged += delegate
        {
            if (updatingUsbChargeUi) return;
            lastUsbChargeOptionIndex = usbChargeSegment.SelectedIndex + 1;
            if (usbChargeToggle.Checked)
                SetPowerOffUsbChargingFromUi(lastUsbChargeOptionIndex);
        };
        card.Controls.Add(usbChargeSegment);
    }

    private static bool HasApgeActionMethod(string method)
    {
        try
        {
            using (ManagementObject device = GetApgeAction())
            using (ManagementBaseObject parameters = device.GetMethodParameters(method))
                return parameters != null;
        }
        catch { return false; }
    }

    private static string GetPowerOffUsbUnavailableReason()
    {
        if (!IsAcerApgeInterfaceAvailable())
            return "Acer's APGeAction firmware interface was not detected on this computer.";
        return null;
    }

    private static int ReadPowerOffUsbChargingIndex(out ulong rawValue)
    {
        rawValue = InvokeApgeFunction("GetFunction", UsbChargeQueryInput);
        for (int i = 0; i < UsbChargeReadValues.Length; i++)
            if (UsbChargeReadValues[i] == rawValue) return i;
        return -1;
    }

    private void WritePowerOffUsbCharging(int index)
    {
        if (index < 0 || index >= UsbChargeWriteValues.Length)
            throw new ArgumentOutOfRangeException("index");
        string unavailable = GetPowerOffUsbUnavailableReason();
        if (unavailable != null) throw new InvalidOperationException(unavailable);

        if (!usbChargeStateVerified)
        {
            ulong currentRaw;
            int currentIdx = ReadPowerOffUsbChargingIndex(out currentRaw);
            if (currentIdx < 0)
                throw new InvalidOperationException("Acer firmware reported an unrecognized USB charging state (0x" +
                currentRaw.ToString("X") + "), so NitroHub did not change it.");
            usbChargeStateVerified = true;
            if (currentIdx == index) return;
        }

        InvokeApgeFunction("SetFunction", UsbChargeWriteValues[index]);

        int[] delays = new int[] { 25, 60, 100, 140 };
        for (int attempt = 0; attempt < delays.Length; attempt++)
        {
            System.Threading.Thread.Sleep(delays[attempt]);
            ulong confirmedRaw;
            if (ReadPowerOffUsbChargingIndex(out confirmedRaw) == index) return;
        }
        usbChargeStateVerified = false;
        throw new InvalidOperationException("Acer firmware did not confirm the new USB charging setting.");
    }

    private void RefreshPowerOffUsbChargingState()
    {
        if (usbChargeToggle == null || usbChargeSegment == null) return;
        int selected = -1;
        bool controllable = false;
        RunHardwareAsync(delegate
        {
            if (GetPowerOffUsbUnavailableReason() == null)
            {
                ulong raw;
                selected = ReadPowerOffUsbChargingIndex(out raw);
                if (selected >= 0)
                {
                    controllable = true;
                    usbChargeStateVerified = true;
                }
            }
        }, delegate(Exception ex)
        {
            if (ex != null)
            {
                selected = -1;
                controllable = false;
            }
            // Don't overwrite the UI if the user already started changing it while this refresh was in flight.
            if (usbChargeBusy || pendingUsbChargeIndex >= 0) return;
            updatingUsbChargeUi = true;
            try
            {
                bool isOn = controllable && selected > 0;
                if (isOn) lastUsbChargeOptionIndex = selected;
                usbChargeToggle.Enabled = controllable;
                usbChargeToggle.Checked = isOn;
                usbChargeSegment.SelectedIndex = Math.Max(0, lastUsbChargeOptionIndex - 1);
                usbChargeSegment.Enabled = isOn;
            }
            finally
            {
                updatingUsbChargeUi = false;
            }
        });
    }

    private void SetPowerOffUsbChargingFromUi(int index)
    {
        // Keep the latest click instead of dropping it while a previous WMI write is in flight.
        if (usbChargeBusy)
        {
            pendingUsbChargeIndex = index;
            return;
        }
        usbChargeBusy = true;
        pendingUsbChargeIndex = -1;
        RunHardwareAsync(delegate
        {
            WritePowerOffUsbCharging(index);
        }, delegate(Exception ex)
        {
            usbChargeBusy = false;
            int nextPending = pendingUsbChargeIndex;
            pendingUsbChargeIndex = -1;
            if (ex != null)
            {
                try { RefreshPowerOffUsbChargingState(); } catch { }
                MessageBox.Show(Tr("Could not change Power-off USB Charging: ", "无法更改关机 USB 充电设置：") + ex.Message,
                    TrKey("Power-off USB Charging"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (nextPending >= 0 && nextPending != index)
            {
                SetPowerOffUsbChargingFromUi(nextPending);
                return;
            }
            // WritePowerOffUsbCharging already confirmed read-back; sync UI directly without extra WMI queries.
            updatingUsbChargeUi = true;
            try
            {
                bool isOn = index > 0;
                if (isOn) lastUsbChargeOptionIndex = index;
                usbChargeToggle.Checked = isOn;
                usbChargeSegment.SelectedIndex = Math.Max(0, lastUsbChargeOptionIndex - 1);
                usbChargeSegment.Enabled = isOn;
            }
            finally
            {
                updatingUsbChargeUi = false;
            }
        });
    }

    // Bluelight Shield (software color filter via Windows gamma ramp)

    private void BuildBluelightCard(Panel page)
    {
        Panel card = new Panel
        {
            Location = new Point(18 + UsbCardWidth + FeatureCardGap, 96), Size = new Size(BluelightCardWidth, FeatureCardHeight),
            BackColor = Color.FromArgb(34, 41, 49), BorderStyle = BorderStyle.None, Tag = "card"
        };
        page.Controls.Add(card);
        card.Controls.Add(new Label
        {
            Text = "BLUELIGHT SHIELD", Location = new Point(20, 22), Size = new Size(250, 24),
            Font = new Font("Segoe UI Semibold", 10.5F), ForeColor = SharedHeadingColor, Tag = "section-heading"
        });

        bluelightToggle = new ToggleSwitch
        {
            Name = "bluelight-toggle", Location = new Point(BluelightCardWidth - 20 - 68, 19), Size = new Size(68, 30), ShowStateText = true,
            AccessibleName = "Bluelight Shield"
        };
        bluelightToggle.Toggled += delegate
        {
            if (updatingBluelightUi) return;
            int targetLevel = bluelightToggle.Checked ? (lastBluelightOptionIndex + 1) : 0;
            SetBluelightLevelFromUi(targetLevel);
        };
        card.Controls.Add(bluelightToggle);

        card.Controls.Add(new Label
        {
            Text = "Reduces blue light emission to ease eye strain.",
            Location = new Point(20, 54),
            Size = new Size(BluelightCardWidth - 40, 40),
            Font = new Font("Segoe UI", 9F),
            ForeColor = Color.FromArgb(182, 190, 198),
            Tag = "muted"
        });

        card.Controls.Add(new Label
        {
            Text = "Filter level", Location = new Point(20, FeatureOptionLabelY), Size = new Size(BluelightCardWidth - 40, 18),
            Font = new Font("Segoe UI Semibold", 8.5F), ForeColor = SharedHeadingColor, Tag = "section-heading"
        });

        bluelightSegment = new SegmentedControl
        {
            Name = "bluelight-segment", Location = new Point(20, FeatureOptionSegmentY), Size = new Size(4 * FeatureSegmentCellWidth, 38),
            Items = new string[] { "Low", "Medium", "High", "Long Hour" },
            SelectedIndex = 0, Enabled = false
        };
        bluelightSegment.SelectedIndexChanged += delegate
        {
            if (updatingBluelightUi) return;
            lastBluelightOptionIndex = bluelightSegment.SelectedIndex;
            if (bluelightToggle.Checked)
                SetBluelightLevelFromUi(lastBluelightOptionIndex + 1);
        };
        card.Controls.Add(bluelightSegment);

        bluelightKeepCheckBox = new CheckBox
        {
            Name = "bluelight-keep-checkbox",
            Text = "Keep filter after quitting",
            Location = new Point(20, FeatureOptionSegmentY + 50),
            Size = new Size(BluelightCardWidth - 40, 24),
            Font = new Font("Segoe UI Semibold", 8.5F),
            ForeColor = SharedHeadingColor,
            BackColor = Color.Transparent,
            Tag = "section-heading",
            UseVisualStyleBackColor = false,
            AccessibleName = "Keep Bluelight Shield after quitting NitroHub",
            AccessibleDescription = "When checked, the screen filter stays active after NitroHub is closed."
        };
        bluelightKeepCheckBox.CheckedChanged += delegate
        {
            if (settingsPanel == null || settingsPanel.CurrentConfig == null) return;
            settingsPanel.CurrentConfig.KeepBluelightOnExit = bluelightKeepCheckBox.Checked;
            settingsPanel.CurrentConfig.Save();
        };
        card.Controls.Add(bluelightKeepCheckBox);
    }

    private void SyncBluelightKeepToggle()
    {
        if (bluelightKeepCheckBox != null && settingsPanel != null && settingsPanel.CurrentConfig != null)
            bluelightKeepCheckBox.Checked = settingsPanel.CurrentConfig.KeepBluelightOnExit;
    }

    private void InitializeBluelightShield()
    {
        bluelightLevel = BluelightShield.ReadSavedLevel();
        if (bluelightLevel > 0) lastBluelightOptionIndex = bluelightLevel - 1;
        SyncBluelightKeepToggle();

        bluelightReapplyTimer = new Timer { Interval = 2500 };
        bluelightReapplyTimer.Tick += delegate
        {
            bluelightReapplyTimer.Stop();
            if (bluelightLevel <= 0) return;
            try
            {
                bool softened;
                BluelightShield.Apply(bluelightLevel, out softened);
            }
            catch { }
        };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += BluelightDisplaySettingsChanged;
        Microsoft.Win32.SystemEvents.PowerModeChanged += BluelightPowerModeChanged;
        Microsoft.Win32.SystemEvents.SessionSwitch += BluelightSessionSwitch;

        if (bluelightLevel > 0)
        {
            try
            {
                bool softened;
                int displays = BluelightShield.Apply(bluelightLevel, out softened);
                if (displays <= 0) bluelightLevel = 0;
            }
            catch
            {
                bluelightLevel = 0;
            }
        }
        UpdateBluelightControls();
    }

    private void SystemUserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category != Microsoft.Win32.UserPreferenceCategory.General && e.Category != Microsoft.Win32.UserPreferenceCategory.Color) return;
        PostToUi(delegate
        {
            if (settingsPanel != null) settingsPanel.OnSystemThemeChanged();
        });
    }

    // Acer EC firmware frequently resets charge limit and keyboard lighting after S3/Modern Standby sleep.
    // Wait briefly after Resume for ACPI/WMI services to settle, then verify and restore saved settings off the UI thread.
    private void SystemPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode != Microsoft.Win32.PowerModes.Resume) return;
        PostToUi(delegate
        {
            if (resumeReapplyTimer == null)
            {
                resumeReapplyTimer = new Timer { Interval = 3000 };
                resumeReapplyTimer.Tick += delegate
                {
                    if (resumeReapplyTimer != null) resumeReapplyTimer.Stop();
                    if (!uiClosing && !IsDisposed) RecoverFirmwareAfterResume();
                };
            }
            resumeReapplyTimer.Stop();
            resumeReapplyTimer.Start();
        });
    }

    private void RecoverFirmwareAfterResume()
    {
        try { CheckPowerSourceTransition(); } catch { }

        bool? savedLimit = ReadSavedChargeLimitPreference();
        if (savedLimit.HasValue)
        {
            bool target = savedLimit.Value;
            bool confirmed = target;
            RunHardwareAsync(delegate
            {
                bool current = ReadChargeLimitState();
                if (current != target) WriteChargeLimitState(target);
                confirmed = ReadChargeLimitState();
            }, delegate(Exception ex)
            {
                if (ex == null) ApplyChargeLimitStatusUi(confirmed, null, null);
                else RefreshStatus();
            });
        }
        else
        {
            RefreshStatus();
        }

        if (keyboardEffectControlAvailable && effectCombo != null && effectCombo.Enabled && effectCombo.SelectedItem is KeyboardEffectProfile)
        {
            KeyboardEffectProfile selected = (KeyboardEffectProfile)effectCombo.SelectedItem;
            int mode = selected.FirmwareMode;
            int direction = (mode == 3 || mode == 4) ? 1 : 0;
            int speed = speedTrack.Value;
            int brightness = brightnessTrack.Value;
            Color color = lightingColor;
            RunHardwareAsync(delegate
            {
                WriteKeyboardMode(mode, speed, brightness, color, direction);
            }, delegate(Exception ex) { });
        }

        RefreshBatteryCalibrationState();
        CaptureKeyboardTimeoutState();
        RefreshPowerOffUsbChargingState();
        RefreshTelemetry();
    }

    private sealed class HardwareSnapshot
    {
        public string Model;
        public string Serial;
        public string Bios;
        public string Windows;
        public string Processor;
        public string Graphics;
        public string Memory;
        public string Storage;
        public string Network;
        public string AcerModelCode;
        public string AcerSku;
        public List<string> GraphicsList;
        public bool FromCache;

        private static string CachePath()
        {
            return Path.Combine(GetAppDataDirectory(), "hardware-cache.txt");
        }

        private static string Escape(string s)
        {
            if (s == null) return String.Empty;
            return s.Replace("\\", "\\\\").Replace("\r", String.Empty).Replace("\n", "\\n");
        }

        private static string Unescape(string s)
        {
            if (String.IsNullOrEmpty(s)) return String.Empty;
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    char next = s[i + 1];
                    if (next == 'n') { sb.Append(Environment.NewLine); i++; continue; }
                    if (next == '\\') { sb.Append('\\'); i++; continue; }
                }
                sb.Append(s[i]);
            }
            return sb.ToString();
        }

        public static HardwareSnapshot LoadFromDisk()
        {
            HardwareSnapshot snap = new HardwareSnapshot
            {
                Model = "Loading...",
                Serial = "Loading...",
                Bios = "Loading...",
                Windows = "Loading...",
                Processor = "Loading...",
                Graphics = "Loading...",
                Memory = "Loading...",
                Storage = "Loading...",
                Network = "Loading...",
                AcerModelCode = "Loading...",
                AcerSku = String.Empty,
                GraphicsList = new List<string>(),
                FromCache = false
            };
            try
            {
                string path = CachePath();
                if (!File.Exists(path)) return snap;
                Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (string line in File.ReadAllLines(path))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    map[line.Substring(0, eq)] = Unescape(line.Substring(eq + 1));
                }
                string val;
                if (!map.TryGetValue("Model", out val) || String.IsNullOrWhiteSpace(val)) return snap;
                snap.Model = val;
                if (map.TryGetValue("Serial", out val)) snap.Serial = val;
                if (map.TryGetValue("Bios", out val)) snap.Bios = val;
                if (map.TryGetValue("Windows", out val)) snap.Windows = val;
                if (map.TryGetValue("Processor", out val)) snap.Processor = val;
                if (map.TryGetValue("Graphics", out val)) snap.Graphics = val;
                if (map.TryGetValue("Memory", out val)) snap.Memory = val;
                if (map.TryGetValue("Storage", out val)) snap.Storage = val;
                if (map.TryGetValue("Network", out val)) snap.Network = val;
                if (map.TryGetValue("AcerModelCode", out val)) snap.AcerModelCode = val;
                if (map.TryGetValue("AcerSku", out val)) snap.AcerSku = val;
                if (!String.IsNullOrWhiteSpace(snap.Graphics) && !String.Equals(snap.Graphics, "Unavailable", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (string g in snap.Graphics.Split(new string[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string trimmed = g.Trim();
                        if (!String.IsNullOrEmpty(trimmed)) snap.GraphicsList.Add(trimmed);
                    }
                }
                snap.FromCache = true;
            }
            catch { }
            return snap;
        }

        public void SaveToDisk()
        {
            try
            {
                SafeFile.WriteAllLines(CachePath(), new string[]
                {
                    "Model=" + Escape(Model),
                    "Serial=" + Escape(Serial),
                    "Bios=" + Escape(Bios),
                    "Windows=" + Escape(Windows),
                    "Processor=" + Escape(Processor),
                    "Graphics=" + Escape(Graphics),
                    "Memory=" + Escape(Memory),
                    "Storage=" + Escape(Storage),
                    "Network=" + Escape(Network),
                    "AcerModelCode=" + Escape(AcerModelCode),
                    "AcerSku=" + Escape(AcerSku)
                });
            }
            catch { }
        }

        public static HardwareSnapshot ReadLive()
        {
            string graphics = ReadGraphicsNames();
            List<string> graphicsList = new List<string>();
            if (!String.IsNullOrWhiteSpace(graphics) && !String.Equals(graphics, "Unavailable", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string line in graphics.Split(new string[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string trimmed = line.Trim();
                    if (!String.IsNullOrEmpty(trimmed)) graphicsList.Add(trimmed);
                }
            }
            return new HardwareSnapshot
            {
                Model = ReadComputerModel(),
                Serial = ReadComputerSerial(),
                Bios = ReadBiosVersion(),
                Windows = ReadWindowsVersion(),
                Processor = ReadProcessorName(),
                Graphics = graphics,
                Memory = ReadMemoryModules(),
                Storage = ReadStorageDevices(),
                Network = ReadNetworkDevices(),
                AcerModelCode = ReadAcerModelCode(),
                AcerSku = ReadAcerSystemSku(),
                GraphicsList = graphicsList,
                FromCache = false
            };
        }

        public bool SameContent(HardwareSnapshot other)
        {
            if (other == null) return false;
            return String.Equals(Model, other.Model, StringComparison.Ordinal) &&
                String.Equals(Serial, other.Serial, StringComparison.Ordinal) &&
                String.Equals(Bios, other.Bios, StringComparison.Ordinal) &&
                String.Equals(Windows, other.Windows, StringComparison.Ordinal) &&
                String.Equals(Processor, other.Processor, StringComparison.Ordinal) &&
                String.Equals(Graphics, other.Graphics, StringComparison.Ordinal) &&
                String.Equals(Memory, other.Memory, StringComparison.Ordinal) &&
                String.Equals(Storage, other.Storage, StringComparison.Ordinal) &&
                String.Equals(Network, other.Network, StringComparison.Ordinal) &&
                String.Equals(AcerModelCode, other.AcerModelCode, StringComparison.Ordinal) &&
                String.Equals(AcerSku, other.AcerSku, StringComparison.Ordinal);
        }
    }

    private static void ScaleDynamicControlAfterDpi(Control c)
    {
        if (Math.Abs(UiDpi.Scale - 1F) < 0.001F) return;
        if (c.Dock == DockStyle.None)
        {
            if (c.AutoSize) c.Location = new Point(UiDpi.S(c.Left), UiDpi.S(c.Top));
            else c.SetBounds(UiDpi.S(c.Left), UiDpi.S(c.Top), UiDpi.S(c.Width), UiDpi.S(c.Height));
        }
        Padding m = c.Margin;
        c.Margin = new Padding(UiDpi.S(m.Left), UiDpi.S(m.Top), UiDpi.S(m.Right), UiDpi.S(m.Bottom));
        UiDpi.ScaleChildren(c);
    }

    private void StyleDynamicSubtree(Control container)
    {
        MakeTextBackgroundsTransparent(container);
        if (!String.IsNullOrEmpty(currentLanguage))
        {
            ApplyLanguageRecursive(container, currentLanguage);
            UiLang.ApplyFontToTree(container, currentLanguage);
        }
        Color background = isDarkTheme ? Color.FromArgb(21, 26, 32) : Color.FromArgb(244, 247, 251);
        Color card = isDarkTheme ? Color.FromArgb(34, 41, 49) : Color.White;
        Color text = isDarkTheme ? Color.FromArgb(232, 237, 243) : Color.FromArgb(39, 57, 74);
        Color muted = isDarkTheme ? Color.FromArgb(202, 211, 221) : Muted;
        Color border = isDarkTheme ? Color.FromArgb(92, 105, 119) : Color.FromArgb(198, 209, 220);
        ApplyThemeToChildren(container, background, card, text, muted, border);
    }

    private void RefreshHardwareInventoryAsync()
    {
        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            HardwareSnapshot live = HardwareSnapshot.ReadLive();
            live.SaveToDisk();
            PostToUi(delegate { ApplyHardwareSnapshotToUi(live); });
        });
    }

    private void ApplyHardwareSnapshotToUi(HardwareSnapshot snap)
    {
        if (snap == null || IsDisposed) return;
        if (displayedHardwareSnapshot != null && displayedHardwareSnapshot.SameContent(snap))
            return;
        displayedHardwareSnapshot = snap;

        if (overviewIdentityRows != null)
        {
            overviewIdentityRows.SuspendLayout();
            try
            {
                while (overviewIdentityRows.Controls.Count > 0)
                {
                    Control old = overviewIdentityRows.Controls[0];
                    overviewIdentityRows.Controls.RemoveAt(0);
                    old.Dispose();
                }
                modelLabel = AddOverviewInfoRow(overviewIdentityRows, "Model", snap.Model, true);
                AddOverviewInfoRow(overviewIdentityRows, "Serial number", snap.Serial, false, true);
                AddOverviewInfoRow(overviewIdentityRows, "BIOS version", snap.Bios, false);
                AddOverviewInfoRow(overviewIdentityRows, "Windows", snap.Windows, false);
                foreach (Control row in overviewIdentityRows.Controls) ScaleDynamicControlAfterDpi(row);
                StyleDynamicSubtree(overviewIdentityRows);
            }
            finally { overviewIdentityRows.ResumeLayout(true); }
        }

        if (overviewHardwareRows != null)
        {
            overviewHardwareRows.SuspendLayout();
            try
            {
                while (overviewHardwareRows.Controls.Count > 0)
                {
                    Control old = overviewHardwareRows.Controls[0];
                    overviewHardwareRows.Controls.RemoveAt(0);
                    old.Dispose();
                }
                AddOverviewInfoRow(overviewHardwareRows, "Processor", snap.Processor, false);
                AddOverviewInfoRow(overviewHardwareRows, "Graphics", snap.Graphics, false);
                AddOverviewInfoRow(overviewHardwareRows, "Memory modules", snap.Memory, false);
                AddOverviewInfoRow(overviewHardwareRows, "Storage", snap.Storage, false);
                AddOverviewInfoRow(overviewHardwareRows, "Network devices", snap.Network, false);
                foreach (Control row in overviewHardwareRows.Controls) ScaleDynamicControlAfterDpi(row);
                StyleDynamicSubtree(overviewHardwareRows);
            }
            finally { overviewHardwareRows.ResumeLayout(true); }
        }

        supportModelValue = snap.AcerModelCode;
        supportSkuValue = snap.AcerSku;
        supportSerialValue = snap.Serial;
        string modelText = String.IsNullOrWhiteSpace(supportModelValue) ? "Unavailable" : supportModelValue;
        string serialText = String.IsNullOrWhiteSpace(supportSerialValue) ? "Unavailable" : supportSerialValue;
        if (supportModelLabel != null) supportModelLabel.Text = modelText;
        if (supportSerialLabel != null)
        {
            supportSerialLabel.Text = serialText;
            if (supportCopyButton != null)
                supportCopyButton.Visible = serialText != "Unavailable" && serialText != "Loading...";
        }
        ReflowSerialCopyButtons();

        if (supportPagePanel != null)
        {
            Panel graphicsCard = null;
            foreach (Control c in supportPagePanel.Controls)
            {
                if (c is Panel && Convert.ToString(c.Tag) == "graphics-support-panel") { graphicsCard = (Panel)c; break; }
            }
            if (graphicsCard != null)
            {
                graphicsCard.SuspendLayout();
                try
                {
                    for (int i = graphicsCard.Controls.Count - 1; i >= 0; i--)
                    {
                        Control child = graphicsCard.Controls[i];
                        if (Convert.ToString(child.Tag) == "graphics-support-row")
                        {
                            graphicsCard.Controls.RemoveAt(i);
                            child.Dispose();
                        }
                    }
                    List<string> graphicsAdapters = snap.GraphicsList ?? new List<string>();
                    List<string> nvidiaAdapters = FindGraphicsAdapters(graphicsAdapters, "NVIDIA", "GeForce", "RTX", "GTX");
                    List<string> amdAdapters = FindGraphicsAdapters(graphicsAdapters, "AMD", "Radeon");
                    List<string> intelAdapters = FindGraphicsAdapters(graphicsAdapters, "Intel");
                    List<string> otherAdapters = FindOtherGraphicsAdapters(graphicsAdapters);
                    int vendorCount = (nvidiaAdapters.Count > 0 ? 1 : 0) + (amdAdapters.Count > 0 ? 1 : 0) + (intelAdapters.Count > 0 ? 1 : 0);
                    int otherRowCount = vendorCount == 0 || otherAdapters.Count > 0 ? 1 : 0;
                    int totalGraphicsRows = Math.Max(1, vendorCount + otherRowCount);
                    graphicsCard.Height = UiDpi.S(68 + totalGraphicsRows * 74);

                    int graphicsRow = 0;
                    if (nvidiaAdapters.Count > 0)
                        AddGraphicsSupportRow(graphicsCard, "NVIDIA", nvidiaAdapters, graphicsRow++, "NVIDIA App", "https://www.nvidia.com/en-us/software/nvidia-app/");
                    if (amdAdapters.Count > 0)
                        AddGraphicsSupportRow(graphicsCard, "AMD", amdAdapters, graphicsRow++, "AMD Drivers", "https://www.amd.com/en/support/download/drivers.html");
                    if (intelAdapters.Count > 0)
                        AddGraphicsSupportRow(graphicsCard, "Intel", intelAdapters, graphicsRow++, "Intel Driver Assistant", "https://www.intel.com/content/www/us/en/support/detect.html");
                    if (graphicsRow == 0 || otherAdapters.Count > 0)
                        AddGraphicsSupportRow(graphicsCard, "Other graphics", otherAdapters.Count == 0 ? graphicsAdapters : otherAdapters, graphicsRow, String.Empty, String.Empty);

                    foreach (Control child in graphicsCard.Controls)
                    {
                        if (Convert.ToString(child.Tag) == "graphics-support-row")
                            ScaleDynamicControlAfterDpi(child);
                    }
                    StyleDynamicSubtree(graphicsCard);
                }
                finally { graphicsCard.ResumeLayout(true); }
            }
        }
    }

    private void ShutdownBluelightShield()
    {
        try
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= BluelightDisplaySettingsChanged;
            Microsoft.Win32.SystemEvents.PowerModeChanged -= BluelightPowerModeChanged;
            Microsoft.Win32.SystemEvents.SessionSwitch -= BluelightSessionSwitch;
            if (bluelightReapplyTimer != null)
            {
                bluelightReapplyTimer.Stop();
                bluelightReapplyTimer.Dispose();
                bluelightReapplyTimer = null;
            }
        }
        catch { }
    }

    private void BluelightDisplaySettingsChanged(object sender, EventArgs e)
    {
        ScheduleBluelightReapply();
    }

    private void BluelightPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode != Microsoft.Win32.PowerModes.Suspend) ScheduleBluelightReapply();
    }

    private void BluelightSessionSwitch(object sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionUnlock ||
            e.Reason == Microsoft.Win32.SessionSwitchReason.SessionLogon ||
            e.Reason == Microsoft.Win32.SessionSwitchReason.ConsoleConnect)
        ScheduleBluelightReapply();
    }

    // System events arrive on a background thread, so hand the work to the form's thread.
    private void ScheduleBluelightReapply()
    {
        PostToUi(delegate
        {
            if (bluelightLevel <= 0 || bluelightReapplyTimer == null) return;
            bluelightReapplyTimer.Stop();
            bluelightReapplyTimer.Start();
        });
    }

    private void UpdateBluelightControls()
    {
        if (bluelightToggle == null || bluelightSegment == null) return;
        updatingBluelightUi = true;
        try
        {
            bool isOn = bluelightLevel > 0;
            if (isOn) lastBluelightOptionIndex = bluelightLevel - 1;
            bluelightToggle.Checked = isOn;
            bluelightSegment.SelectedIndex = Math.Max(0, lastBluelightOptionIndex);
            bluelightSegment.Enabled = isOn;
        }
        finally
        {
            updatingBluelightUi = false;
        }
    }

    private void SetBluelightLevelFromUi(int level)
    {
        try
        {
            bool softened;
            int displays = BluelightShield.Apply(level, out softened);
            if (displays <= 0)
            {
                UpdateBluelightControls();
                MessageBox.Show(Tr("Windows did not accept the color change on any display.", "Windows 未在任何显示器上接受色彩更改。"),
                    TrKey("Bluelight Shield"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            bluelightLevel = level;
            BluelightShield.SaveLevel(level);
            if (level > 0) EnsureChargeLimitStartupTask();
            UpdateBluelightControls();
        }
        catch (Exception ex)
        {
            UpdateBluelightControls();
            MessageBox.Show(Tr("Could not change Bluelight Shield: ", "无法更改蓝光护盾设置：") + ex.Message,
                TrKey("Bluelight Shield"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void BuildSettingsPage(Panel page)
    {
        settingsPanel = new ModernSettingsPanel(
            this,
            delegate(bool dark)
        {
            isDarkTheme = dark;
            ApplyTheme();
        },
            delegate(string lang)
        {
            ApplyGlobalLanguage(lang);
        },
            EnsureChargeLimitStartupTask,
            delegate
        {
            RemoveScheduledTask(StartupTaskName);
            RemoveScheduledTask("NitroHub Charge Limit");
        },
            IsStartupTaskInstalled
            );
        page.Controls.Add(settingsPanel);
        settingsPanel.SettingsResetAction = SyncBluelightKeepToggle;
        settingsPanel.DiagnosticsItemsProvider = BuildDiagnosticItems;
        isDarkTheme = settingsPanel.IsDark;
        if (settingsPanel.CurrentConfig != null)
        {
            ApplyGlobalLanguage(settingsPanel.CurrentConfig.Language);
        }
    }

    private List<DiagItem> BuildDiagnosticItems()
    {
        List<DiagItem> items = new List<DiagItem>();
        try { items.Add(new DiagItem(Tr("Model", "型号"), ReadComputerModel(), 0)); }
        catch (Exception ex) { items.Add(new DiagItem(Tr("Model", "型号"), ex.Message, 2)); }
        try { items.Add(new DiagItem("BIOS", ReadBiosVersion(), 0)); }
        catch (Exception ex) { items.Add(new DiagItem("BIOS", ex.Message, 2)); }
        bool admin = false;
        try { admin = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator); }
        catch { }
        items.Add(new DiagItem(Tr("Administrator rights", "管理员权限"),
            admin ? Tr("Yes", "是") : Tr("No - some features may fail", "否 - 部分功能可能失败"), admin ? 0 : 2));
        bool startup = false;
        try { startup = IsStartupTaskInstalled(); }
        catch { }
        items.Add(new DiagItem(Tr("Startup task", "开机自启任务"),
            startup ? Tr("Installed", "已安装") : Tr("Not installed", "未安装"), startup ? 0 : 1));
        try { items.Add(new DiagItem(Tr("Battery charge limit", "电池充电上限"), Tr("Working", "正常") + " (" + (ReadChargeLimitState() ? "80%" : "100%") + ")", 0)); }
        catch (Exception ex) { items.Add(new DiagItem(Tr("Battery charge limit", "电池充电上限"), ex.Message, 2)); }
        items.Add(new DiagItem(Tr("Keyboard lighting", "键盘灯光"),
            keyboardEffectControlAvailable ? Tr("Working", "正常") : Tr("Not available", "不可用"), keyboardEffectControlAvailable ? 0 : 1));
        bool usb = usbChargeToggle != null && usbChargeToggle.Enabled;
        items.Add(new DiagItem(Tr("Power-off USB charging", "关机 USB 充电"),
            usb ? Tr("Working", "正常") : Tr("Not available", "不可用"), usb ? 0 : 1));
        return items;
    }

    private void AddGraphicsSupportRow(Panel parent, string vendor, List<string> adapters, int rowIndex, string buttonText, string url)
    {
        Panel row = new RoundedPanel
        {
            Location = new Point(18, 52 + rowIndex * 74), Size = new Size(774, 66),
            BackColor = Color.FromArgb(28, 34, 41), BorderStyle = BorderStyle.None, Tag = "graphics-support-row"
        };
        Color vendorAccent = String.Equals(vendor, "AMD", StringComparison.OrdinalIgnoreCase) ? Color.FromArgb(232, 106, 51) : Teal;
        row.Controls.Add(new Panel
        {
            Location = new Point(14, 14), Size = new Size(3, 38), BackColor = vendorAccent, Tag = "accent-strip"
        });
        row.Controls.Add(new Label
        {
            Text = vendor.ToUpperInvariant(), Location = new Point(30, 8), Size = new Size(450, 17),
            Font = UiLang.GetCachedFont("Segoe UI Semibold", 8.5F, FontStyle.Regular), ForeColor = SharedHeadingColor,
            Tag = "section-heading", TextAlign = ContentAlignment.MiddleLeft
        });
        row.Controls.Add(new Label
        {
            Text = adapters.Count == 0 ? "No graphics adapter detected." : String.Join(Environment.NewLine, adapters.ToArray()),
            Location = new Point(30, 27), Size = new Size(450, 30), AutoEllipsis = true,
            Font = UiLang.GetCachedFont("Segoe UI", 9.5F, FontStyle.Regular), ForeColor = Color.FromArgb(245, 247, 250),
            TextAlign = ContentAlignment.MiddleLeft
        });
        if (!String.IsNullOrWhiteSpace(buttonText))
        {
            Button button = new RoundedButton
            {
                Name = "support-link", Text = buttonText, Location = new Point(578, 14),
                Size = new Size(180, 38), FlatStyle = FlatStyle.Flat,
                BackColor = Teal, ForeColor = Color.White, Font = UiLang.GetCachedFont("Segoe UI Semibold", ActionButtonFontSize, FontStyle.Regular),
                UseVisualStyleBackColor = false, Tag = "accent"
            };
            button.FlatAppearance.BorderSize = 0;
            button.Click += delegate { OpenSupportUrl(url); };
            row.Controls.Add(button);
        }
        parent.Controls.Add(row);
    }

    private void OpenSupportUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(ex.Message, Tr("Could not open support page", "无法打开支持页面"), MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private static string ReadMemoryModules()
    {
        try
        {
            ulong total = 0;
            List<string> modules = new List<string>();
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(new ManagementScope(@"root\CIMV2"), new ObjectQuery("SELECT Capacity, PartNumber, Manufacturer, Speed, SMBIOSMemoryType FROM Win32_PhysicalMemory"), WmiOpts.Create()))
            using (ManagementObjectCollection items = searcher.Get())
            {
                foreach (ManagementObject item in items)
                    using (item)
                {
                    ulong bytes = item["Capacity"] == null ? 0 : Convert.ToUInt64(item["Capacity"]);
                    total += bytes;
                    string capacity = bytes == 0 ? "capacity unavailable" : (bytes / 1073741824.0).ToString("0.#") + " GB";
                    string part = Convert.ToString(item["PartNumber"]).Trim();
                    string manufacturer = Convert.ToString(item["Manufacturer"]).Trim();
                    string module = String.Join(" ", new string[] { manufacturer, part }).Trim();
                    string type = item["SMBIOSMemoryType"] == null ? "type unavailable" : GetMemoryTypeName(Convert.ToUInt16(item["SMBIOSMemoryType"]));
                    string speed = item["Speed"] == null ? "speed unavailable" : Convert.ToString(item["Speed"]) + " MT/s";
                    modules.Add("DIMM " + (modules.Count + 1) + ": " + capacity + " · " + type + " · " + speed +
                        (String.IsNullOrWhiteSpace(module) ? "" : " · " + module));
                }
            }
            if (total == 0) return "Unavailable";
            modules.Insert(0, "Total: " + (total / 1073741824.0).ToString("0.#") + " GB");
            return String.Join(Environment.NewLine, modules.ToArray());
        }
        catch { return "Unavailable"; }
    }

    private static string GetMemoryTypeName(ushort type)
    {
        switch (type)
        {
            case 20: return "DDR";
            case 21: return "DDR2";
            case 24: return "DDR3";
            case 26: return "DDR4";
            case 34: return "DDR5";
            default: return "Unknown";
        }
    }

    private static string ReadStorageDevices()
    {
        try
        {
            List<string> devices = new List<string>();
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(new ManagementScope(@"root\CIMV2"), new ObjectQuery("SELECT Model, Size FROM Win32_DiskDrive"), WmiOpts.Create()))
            using (ManagementObjectCollection items = searcher.Get())
            {
                foreach (ManagementObject item in items)
                    using (item)
                {
                    string model = Convert.ToString(item["Model"]).Trim();
                    string size = "capacity unavailable";
                    if (item["Size"] != null)
                        size = (Convert.ToUInt64(item["Size"]) / 1073741824.0).ToString("0.#") + " GB";
                    if (!String.IsNullOrWhiteSpace(model)) devices.Add(model + " · " + size);
                }
            }
            return devices.Count == 0 ? "Unavailable" : String.Join(Environment.NewLine, devices.ToArray());
        }
        catch { return "Unavailable"; }
    }

    private static string ReadNetworkDevices()
    {
        try
        {
            List<string> devices = new List<string>();
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(new ManagementScope(@"root\CIMV2"), new ObjectQuery("SELECT Name, Manufacturer FROM Win32_NetworkAdapter WHERE PhysicalAdapter=True"), WmiOpts.Create()))
            using (ManagementObjectCollection items = searcher.Get())
            {
                foreach (ManagementObject item in items)
                    using (item)
                {
                    string name = Convert.ToString(item["Name"]).Trim();
                    if (String.IsNullOrWhiteSpace(name)) continue;
                    string manufacturer = Convert.ToString(item["Manufacturer"]).Trim();
                    string description = String.IsNullOrWhiteSpace(manufacturer) ? name : manufacturer + " " + name;
                    devices.Add(description);
                }
            }
            return devices.Count == 0 ? "Unavailable" : String.Join(Environment.NewLine, devices.ToArray());
        }
        catch { return "Unavailable"; }
    }

    private static string ReadHardwareLines(string query, string propertyName)
    {
        try
        {
            List<string> values = new List<string>();
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(new ManagementScope(@"root\CIMV2"), new ObjectQuery(query), WmiOpts.Create()))
            using (ManagementObjectCollection items = searcher.Get())
            {
                foreach (ManagementObject item in items)
                    using (item)
                {
                    string value = Convert.ToString(item[propertyName]).Trim();
                    if (!String.IsNullOrWhiteSpace(value)) values.Add(value);
                }
            }
            return values.Count == 0 ? "Unavailable" : String.Join(Environment.NewLine, values.ToArray());
        }
        catch { return "Unavailable"; }
    }

    private static string ReadWindowsVersion()
    {
        string caption = ReadWmiString("SELECT Caption FROM Win32_OperatingSystem", "Caption");
        string version = ReadWmiString("SELECT Version FROM Win32_OperatingSystem", "Version");
        return caption == "Unavailable" ? caption : (caption + (version == "Unavailable" ? "" : " (" + version + ")"));
    }

    // Features are enabled by what the firmware actually exposes, not by a model name: any Acer laptop that provides
    // the Acer gaming WMI interface (fan / keyboard) or the APGeAction interface (USB charging / keyboard timeout) works.
    // A positive result is cached; a negative one is probed again next time, because the Acer WMI provider can start late after sign-in.
    private static bool acerGamingInterfaceFound;
    private static bool acerApgeInterfaceFound;

    private static bool IsAcerGamingInterfaceAvailable()
    {
        if (acerGamingInterfaceFound) return true;
        try
        {
            using (ManagementObject device = GetGamingFunction())
                acerGamingInterfaceFound = device != null;
        }
        catch { acerGamingInterfaceFound = false; }
        return acerGamingInterfaceFound;
    }

    private static bool IsAcerApgeInterfaceAvailable()
    {
        if (acerApgeInterfaceFound) return true;
        acerApgeInterfaceFound = HasApgeActionMethod("GetFunction") && HasApgeActionMethod("SetFunction");
        return acerApgeInterfaceFound;
    }

    private static string MissingInterfaceMessage(string feature, string interfaceName)
    {
        return feature + " is not available on this computer (" + ReadComputerModel() + "): it does not expose Acer's " + interfaceName + " interface.";
    }

    private static string cachedComputerModel;
    private static readonly object computerModelLock = new object();

    private static string ReadComputerModel()
    {
        // The model cannot change while the app runs, so query WMI once. A failed query is not cached and is retried next time.
        string cached = cachedComputerModel;
        if (cached != null) return cached;
        lock (computerModelLock)
        {
            if (cachedComputerModel != null) return cachedComputerModel;
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(new ManagementScope(@"root\CIMV2"), new ObjectQuery("SELECT Model FROM Win32_ComputerSystem"), WmiOpts.Create()))
                using (ManagementObjectCollection items = searcher.Get())
                {
                    foreach (ManagementObject item in items)
                        using (item)
                    {
                        string model = Convert.ToString(item["Model"]);
                        if (!String.IsNullOrWhiteSpace(model))
                        {
                            cachedComputerModel = model;
                            return model;
                        }
                    }
                }
            }
            catch { }
        }
        return "Unavailable";
    }

    private static string ReadAcerModelCode()
    {
        string model = ReadComputerModel();
        Match match = Regex.Match(model, @"\b[A-Z]{1,8}\d{1,5}-[A-Z0-9]{1,8}\b", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            string productName = ReadWmiString("SELECT Name FROM Win32_ComputerSystemProduct", "Name");
            match = Regex.Match(productName, @"\b[A-Z]{1,8}\d{1,5}-[A-Z0-9]{1,8}\b", RegexOptions.IgnoreCase);
        }
        return match.Success ? match.Value.ToUpperInvariant() : "Unavailable";
    }

    private static string ReadAcerSystemSku()
    {
        string sku = ReadWmiString("SELECT SystemSKUNumber FROM Win32_ComputerSystem", "SystemSKUNumber");
        if (IsUnavailableAcerId(sku))
        {
            try
            {
                object registrySku = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\BIOS", "SystemSKU", null);
                sku = registrySku == null ? "Unavailable" : Convert.ToString(registrySku).Trim();
            }
            catch { sku = "Unavailable"; }
        }
        return IsUnavailableAcerId(sku) ? "Unavailable" : sku;
    }

    private static bool IsUnavailableAcerId(string value)
    {
        return String.IsNullOrWhiteSpace(value) || value == "Unavailable" || value == "0" ||
            value.IndexOf("to be filled", StringComparison.OrdinalIgnoreCase) >= 0 ||
            value.IndexOf("default string", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string GetAcerProductSupportUrl(string model, string sku, string serial)
    {
        const string supportRoot = "https://www.acer.com/my-en/support";
        if (IsUnavailableAcerId(model) || IsUnavailableAcerId(sku))
            return supportRoot + "/drivers-and-manuals";

        string url = supportRoot + "/product-support/" + Uri.EscapeDataString(model);
        url += "/" + Uri.EscapeDataString(sku);
        url += "/downloads";
        if (!IsUnavailableAcerId(serial)) url += "?sn=" + Uri.EscapeDataString(serial.Trim());
        return url;
    }

    private static string PhotoCacheImagePath() { return Path.Combine(GetAppDataDirectory(), "product-photo.png"); }
    private static string PhotoCacheKeyPath() { return Path.Combine(GetAppDataDirectory(), "product-photo.key"); }

    // The product photo is cached on disk, so later launches show it immediately instead of searching Acer's site and downloading again.
    // A photo found for the exact model is kept 30 days; the generic fallback photo only 3 days so a better one is picked up soon.
    private static Bitmap TryLoadCachedLaptopPhoto()
    {
        try
        {
            string imagePath = PhotoCacheImagePath();
            string keyPath = PhotoCacheKeyPath();
            if (!File.Exists(imagePath) || !File.Exists(keyPath)) return null;
            string[] key = File.ReadAllLines(keyPath);
            if (key.Length < 2 || !String.Equals(key[0], ReadComputerModel(), StringComparison.Ordinal)) return null;
            TimeSpan maxAge = key[1] == "fallback" ? TimeSpan.FromDays(3) : TimeSpan.FromDays(30);
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(keyPath) > maxAge) return null;
            byte[] bytes = File.ReadAllBytes(imagePath);
            using (MemoryStream stream = new MemoryStream(bytes))
            using (Image decoded = Image.FromStream(stream))
                return new Bitmap(decoded);
        }
        catch { return null; }
    }

    private void SaveLaptopPhotoCache(bool isFallback)
    {
        try
        {
            Image image = laptopProductPhoto == null ? null : laptopProductPhoto.Image;
            if (image == null) return;
            string imagePath = PhotoCacheImagePath();
            string temp = imagePath + ".tmp";
            image.Save(temp, System.Drawing.Imaging.ImageFormat.Png);
            if (File.Exists(imagePath)) File.Replace(temp, imagePath, null);
            else File.Move(temp, imagePath);
            SafeFile.WriteAllLines(PhotoCacheKeyPath(), new string[] { ReadComputerModel(), isFallback ? "fallback" : "exact" });
        }
        catch { }
    }

    private void BeginLoadLaptopPhoto()
    {
        System.Threading.ThreadPool.QueueUserWorkItem(delegate { try { LoadLaptopPhotoWorker(); } catch { } });
    }

    private void LoadLaptopPhotoWorker()
    {
        {
            Bitmap cached = TryLoadCachedLaptopPhoto();
            if (cached != null)
            {
                try
                {
                    if (IsDisposed || !IsHandleCreated) { cached.Dispose(); return; }
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (laptopProductPhoto == null || laptopProductPhoto.IsDisposed) { cached.Dispose(); return; }
                        Image previous = laptopProductPhoto.Image;
                        laptopProductPhoto.Image = cached;
                        if (previous != null) previous.Dispose();
                        laptopPhotoStatus.Visible = false;
                    });
                }
                catch { cached.Dispose(); }
                return;
            }
            string photoUrl = TryFindAcerProductPhotoUrl(
                ReadAcerModelCode(), ReadAcerSystemSku(), ReadComputerSerial());
            bool isFallback = String.IsNullOrEmpty(photoUrl);
            if (isFallback) photoUrl = GetFallbackLaptopPhotoUrl();
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke((MethodInvoker)delegate { StartLaptopPhotoDownload(photoUrl, isFallback); });
            }
            catch { }
        }
    }

    private void StartLaptopPhotoDownload(string photoUrl, bool isFallback)
    {
        if (laptopProductPhoto == null || laptopProductPhoto.IsDisposed || String.IsNullOrEmpty(photoUrl)) return;
        laptopPhotoUsingFallback = isFallback;
        laptopPhotoStatus.Text = "Loading your Acer product photo...";
        laptopPhotoStatus.Visible = true;
        try { laptopProductPhoto.LoadAsync(photoUrl); }
        catch
        {
            if (!isFallback) StartLaptopPhotoDownload(GetFallbackLaptopPhotoUrl(), true);
            else laptopPhotoStatus.Text = "Acer product photo could not be loaded.";
        }
    }

    private static string TryFindAcerProductPhotoUrl(string model, string sku, string serial)
    {
        try
        {
            string supportUrl = GetAcerProductSupportUrl(model, sku, serial);
            if (supportUrl.IndexOf("/downloads", StringComparison.OrdinalIgnoreCase) >= 0)
                supportUrl = supportUrl.Replace("/downloads", "/details");
            else if (supportUrl.IndexOf("/drivers-and-manuals", StringComparison.OrdinalIgnoreCase) >= 0)
                return null;

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(supportUrl);
            request.Method = "GET";
            request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) NitroHub/1.0";
            request.Timeout = 8000;
            request.ReadWriteTimeout = 8000;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (StreamReader reader = new StreamReader(response.GetResponseStream()))
            {
                string html = reader.ReadToEnd();
                Uri pageUri = new Uri(supportUrl);
                string bestPhoto = null;
                int bestScore = -1;
                MatchCollection tags = Regex.Matches(html, "<(?:meta|img|source|link)\\b[^>]*>", RegexOptions.IgnoreCase);
                foreach (Match item in tags)
                {
                    string tag = item.Value;
                    bool isMeta = tag.StartsWith("<meta", StringComparison.OrdinalIgnoreCase);
                    if (isMeta && !Regex.IsMatch(tag, "(?:property|name)\\s*=\\s*['\\\"](?:og:image|twitter:image)['\\\"]", RegexOptions.IgnoreCase)) continue;
                    if (tag.StartsWith("<link", StringComparison.OrdinalIgnoreCase) &&
                        !Regex.IsMatch(tag, "rel\\s*=\\s*['\\\"](?:image_src|preload)['\\\"]", RegexOptions.IgnoreCase)) continue;

                    string[] attributes = isMeta ? new string[] { "content" } :
                        tag.StartsWith("<link", StringComparison.OrdinalIgnoreCase)
                        ? new string[] { "href" }
                        : new string[] { "src", "data-src", "data-lazy-src", "srcset", "data-srcset" };
                    foreach (string attribute in attributes)
                    {
                        Match valueMatch = Regex.Match(tag, "(?:^|\\s)" + attribute + "\\s*=\\s*(['\\\"])(.*?)\\1", RegexOptions.IgnoreCase);
                        if (!valueMatch.Success) continue;
                        string candidate = System.Net.WebUtility.HtmlDecode(valueMatch.Groups[2].Value).Trim();
                        if (attribute.IndexOf("srcset", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            string[] variants = candidate.Split(',');
                            for (int variantIndex = 0; variantIndex < variants.Length; variantIndex++)
                            {
                                string[] parts = variants[variantIndex].Trim().Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                                if (parts.Length > 0) AddAcerPhotoCandidate(parts[0], pageUri, model, sku, ref bestPhoto, ref bestScore);
                            }
                        }
                        else
                        {
                            AddAcerPhotoCandidate(candidate, pageUri, model, sku, ref bestPhoto, ref bestScore);
                        }
                    }
                }
                return bestPhoto;
            }
        }
        catch { }
        return null;
    }

    private static void AddAcerPhotoCandidate(string candidate, Uri pageUri, string model, string sku,
        ref string bestPhoto, ref int bestScore)
    {
        if (String.IsNullOrWhiteSpace(candidate)) return;
        candidate = candidate.Replace("\\/", "/");
        Uri imageUri;
        if (!Uri.TryCreate(pageUri, candidate, out imageUri) || imageUri.Scheme != Uri.UriSchemeHttps ||
            !imageUri.Host.EndsWith("acer.com", StringComparison.OrdinalIgnoreCase)) return;

        string path = Uri.UnescapeDataString(imageUri.AbsolutePath);
        if (Regex.IsMatch(path, "logo|favicon|social|banner|icon|windows-11|sn-snid", RegexOptions.IgnoreCase)) return;
        int score = 0;
        string compactSku = (sku ?? String.Empty).Replace(".", String.Empty).Replace("-", String.Empty).ToLowerInvariant();
        string compactPath = path.Replace(".", String.Empty).Replace("-", String.Empty).ToLowerInvariant();
        if (!String.IsNullOrEmpty(compactSku) && compactPath.IndexOf(compactSku, StringComparison.Ordinal) >= 0) score += 100;
        if (!String.IsNullOrEmpty(model) && path.IndexOf(model, StringComparison.OrdinalIgnoreCase) >= 0) score += 80;
        if (Regex.IsMatch(path, "\\.(?:png|jpe?g|webp)$", RegexOptions.IgnoreCase)) score += 20;
        if (path.IndexOf("product", StringComparison.OrdinalIgnoreCase) >= 0) score += 10;
        if (score > bestScore)
        {
            bestScore = score;
            bestPhoto = imageUri.AbsoluteUri;
        }
    }

    private static string GetFallbackLaptopPhotoUrl()
    {
        string productName = ReadComputerModel();
        if (IsUnavailableAcerId(productName)) productName = "Nitro " + ReadAcerModelCode();
        return "https://global-download.acer.com/Image/ModelPictures/Notebook/" +
            Uri.EscapeDataString(productName.Trim()) + ".png";
    }

    internal sealed class AppSettingsConfig
    {
        public string Language = "English (US)";
        public string ThemeMode = "System";
        public bool StartOnStartup = false;
        public string CloseBehavior = "Quit application";
        public uint HotkeyModifiers = 0x0002 | 0x0001; // Ctrl + Alt
        public Keys HotkeyKey = Keys.N;
        public bool KeepBluelightOnExit = false; // keep the Bluelight Shield filter after NitroHub quits

        public string GetModifierLabel()
        {
            bool ctrl = (HotkeyModifiers & 0x0002) != 0;
            bool alt = (HotkeyModifiers & 0x0001) != 0;
            bool shift = (HotkeyModifiers & 0x0004) != 0;
            if (ctrl && shift) return "Ctrl + Shift";
            if (ctrl && alt) return "Ctrl + Alt";
            if (alt && shift) return "Alt + Shift";
            if (ctrl) return "Ctrl";
            if (alt) return "Alt";
            if (shift) return "Shift";
            return "Ctrl + Alt";
        }

        public static uint ParseModifierLabel(string label)
        {
            if (label == "Ctrl + Shift") return 0x0002 | 0x0004;
            if (label == "Ctrl + Alt") return 0x0002 | 0x0001;
            if (label == "Alt + Shift") return 0x0001 | 0x0004;
            if (label == "Ctrl") return 0x0002;
            if (label == "Alt") return 0x0001;
            if (label == "Shift") return 0x0004;
            return 0x0002 | 0x0001;
        }

        public string FormatShortcut()
        {
            if (HotkeyKey == Keys.None) return "Not set";
            string keyText = HotkeyKey == Keys.Space ? "Space" : HotkeyKey.ToString();
            return GetModifierLabel() + " + " + keyText;
        }

        private static string ConfigPath()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NitroHub");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.ini");
        }

        public static AppSettingsConfig Load()
        {
            AppSettingsConfig cfg = new AppSettingsConfig();
            try
            {
                string path = ConfigPath();
                if (!File.Exists(path)) return cfg;
                foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                {
                    int idx = line.IndexOf('=');
                    if (idx <= 0) continue;
                    string k = line.Substring(0, idx).Trim();
                    string v = line.Substring(idx + 1).Trim();
                    if (k == "Language") cfg.Language = v;
                    else if (k == "ThemeMode") cfg.ThemeMode = v;
                    else if (k == "StartOnStartup") bool.TryParse(v, out cfg.StartOnStartup);
                    else if (k == "CloseBehavior") cfg.CloseBehavior = v;
                    else if (k == "KeepBluelightOnExit") bool.TryParse(v, out cfg.KeepBluelightOnExit);
                    else if (k == "HotkeyModifiers") uint.TryParse(v, out cfg.HotkeyModifiers);
                    else if (k == "HotkeyKey")
                    {
                        int keyCode;
                        if (Int32.TryParse(v, out keyCode)) cfg.HotkeyKey = (Keys)keyCode;
                    }
                }
            }
            catch { }
            return cfg;
        }

        public void Save()
        {
            try
            {
                SafeFile.WriteAllLines(ConfigPath(), new string[]
                {
                    "Language=" + Language,
                    "ThemeMode=" + ThemeMode,
                    "StartOnStartup=" + StartOnStartup,
                    "CloseBehavior=" + CloseBehavior,
                    "KeepBluelightOnExit=" + KeepBluelightOnExit,
                    "HotkeyModifiers=" + HotkeyModifiers,
                    "HotkeyKey=" + (int)HotkeyKey
                }, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Program.LogCrash("SettingsSave", ex);
                try { if (SaveFailed != null) SaveFailed(); } catch { }
            }
        }

        public static event Action SaveFailed;
    }

    internal sealed class DiagItem
    {
        public string Name;
        public string Value;
        public int Level; // 0 = ok, 1 = info, 2 = problem

        public DiagItem(string name, string value, int level)
        {
            Name = name;
            Value = value;
            Level = level;
        }
    }

    internal sealed class ModernSettingsPanel : Panel
    {
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public const int WM_HOTKEY = 0x0312;
        internal const int HOTKEY_ID = 0x9001;
        private enum SettingIconType
        {
            None,
            Language,
            Theme,
            Startup,
            CloseWindow,
            Shortcut,
            Update,
            Troubleshooting,
            About
        }

        private Color IconAccentColor
        {
            get { return currentDark ? Color.FromArgb(248, 113, 113) : Color.FromArgb(210, 48, 66); }
        }

        private readonly Form hostForm;
        private readonly Action<bool> onThemeApplied;
        private readonly Action<string> onLanguageApplied;
        private readonly Func<string> ensureStartupTaskFunc;
        private readonly Action removeStartupTaskAction;
        private readonly Func<bool> isStartupInstalledFunc;

        private AppSettingsConfig config;
        private Panel scrollContainer;
        private Panel centerColumn;
        private ModernScrollBar vScrollBar;
        private Panel toastPanel;
        private Label toastLabel;
        private Timer toastTimer;

        private Label grpGeneralLbl, grpBehaviorLbl, grpUpdatesLbl, grpSupportLbl;
        private Label langTitleLbl, langSubLbl;
        private Label themeTitleLbl, themeSubLbl;
        private Label startupTitleLbl, startupSubLbl;
        private Label closeTitleLbl, closeSubLbl;
        private Label shortcutTitleLbl, shortcutSubLbl;
        public Func<List<DiagItem>> DiagnosticsItemsProvider;
        private Label updateTitleLbl, updateSubtitleLabel;
        private Label troubleTitleLbl, troubleSubLbl, aboutTitleLbl, aboutSubLbl;

        private ComboBox languageCombo;
        private SegmentedControl themeSegment;
        private ToggleSwitch startupSwitch;
        private ComboBox closeBehaviorCombo;
        private ComboBox modifierCombo;
        private ComboBox keyCombo;
        private RoundedButton shortcutResetBtn;
        private const string RepoUrl = "https://github.com/NullIsland123/NitroHub";
        private const string IssuesUrl = "https://github.com/NullIsland123/NitroHub/issues";
        private const string ReleasesUrl = "https://github.com/NullIsland123/NitroHub/releases";
        private const string LatestReleaseApiUrl = "https://api.github.com/repos/NullIsland123/NitroHub/releases/latest";

        private RoundedButton checkUpdateBtn;
        private bool updateCheckedUpToDate;
        private string latestAvailableTag;

        private bool currentDark = true;
        private bool startupSwitchTouchedByUser;
        private Action clearActiveNavHover;
        private readonly List<Action> resetNavHoverActions = new List<Action>();
        private Color BgColor, CardColor, CardBorderColor, InputBorderColor, DividerColor, TitleColor, SubtitleColor, ControlBgColor;

        public AppSettingsConfig CurrentConfig { get { return config; } }
        public Action SettingsResetAction; // lets the host refresh its own controls after "Reset All Settings"
        public bool IsDark { get { return currentDark; } }

        public ModernSettingsPanel(
            Form host,
            Action<bool> applyHostTheme,
            Action<string> applyHostLanguage,
            Func<string> ensureStartupTask,
            Action removeStartupTask,
            Func<bool> isStartupInstalled)
        {
            hostForm = host;
            onThemeApplied = applyHostTheme;
            onLanguageApplied = applyHostLanguage;
            ensureStartupTaskFunc = ensureStartupTask;
            removeStartupTaskAction = removeStartupTask;
            isStartupInstalledFunc = isStartupInstalled;

            Dock = DockStyle.Fill;
            DoubleBuffered = true;
            config = AppSettingsConfig.Load();

            InitializeToast();
            Action saveFailedHandler = delegate
            {
                try
                {
                    if (IsHandleCreated)
                        BeginInvoke(new MethodInvoker(delegate { ShowToast(T("Could not save settings", "无法保存设置")); }));
                }
                catch { }
            };
            AppSettingsConfig.SaveFailed += saveFailedHandler;
            Disposed += delegate { AppSettingsConfig.SaveFailed -= saveFailedHandler; };
            BuildLayout();
            ApplyLocalization();
            ApplyVisualTheme(false);

            // Check the Windows scheduled task off the UI thread once the host handle exists so schtasks.exe never delays startup.
            if (isStartupInstalledFunc != null && hostForm != null)
            {
                bool startupCheckStarted = false;
                Action startStartupCheck = delegate
                {
                    if (startupCheckStarted) return;
                    startupCheckStarted = true;
                    System.Threading.ThreadPool.QueueUserWorkItem(delegate
                    {
                        bool installed = false;
                        try { installed = isStartupInstalledFunc(); } catch { return; }
                        try
                        {
                            if (!hostForm.IsDisposed && hostForm.IsHandleCreated)
                            {
                                hostForm.BeginInvoke(new MethodInvoker(delegate
                                {
                                    if (IsDisposed || startupSwitchTouchedByUser || startupSwitch == null) return;
                                    config.StartOnStartup = installed;
                                    startupSwitch.Checked = installed;
                                }));
                            }
                        }
                        catch { }
                    });
                };
                hostForm.HandleCreated += delegate { startStartupCheck(); };
                if (hostForm.IsHandleCreated) startStartupCheck();
            }

            if (hostForm != null)
            {
                hostForm.HandleCreated += delegate { RegisterGlobalHotkey(true); };
                if (hostForm.IsHandleCreated) RegisterGlobalHotkey(true);
            }
        }

        private string T(string en, string zh)
        {
            return UiLang.Pick(config.Language, en, zh);
        }

        private void InitializeToast()
        {
            toastPanel = new RoundedPanel
            {
                Size = new Size(190, 34),
                Visible = false,
                BackColor = Color.FromArgb(40, 48, 58)
            };
            toastLabel = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI Semibold", 8.5F),
                ForeColor = Color.FromArgb(111, 207, 151),
                Text = "Settings saved"
            };
            toastPanel.Controls.Add(toastLabel);

            toastTimer = new Timer { Interval = 2000 };
            toastTimer.Tick += delegate
            {
                toastTimer.Stop();
                toastPanel.Visible = false;
            };
        }

        public void ShowToast(string message)
        {
            if (message != null) return; // notifications disabled
            toastLabel.Text = message;
            int textWidth = TextRenderer.MeasureText(message, toastLabel.Font).Width + 32;
            toastPanel.Size = new Size(Math.Max(150, textWidth), 34);
            toastPanel.Location = new Point(Width - toastPanel.Width - 24, 14);
            toastPanel.Visible = true;
            toastPanel.BringToFront();
            toastTimer.Stop();
            toastTimer.Start();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateCustomScrollBounds();
            if (toastPanel != null && toastPanel.Visible)
                toastPanel.Location = new Point(Width - toastPanel.Width - 24, 14);
        }

        private void UpdateCustomScrollBounds()
        {
            if (centerColumn == null || scrollContainer == null) return;
            int viewW = scrollContainer.ClientSize.Width;
            int viewH = scrollContainer.ClientSize.Height;
            int colWidth = Math.Min(UiDpi.S(700), Math.Max(UiDpi.S(540), viewW - UiDpi.S(48)));
            centerColumn.Width = colWidth;
            centerColumn.Left = Math.Max(UiDpi.S(16), (viewW - colWidth) / 2);

            if (vScrollBar != null)
            {
                vScrollBar.Location = new Point(viewW - vScrollBar.Width - UiDpi.S(4), UiDpi.S(6));
                vScrollBar.Height = Math.Max(UiDpi.S(20), viewH - UiDpi.S(12));
                vScrollBar.Viewport = viewH;
                vScrollBar.Maximum = Math.Max(0, centerColumn.Height + UiDpi.S(24) - viewH);
                centerColumn.Top = UiDpi.S(16) - vScrollBar.Value;
            }
        }

        public void RelayoutAfterDpi()
        {
            UpdateCustomScrollBounds();
        }

        private void AttachWheelRecursive(Control parent)
        {
            parent.MouseWheel += delegate(object sender, MouseEventArgs e)
            {
                if (vScrollBar != null && vScrollBar.Maximum > 0)
                {
                    // Do not hijack mouse wheel while a ComboBox dropdown is open.
                    ComboBox cb = sender as ComboBox;
                    if (cb != null && cb.DroppedDown) return;
                    vScrollBar.Value -= (e.Delta / 120) * UiDpi.S(48);
                }
            };
            foreach (Control child in parent.Controls)
                AttachWheelRecursive(child);
        }

        private void BuildLayout()
        {
            scrollContainer = new BufferedPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = false
            };
            Controls.Add(scrollContainer);

            vScrollBar = new ModernScrollBar();
            vScrollBar.ValueChanged += delegate
            {
                if (centerColumn != null)
                    centerColumn.Top = UiDpi.S(16) - vScrollBar.Value;
            };
            scrollContainer.Controls.Add(vScrollBar);

            centerColumn = new BufferedPanel
            {
                Location = new Point(80, 16),
                Size = new Size(700, 640)
            };
            scrollContainer.Controls.Add(centerColumn);
            vScrollBar.BringToFront();

            int y = 8;

            grpGeneralLbl = AddGroupHeader(centerColumn, "GENERAL", y);
            Panel generalCard = CreateGroupCard(centerColumn, y + 24, 136);
            y += 24 + 136 + 22;

            languageCombo = new ThemedComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Size = new Size(184, 30),
                MaxDropDownItems = 12,
                Font = new Font("Microsoft YaHei UI", 9F)
            };
            languageCombo.Items.AddRange(UiLang.Languages);
            languageCombo.SelectedItem = config.Language;
            if (languageCombo.SelectedIndex < 0) languageCombo.SelectedIndex = 0;
            languageCombo.SelectedIndexChanged += delegate
            {
                config.Language = Convert.ToString(languageCombo.SelectedItem);
                config.Save();
                ApplyLocalization();
                if (onLanguageApplied != null) onLanguageApplied(config.Language);
                ShowToast(T("Settings saved", "设置已保存"));
            };
            AddSettingRow(generalCard, 0, languageCombo, true, SettingIconType.Language, out langTitleLbl, out langSubLbl);

            themeSegment = new SegmentedControl
            {
                Size = new Size(216, 32),
                Items = new string[] { "Light", "Dark", "System" },
                Font = new Font("Segoe UI Semibold", 8.5F)
            };
            themeSegment.SelectedIndex = config.ThemeMode == "Light" ? 0 : (config.ThemeMode == "System" ? 2 : 1);
            themeSegment.SelectedIndexChanged += delegate
            {
                config.ThemeMode = themeSegment.Items[themeSegment.SelectedIndex];
                config.Save();
                ApplyVisualTheme(true);
                ShowToast(T("Settings saved", "设置已保存"));
            };
            AddSettingRow(generalCard, 68, themeSegment, false, SettingIconType.Theme, out themeTitleLbl, out themeSubLbl);

            grpBehaviorLbl = AddGroupHeader(centerColumn, "BEHAVIOR & SHORTCUTS", y);
            Panel behaviorCard = CreateGroupCard(centerColumn, y + 24, 204);
            y += 24 + 204 + 22;

            startupSwitch = new ToggleSwitch
            {
                Size = new Size(68, 28),
                ShowStateText = true,
                Checked = config.StartOnStartup
            };
            startupSwitch.Toggled += delegate
            {
                startupSwitchTouchedByUser = true;
                bool target = startupSwitch.Checked;
                if (target)
                {
                    string err = ensureStartupTaskFunc != null ? ensureStartupTaskFunc() : null;
                    if (!String.IsNullOrEmpty(err))
                    {
                        startupSwitch.Checked = false;
                        MessageBox.Show(T("Run NitroHub as Administrator once to register the startup task.\n\n",
                            "请以管理员身份运行一次 NitroHub 以注册开机自启任务。\n\n") + err,
                            T("Startup", "开机自启"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                }
                else
                {
                    if (removeStartupTaskAction != null) removeStartupTaskAction();
                }
                config.StartOnStartup = target;
                config.Save();
                ShowToast(T("Settings saved", "设置已保存"));
            };
            AddSettingRow(behaviorCard, 0, startupSwitch, true, SettingIconType.Startup, out startupTitleLbl, out startupSubLbl);

            closeBehaviorCombo = new ThemedComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Size = new Size(224, 30),
                Font = new Font("Segoe UI", 9F)
            };
            AddSettingRow(behaviorCard, 68, closeBehaviorCombo, true, SettingIconType.CloseWindow, out closeTitleLbl, out closeSubLbl);
            PopulateCloseBehaviorOptions();
            closeBehaviorCombo.SelectedIndexChanged += delegate
            {
                if (closeBehaviorCombo.SelectedIndex == 1) config.CloseBehavior = "Quit application";
                else config.CloseBehavior = "Minimize to system tray";
                config.Save();
                ShowToast(T("Settings saved", "设置已保存"));
            };

            Panel shortcutBox = new BufferedPanel { Size = new Size(292, 32), Tag = "settings-row-box" };
            modifierCombo = new ThemedComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(0, 2),
                Size = new Size(122, 28),
                Font = new Font("Segoe UI Semibold", 8.5F)
            };
            modifierCombo.Items.AddRange(new object[]
            {
                "Ctrl + Shift",
                "Ctrl + Alt",
                "Alt + Shift",
                "Ctrl",
                "Alt",
                "Shift"
            });
            modifierCombo.SelectedItem = config.GetModifierLabel();
            if (modifierCombo.SelectedIndex < 0) modifierCombo.SelectedIndex = 0;

            keyCombo = new ThemedComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(128, 2),
                Size = new Size(92, 28),
                Font = new Font("Consolas", 9F, FontStyle.Bold)
            };
            for (char c = 'A'; c <= 'Z'; c++) keyCombo.Items.Add(c.ToString());
            for (int f = 1; f <= 12; f++) keyCombo.Items.Add("F" + f);
            for (int d = 0; d <= 9; d++) keyCombo.Items.Add("D" + d);
            keyCombo.Items.Add("Space");

            string currentKeyStr = config.HotkeyKey == Keys.Space ? "Space" : config.HotkeyKey.ToString();
            keyCombo.SelectedItem = currentKeyStr;
            if (keyCombo.SelectedIndex < 0) keyCombo.SelectedIndex = keyCombo.Items.IndexOf("N");

            EventHandler combinationChanged = delegate
            {
                config.HotkeyModifiers = AppSettingsConfig.ParseModifierLabel(Convert.ToString(modifierCombo.SelectedItem));
                string kText = Convert.ToString(keyCombo.SelectedItem);
                if (kText == "Space") config.HotkeyKey = Keys.Space;
                else
                {
                    Keys parsed;
                    if (Enum.TryParse(kText, out parsed)) config.HotkeyKey = parsed;
                }
                config.Save();
                RegisterGlobalHotkey(true);
                ShowToast(T("Shortcut updated: ", "快捷键已更新: ") + config.FormatShortcut());
            };
            modifierCombo.SelectedIndexChanged += combinationChanged;
            keyCombo.SelectedIndexChanged += combinationChanged;

            shortcutResetBtn = new RoundedButton
            {
                Name = "settings-action",
                Location = new Point(226, 1),
                Size = new Size(64, 30),
                Text = "Reset",
                Font = new Font("Segoe UI Semibold", 8.5F),
                FlatStyle = FlatStyle.Flat
            };
            shortcutResetBtn.FlatAppearance.BorderSize = 1;
            shortcutResetBtn.Click += delegate
            {
                config.HotkeyModifiers = 0x0002 | 0x0001;
                config.HotkeyKey = Keys.N;
                config.Save();
                modifierCombo.SelectedItem = "Ctrl + Alt";
                keyCombo.SelectedItem = "N";
                RegisterGlobalHotkey();
                ShowToast(T("Shortcut reset", "快捷键已重置"));
            };
            shortcutBox.Controls.Add(modifierCombo);
            shortcutBox.Controls.Add(keyCombo);
            shortcutBox.Controls.Add(shortcutResetBtn);
            AddSettingRow(behaviorCard, 136, shortcutBox, true, SettingIconType.Shortcut, out shortcutTitleLbl, out shortcutSubLbl);

            grpUpdatesLbl = AddGroupHeader(centerColumn, "UPDATES", y);
            Panel updatesCard = CreateGroupCard(centerColumn, y + 24, 68);
            y += 24 + 68 + 22;

            checkUpdateBtn = new RoundedButton
            {
                Name = "settings-action",
                Size = new Size(156, 32),
                Text = "Check for Updates",
                Font = new Font("Segoe UI Semibold", 8.5F),
                FlatStyle = FlatStyle.Flat
            };
            checkUpdateBtn.FlatAppearance.BorderSize = 1;
            checkUpdateBtn.Click += delegate { StartUpdateCheck(); };
            AddSettingRow(updatesCard, 0, checkUpdateBtn, false, SettingIconType.Update, out updateTitleLbl, out updateSubtitleLabel);

            grpSupportLbl = AddGroupHeader(centerColumn, "SUPPORT & ABOUT", y);
            Panel supportCard = CreateGroupCard(centerColumn, y + 24, 136);
            y += 24 + 136 + 24;

            AddNavigationRow(supportCard, 0, true, SettingIconType.Troubleshooting, ShowTroubleshootingDialog, out troubleTitleLbl, out troubleSubLbl);
            AddNavigationRow(supportCard, 68, false, SettingIconType.About, ShowAboutDialog, out aboutTitleLbl, out aboutSubLbl);

            centerColumn.Height = y;
            AttachWheelRecursive(scrollContainer);
            UpdateCustomScrollBounds();
        }

        private void PopulateCloseBehaviorOptions()
        {
            if (closeBehaviorCombo == null) return;
            int prevIdx = 0;
            if (String.Equals(config.CloseBehavior, "Quit application", StringComparison.OrdinalIgnoreCase)) prevIdx = 1;

            closeBehaviorCombo.Items.Clear();
            closeBehaviorCombo.Items.AddRange(new object[]
            {
                T("Minimize to system tray", "最小化到系统托盘"),
                T("Quit application", "退出应用")
            });
            closeBehaviorCombo.SelectedIndex = prevIdx;
        }

        private void ApplyLocalization()
        {
            UiLang.ApplyFontToTree(this, config.Language);
            if (grpGeneralLbl != null) grpGeneralLbl.Text = T("GENERAL", "常规");
            if (grpBehaviorLbl != null) grpBehaviorLbl.Text = T("BEHAVIOR & SHORTCUTS", "行为与快捷键");
            if (grpUpdatesLbl != null) grpUpdatesLbl.Text = T("UPDATES", "软件更新");
            if (grpSupportLbl != null) grpSupportLbl.Text = T("SUPPORT & ABOUT", "支持与关于");

            if (langTitleLbl != null) langTitleLbl.Text = T("Language", "界面语言");
            if (langSubLbl != null) langSubLbl.Text = T("Choose the interface display language", "选择应用的界面显示语言");

            if (themeTitleLbl != null) themeTitleLbl.Text = T("Theme", "主题外观");
            if (themeSubLbl != null) themeSubLbl.Text = T("Customize how the app looks on your device", "自定义应用的浅色或深色外观");

            if (startupTitleLbl != null) startupTitleLbl.Text = T("Start on startup", "开机自动启动");
            if (startupSubLbl != null) startupSubLbl.Text = T("Automatically launch the app when you log in", "登录 Windows 时自动启动 NitroHub");

            if (closeTitleLbl != null) closeTitleLbl.Text = T("When closing the window", "关闭窗口时");
            if (closeSubLbl != null) closeSubLbl.Text = T("Choose whether closing the window minimizes to tray or quits", "选择点击关闭按钮时最小化到托盘还是退出程序");
            PopulateCloseBehaviorOptions();

            if (shortcutTitleLbl != null) shortcutTitleLbl.Text = T("Open with shortcut", "唤起快捷键");
            if (shortcutSubLbl != null) shortcutSubLbl.Text = T("Select a modifier and key combination to open the app", "选择修饰键与按键组合以随时唤起窗口");
            if (shortcutResetBtn != null) shortcutResetBtn.Text = T("Reset", "重置");

            if (updateTitleLbl != null) updateTitleLbl.Text = T("Software Updates", "软件更新");
            UpdateVersionSubtitle();
            if (checkUpdateBtn != null && checkUpdateBtn.Enabled) checkUpdateBtn.Text = T("Check for Updates", "检查更新");

            if (troubleTitleLbl != null) troubleTitleLbl.Text = T("Troubleshooting", "故障排查");
            if (troubleSubLbl != null) troubleSubLbl.Text = T("Run diagnostics, repair startup, or reset settings to default", "运行诊断、修复开机自启或恢复默认设置");
            if (aboutTitleLbl != null) aboutTitleLbl.Text = T("About", "关于");
            if (aboutSubLbl != null) aboutSubLbl.Text = T("Version info, licenses, privacy policy, and feedback", "版本信息、许可声明、隐私策略与反馈");
        }

        private Label AddGroupHeader(Panel parent, string title, int y)
        {
            Label lbl = new Label
            {
                Text = title,
                Location = new Point(4, y),
                Size = new Size(690, 20),
                Font = new Font("Segoe UI Semibold", 8.5F),
                UseMnemonic = false,
                Tag = "group-title"
            };
            parent.Controls.Add(lbl);
            return lbl;
        }

        private Panel CreateGroupCard(Panel parent, int y, int height)
        {
            RoundedPanel card = new RoundedPanel
            {
                Location = new Point(0, y),
                Size = new Size(700, height),
                Tag = "settings-card"
            };
            parent.Controls.Add(card);
            return card;
        }

        private void AddSettingRow(Panel card, int y, Control rightControl, bool hasDivider, SettingIconType iconType, out Label titleLbl, out Label subLbl)
        {
            Panel row = new BufferedPanel
            {
                Location = new Point(1, y + (y == 0 ? 1 : 0)),
                Size = new Size(698, y == 0 ? 67 : 68),
                BackColor = Color.Transparent,
                Tag = "settings-row"
            };
            row.Paint += delegate(object sender, PaintEventArgs e)
            {
                DrawRowIcon(e.Graphics, iconType, new Rectangle(18, (row.Height - 18) / 2, 18, 18));
            };

            int rightX = 698 - 20 - rightControl.Width;
            int textWidth = Math.Max(260, rightX - 48 - 16);

            titleLbl = new Label
            {
                Location = new Point(48, 13),
                Size = new Size(textWidth, 20),
                Font = new Font("Segoe UI Semibold", 9.5F),
                Tag = "row-title"
            };
            subLbl = new Label
            {
                Location = new Point(48, 35),
                Size = new Size(textWidth, 18),
                Font = new Font("Segoe UI", 8.5F),
                Tag = "row-subtitle"
            };

            rightControl.Location = new Point(rightX, (row.Height - rightControl.Height) / 2);

            row.Controls.Add(titleLbl);
            row.Controls.Add(subLbl);
            row.Controls.Add(rightControl);

            if (hasDivider)
            {
                Panel divider = new Panel
                {
                    Location = new Point(48, row.Height - 1),
                    Size = new Size(630, 1),
                    Tag = "row-divider"
                };
                row.Controls.Add(divider);
            }

            card.Controls.Add(row);
        }

        private static void FillNavRowHover(Graphics g, Rectangle bounds, bool roundTop, bool roundBottom, Color fill)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float r = Math.Min(UiDpi.S(11), Math.Min(bounds.Width, bounds.Height) / 2F);
            float d = r * 2F;
            RectangleF rect = new RectangleF(bounds.X, bounds.Y, bounds.Width - 0.5F, bounds.Height - 0.5F);
            using (GraphicsPath path = new GraphicsPath())
            {
                if (roundTop)
                {
                    path.AddArc(rect.Left, rect.Top, d, d, 180F, 90F);
                    path.AddArc(rect.Right - d, rect.Top, d, d, 270F, 90F);
                }
                else
                {
                    path.AddLine(rect.Left, rect.Top, rect.Right, rect.Top);
                }
                if (roundBottom)
                {
                    path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0F, 90F);
                    path.AddArc(rect.Left, rect.Bottom - d, d, d, 90F, 90F);
                }
                else
                {
                    path.AddLine(rect.Right, rect.Bottom, rect.Left, rect.Bottom);
                }
                path.CloseFigure();
                using (SolidBrush brush = new SolidBrush(fill))
                    g.FillPath(brush, path);
            }
        }

        private void AddNavigationRow(Panel card, int y, bool hasDivider, SettingIconType iconType, Action onClick, out Label titleLbl, out Label subLbl)
        {
            bool isFirstRow = (y == 0);
            bool isLastRow = !hasDivider;
            bool isHovered = false;
            Panel row = new BufferedPanel
            {
                Location = new Point(1, y + (isFirstRow ? 1 : 0)),
                Size = new Size(698, (isFirstRow || isLastRow) ? 67 : 68),
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand,
                Tag = "settings-row"
            };
            row.Paint += delegate(object sender, PaintEventArgs e)
            {
                if (isHovered)
                {
                    Color hoverBg = currentDark ? Color.FromArgb(46, 56, 68) : Color.FromArgb(238, 243, 248);
                    FillNavRowHover(e.Graphics, row.ClientRectangle, isFirstRow, isLastRow, hoverBg);
                }
                DrawRowIcon(e.Graphics, iconType, new Rectangle(18, (row.Height - 18) / 2, 18, 18));
            };

            titleLbl = new Label
            {
                Location = new Point(48, 14),
                Size = new Size(552, 20),
                Font = new Font("Segoe UI Semibold", 9.5F),
                Cursor = Cursors.Hand,
                Tag = "row-title"
            };
            subLbl = new Label
            {
                Location = new Point(48, 36),
                Size = new Size(552, 18),
                Font = new Font("Segoe UI", 8.5F),
                Cursor = Cursors.Hand,
                Tag = "row-subtitle"
            };
            Label arrowLbl = new Label
            {
                Text = ">",
                Location = new Point(660, 18),
                Size = new Size(20, 30),
                Font = new Font("Segoe UI", 12F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter,
                Cursor = Cursors.Hand,
                Tag = "row-subtitle"
            };

            Label navTitleLbl = titleLbl;
            Label navSubLbl = subLbl;
            Action myClearHover = null;
            Action<bool> setHover = delegate(bool on)
            {
                if (on && clearActiveNavHover != null && clearActiveNavHover != myClearHover)
                    clearActiveNavHover();
                if (isHovered == on) return;
                isHovered = on;
                if (on) clearActiveNavHover = myClearHover;
                else if (clearActiveNavHover == myClearHover) clearActiveNavHover = null;
                Color bg = on ? (currentDark ? Color.FromArgb(46, 56, 68) : Color.FromArgb(238, 243, 248)) : CardColor;
                navTitleLbl.BackColor = bg;
                navSubLbl.BackColor = bg;
                arrowLbl.BackColor = bg;
                row.Invalidate();
            };
            myClearHover = delegate { setHover(false); };
            resetNavHoverActions.Add(myClearHover);

            EventHandler clickHandler = delegate
            {
                setHover(false);
                onClick();
            };
            EventHandler enterHandler = delegate { setHover(true); };
            EventHandler leaveHandler = delegate
            {
                Point pt = row.PointToClient(Cursor.Position);
                if (pt.X < 0 || pt.Y < 0 || pt.X >= row.Width || pt.Y >= row.Height)
                    setHover(false);
            };

            row.Controls.Add(titleLbl);
            row.Controls.Add(subLbl);
            row.Controls.Add(arrowLbl);

            Panel divider = null;
            if (hasDivider)
            {
                divider = new Panel
                {
                    Location = new Point(48, row.Height - 1),
                    Size = new Size(630, 1),
                    Tag = "row-divider"
                };
                row.Controls.Add(divider);
            }

            List<Control> hoverTargets = new List<Control> { row, titleLbl, subLbl, arrowLbl };
            if (divider != null) hoverTargets.Add(divider);
            foreach (Control c in hoverTargets)
            {
                c.Click += clickHandler;
                c.MouseEnter += enterHandler;
                c.MouseLeave += leaveHandler;
            }

            card.Controls.Add(row);
        }

        private void DrawRowIcon(Graphics g, SettingIconType iconType, Rectangle r)
        {
            if (iconType == SettingIconType.None || r.Width <= 0 || r.Height <= 0) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color c = IconAccentColor;
            using (Pen p = new Pen(c, 2.0F) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            using (SolidBrush b = new SolidBrush(c))
            {
                float cx = r.X + r.Width / 2F;
                float cy = r.Y + r.Height / 2F;

                switch (iconType)
                {
                    case SettingIconType.Language:
                        g.DrawEllipse(p, r.X + 1.5F, r.Y + 1.5F, r.Width - 3F, r.Height - 3F);
                        g.DrawEllipse(p, cx - 3.2F, r.Y + 1.5F, 6.4F, r.Height - 3F);
                        g.DrawLine(p, r.X + 2F, cy, r.Right - 2F, cy);
                        break;

                    case SettingIconType.Theme:
                        g.DrawEllipse(p, r.X + 1.5F, r.Y + 1.5F, r.Width - 3F, r.Height - 3F);
                        g.FillPie(b, r.X + 1.5F, r.Y + 1.5F, r.Width - 3F, r.Height - 3F, -90F, 180F);
                        break;

                    case SettingIconType.Startup:
                        g.DrawLine(p, cx, r.Bottom - 2F, cx, r.Y + 3F);
                        g.DrawLines(p, new PointF[] {
                            new PointF(cx - 3.5F, r.Y + 6.5F),
                            new PointF(cx, r.Y + 2.5F),
                            new PointF(cx + 3.5F, r.Y + 6.5F)
                        });
                        g.DrawLine(p, r.X + 2.5F, r.Bottom - 2F, r.Right - 2.5F, r.Bottom - 2F);
                        break;

                    case SettingIconType.CloseWindow:
                        g.DrawRectangle(p, r.X + 1.5F, r.Y + 2.5F, r.Width - 3F, r.Height - 5F);
                        g.DrawLine(p, cx - 2.5F, cy - 1F, cx + 2.5F, cy + 4F);
                        g.DrawLine(p, cx + 2.5F, cy - 1F, cx - 2.5F, cy + 4F);
                        break;

                    case SettingIconType.Shortcut:
                        g.DrawRectangle(p, r.X + 1.5F, r.Y + 3.5F, r.Width - 3F, r.Height - 7F);
                        g.DrawLine(p, r.X + 4.5F, cy - 1.5F, r.X + 7.5F, cy - 1.5F);
                        g.DrawLine(p, r.X + 6F, cy - 3F, r.X + 6F, cy + 1.5F);
                        g.DrawLine(p, r.Right - 7.5F, cy + 1.5F, r.Right - 4.5F, cy - 1.5F);
                        break;

                    case SettingIconType.Update:
                        g.DrawArc(p, r.X + 2F, r.Y + 2F, r.Width - 4F, r.Height - 4F, 200F, 145F);
                        g.DrawLines(p, new PointF[] {
                            new PointF(r.Right - 5.5F, r.Y + 3.5F),
                            new PointF(r.Right - 2.2F, r.Y + 4.8F),
                            new PointF(r.Right - 3.5F, r.Y + 8F)
                        });
                        g.DrawArc(p, r.X + 2F, r.Y + 2F, r.Width - 4F, r.Height - 4F, 20F, 145F);
                        g.DrawLines(p, new PointF[] {
                            new PointF(r.X + 5.5F, r.Bottom - 3.5F),
                            new PointF(r.X + 2.2F, r.Bottom - 4.8F),
                            new PointF(r.X + 3.5F, r.Bottom - 8F)
                        });
                        break;

                    case SettingIconType.Troubleshooting:
                        g.DrawLine(p, r.X + 3.5F, r.Bottom - 3.5F, r.Right - 6.5F, r.Y + 6.5F);
                        g.DrawArc(p, r.Right - 9F, r.Y + 1.5F, 7.5F, 7.5F, 135F, 270F);
                        break;

                    case SettingIconType.About:
                        g.DrawEllipse(p, r.X + 1.5F, r.Y + 1.5F, r.Width - 3F, r.Height - 3F);
                        g.FillEllipse(b, cx - 1F, r.Y + 4.5F, 2F, 2F);
                        g.DrawLine(p, cx, cy - 0.5F, cx, r.Bottom - 4.5F);
                        break;
                }
            }
        }

        public bool RegisterGlobalHotkey(bool warnOnFailure = false)
        {
            if (hostForm == null || !hostForm.IsHandleCreated) return false;
            try { UnregisterHotKey(hostForm.Handle, HOTKEY_ID); } catch { }
            if (config.HotkeyKey == Keys.None) return true;
            bool registered = false;
            try { registered = RegisterHotKey(hostForm.Handle, HOTKEY_ID, config.HotkeyModifiers, (uint)config.HotkeyKey); }
            catch { }
            if (!registered && warnOnFailure)
                ShowToast(T("Shortcut is already used by another app", "快捷键已被其他程序占用"));
            return registered;
        }

        // Called when Windows changes its light/dark setting: only matters while the app theme is "System".
        public void OnSystemThemeChanged()
        {
            if (config == null || config.ThemeMode != "System") return;
            if (IsWindowsSystemDark() == currentDark) return;
            ApplyVisualTheme(true);
        }

        public void UnregisterGlobalHotkey()
        {
            if (hostForm == null || !hostForm.IsHandleCreated) return;
            try { UnregisterHotKey(hostForm.Handle, HOTKEY_ID); } catch { }
        }

        private static bool IsWindowsSystemDark()
        {
            try
            {
                object val = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
                return val != null && Convert.ToInt32(val) == 0;
            }
            catch { return true; }
        }

        public void ApplyVisualTheme(bool notifyHost)
        {
            if (notifyHost && onThemeApplied != null)
            {
                // Delegate to host ApplyTheme() so WM_SETREDRAW batches the entire window repaint.
                currentDark = config.ThemeMode == "Dark" || (config.ThemeMode == "System" && IsWindowsSystemDark());
                onThemeApplied(currentDark);
            }
            else
            {
                ApplyVisualThemeCore(false);
            }
        }

        private void ApplyVisualThemeCore(bool notifyHost)
        {
            currentDark = config.ThemeMode == "Dark" || (config.ThemeMode == "System" && IsWindowsSystemDark());

            BgColor = currentDark ? Color.FromArgb(18, 22, 28) : Color.FromArgb(238, 242, 247);
            CardColor = currentDark ? Color.FromArgb(31, 38, 47) : Color.White;
            CardBorderColor = currentDark ? Color.FromArgb(74, 88, 106) : Color.FromArgb(186, 198, 212);
            InputBorderColor = currentDark ? Color.FromArgb(106, 124, 146) : Color.FromArgb(156, 172, 190);
            DividerColor = currentDark ? Color.FromArgb(50, 60, 72) : Color.FromArgb(228, 234, 241);
            TitleColor = currentDark ? Color.FromArgb(240, 244, 248) : Color.FromArgb(24, 32, 42);
            SubtitleColor = currentDark ? Color.FromArgb(154, 168, 184) : Color.FromArgb(98, 112, 128);
            ControlBgColor = currentDark ? Color.FromArgb(46, 56, 68) : Color.FromArgb(247, 250, 253);

            BackColor = BgColor;
            scrollContainer.BackColor = BgColor;
            centerColumn.BackColor = BgColor;
            if (vScrollBar != null)
            {
                vScrollBar.BackColor = BgColor;
                vScrollBar.ThumbColor = currentDark ? Color.FromArgb(74, 86, 102) : Color.FromArgb(188, 198, 210);
                vScrollBar.ThumbHoverColor = currentDark ? Color.FromArgb(115, 132, 154) : Color.FromArgb(148, 162, 178);
                vScrollBar.Invalidate();
            }
            toastPanel.BackColor = currentDark ? Color.FromArgb(42, 50, 60) : Color.White;

            themeSegment.TrackColor = currentDark ? Color.FromArgb(22, 27, 34) : Color.FromArgb(226, 232, 240);
            themeSegment.ThumbColor = currentDark ? Color.FromArgb(62, 75, 92) : Color.White;
            themeSegment.SelectedTextColor = TitleColor;
            themeSegment.UnselectedTextColor = SubtitleColor;

            foreach (Action resetHover in resetNavHoverActions) resetHover();
            UpdateTreeColors(centerColumn);
            Invalidate(true);

            if (notifyHost && onThemeApplied != null)
                onThemeApplied(currentDark);
        }

        private void UpdateTreeColors(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                string tag = Convert.ToString(c.Tag);
                if (tag == "group-title") { c.BackColor = BgColor; c.ForeColor = SubtitleColor; }
                else if (tag == "settings-card") c.BackColor = CardColor;
                else if (tag == "settings-row") c.BackColor = Color.Transparent;
                else if (tag == "settings-row-box") c.BackColor = CardColor;
                else if (tag == "row-title") { c.BackColor = CardColor; c.ForeColor = TitleColor; }
                else if (tag == "row-subtitle") { c.BackColor = CardColor; c.ForeColor = SubtitleColor; }
                else if (tag == "row-divider") c.BackColor = DividerColor;
                else if (c is ToggleSwitch)
                {
                    ((ToggleSwitch)c).StateTextColor = SubtitleColor;
                    c.Invalidate();
                }
                else if (c is ComboBox)
                {
                    c.BackColor = ControlBgColor;
                    c.ForeColor = TitleColor;
                    if (c is ThemedComboBox) ((ThemedComboBox)c).BorderColor = InputBorderColor;
                    c.Invalidate();
                }
                else if (c is Button)
                {
                    Button btn = (Button)c;
                    btn.BackColor = ControlBgColor;
                    btn.ForeColor = TitleColor;
                    btn.FlatAppearance.BorderColor = InputBorderColor;
                    btn.FlatAppearance.BorderSize = 1;
                }
                UpdateTreeColors(c);
            }
        }

        private RoundedButton MakeDialogButton(string text, int x, int y, int w, bool accent)
        {
            RoundedButton b = new RoundedButton
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(w, 38),
                Font = new Font("Segoe UI Semibold", 9F),
                BackColor = accent ? Color.FromArgb(210, 48, 66) : ControlBgColor,
                ForeColor = accent ? Color.White : TitleColor,
                FlatStyle = FlatStyle.Flat
            };
            b.FlatAppearance.BorderColor = InputBorderColor;
            if (accent) b.FlatAppearance.BorderSize = 0;
            return b;
        }

        private List<DiagItem> CollectDiagnostics()
        {
            List<DiagItem> items = DiagnosticsItemsProvider != null ? DiagnosticsItemsProvider() : new List<DiagItem>();
            return items;
        }

        private void ShowTroubleshootingDialog()
        {
            using (Form dlg = new Form())
            {
                string title = T("Troubleshooting", "故障排查");
                dlg.Text = title;
                dlg.ClientSize = new Size(520, 520);
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.MaximizeBox = false;
                dlg.MinimizeBox = false;
                dlg.BackColor = CardColor;
                dlg.ForeColor = TitleColor;
                dlg.HandleCreated += delegate { ChargeLimitForm.ApplyNativeTitleBarTheme(dlg.Handle, currentDark, CardColor, TitleColor); };

                string appDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NitroHub");

                Label heading = new Label
                {
                    Text = title, Font = UiLang.GetCachedFont("Segoe UI Semibold", 15F, FontStyle.Regular), ForeColor = TitleColor,
                    Location = new Point(24, 18), Size = new Size(472, 32)
                };
                Label sub = new Label
                {
                    Text = T("Check the Acer interfaces and fix common problems", "检查 Acer 接口并修复常见问题"),
                    Font = UiLang.GetCachedFont("Segoe UI", 9F, FontStyle.Regular), ForeColor = SubtitleColor,
                    Location = new Point(24, 52), Size = new Size(472, 22)
                };
                Panel list = new Panel { Location = new Point(24, 86), Size = new Size(472, 264), BackColor = ControlBgColor };

                Action populate = null;
                populate = delegate
                {
                    list.Controls.Clear();
                    int y = 8;
                    foreach (DiagItem it in CollectDiagnostics())
                    {
                        Color dot = it.Level == 0 ? Color.FromArgb(76, 190, 120)
                            : (it.Level == 1 ? Color.FromArgb(140, 150, 162) : Color.FromArgb(226, 86, 86));
                        list.Controls.Add(new Label
                        {
                            Text = "●", ForeColor = dot, Font = UiLang.GetCachedFont("Segoe UI", 9F, FontStyle.Regular),
                            Location = new Point(12, y), Size = new Size(18, 28), TextAlign = ContentAlignment.MiddleCenter
                        });
                        list.Controls.Add(new Label
                        {
                            Text = it.Name, ForeColor = TitleColor, Font = UiLang.GetCachedFont("Segoe UI Semibold", 9F, FontStyle.Regular),
                            Location = new Point(34, y), Size = new Size(160, 28), TextAlign = ContentAlignment.MiddleLeft
                        });
                        list.Controls.Add(new Label
                        {
                            Text = it.Value, ForeColor = SubtitleColor, Font = UiLang.GetCachedFont("Segoe UI", 9F, FontStyle.Regular),
                            Location = new Point(196, y), Size = new Size(264, 28), TextAlign = ContentAlignment.MiddleRight,
                            AutoEllipsis = true
                        });
                        y += 30;
                    }
                };
                populate();

                RoundedButton rerunBtn = MakeDialogButton(T("Re-run Checks", "重新检测"), 24, 364, 228, false);
                rerunBtn.Click += delegate { populate(); };

                RoundedButton copyBtn = MakeDialogButton(T("Copy Report", "复制报告"), 268, 364, 228, false);
                copyBtn.Click += delegate
                {
                    StringBuilder sb = new StringBuilder();
                    sb.AppendLine("NitroHub " + ChargeLimitForm.GetApplicationVersion());
                    foreach (DiagItem it in CollectDiagnostics()) sb.AppendLine(it.Name + ": " + it.Value);
                    try
                    {
                        Clipboard.SetText(sb.ToString());
                        MessageBox.Show(dlg, T("Report copied to the clipboard.", "报告已复制到剪贴板。"), title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(dlg, ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                };

                RoundedButton repairBtn = MakeDialogButton(T("Repair Startup Task", "修复开机自启任务"), 24, 412, 228, false);
                repairBtn.Click += delegate
                {
                    if (!config.StartOnStartup)
                    {
                        MessageBox.Show(dlg, T("Turn on \"Start on startup\" first.", "请先开启“开机自动启动”。"), title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    string err = ensureStartupTaskFunc != null ? ensureStartupTaskFunc() : null;
                    if (String.IsNullOrEmpty(err))
                        MessageBox.Show(dlg, T("Startup task repaired.", "开机自启任务已修复。"), title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    else
                        MessageBox.Show(dlg, T("Could not repair the startup task. Run NitroHub as Administrator and try again.\n\n",
                        "无法修复开机自启任务，请以管理员身份运行后重试。\n\n") + err, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    populate();
                };

                RoundedButton folderBtn = MakeDialogButton(T("Open Settings Folder", "打开设置文件夹"), 268, 412, 228, false);
                folderBtn.Click += delegate
                {
                    try
                    {
                        Directory.CreateDirectory(appDir);
                        Process.Start(new ProcessStartInfo(appDir) { UseShellExecute = true });
                    }
                    catch { }
                };

                RoundedButton resetAllBtn = MakeDialogButton(T("Reset All Settings", "恢复默认设置"), 24, 464, 472, true);
                resetAllBtn.Click += delegate
                {
                    if (MessageBox.Show(dlg,
                        T("Are you sure you want to reset all settings to default? This cannot be undone.",
                        "确定要将所有设置恢复为默认吗？此操作无法撤销。"),
                        T("Reset All Settings", "恢复默认设置"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                    {
                        config = new AppSettingsConfig();
                        config.Save();
                        if (removeStartupTaskAction != null) removeStartupTaskAction();

                        languageCombo.SelectedItem = config.Language;
                        themeSegment.SelectedIndex = 2;
                        startupSwitch.Checked = false;
                        PopulateCloseBehaviorOptions();
                        modifierCombo.SelectedItem = "Ctrl + Alt";
                        keyCombo.SelectedItem = "N";
                        RegisterGlobalHotkey();
                        if (SettingsResetAction != null) SettingsResetAction();
                        ApplyLocalization();
                        if (onLanguageApplied != null) onLanguageApplied(config.Language);
                        ApplyVisualTheme(true);
                        dlg.Close();
                    }
                };

                dlg.Controls.AddRange(new Control[] { heading, sub, list, rerunBtn, copyBtn, repairBtn, folderBtn, resetAllBtn });
                UiDpi.ScaleDialog(dlg);
                dlg.ShowDialog(hostForm);
            }
        }

        private void ShowAboutDialog()
        {
            using (Form dlg = new Form())
            {
                dlg.Text = T("About NitroHub", "关于 NitroHub");
                dlg.Size = new Size(380, 260);
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.MaximizeBox = false;
                dlg.MinimizeBox = false;
                dlg.BackColor = CardColor;
                dlg.ForeColor = TitleColor;
                dlg.HandleCreated += delegate { ChargeLimitForm.ApplyNativeTitleBarTheme(dlg.Handle, currentDark, CardColor, TitleColor); };

                Label nameLbl = new Label
                {
                    Text = "NitroHub",
                    Font = UiLang.GetCachedFont("Segoe UI Semibold", 14F, FontStyle.Regular),
                    Location = new Point(20, 24),
                    Size = new Size(324, 28),
                    TextAlign = ContentAlignment.MiddleCenter
                };
                Label verLbl = new Label
                {
                    Text = T("Version ", "版本 ") + ChargeLimitForm.GetApplicationVersion(),
                    Font = UiLang.GetCachedFont("Segoe UI", 9F, FontStyle.Regular),
                    ForeColor = SubtitleColor,
                    Location = new Point(20, 54),
                    Size = new Size(324, 20),
                    TextAlign = ContentAlignment.MiddleCenter
                };
                Label copyLbl = new Label
                {
                    Text = T("Copyright © 2026 NitroHub Contributors.\nAll rights reserved.",
                        "Copyright © 2026 NitroHub Contributors.\n保留所有权利。"),
                    Font = UiLang.GetCachedFont("Segoe UI", 8.5F, FontStyle.Regular),
                    ForeColor = SubtitleColor,
                    Location = new Point(20, 86),
                    Size = new Size(324, 36),
                    TextAlign = ContentAlignment.MiddleCenter
                };

                LinkLabel webLink = new LinkLabel { Text = T("Website", "官方网站"), Location = new Point(56, 150), AutoSize = true, LinkColor = Color.FromArgb(126, 166, 214) };
                LinkLabel privacyLink = new LinkLabel { Text = T("Privacy Policy", "隐私策略"), Location = new Point(142, 150), AutoSize = true, LinkColor = Color.FromArgb(126, 166, 214) };
                LinkLabel feedbackLink = new LinkLabel { Text = T("Send Feedback", "发送反馈"), Location = new Point(238, 150), AutoSize = true, LinkColor = Color.FromArgb(126, 166, 214) };

                webLink.LinkClicked += delegate
                {
                    try { Process.Start(new ProcessStartInfo(RepoUrl) { UseShellExecute = true }); }
                    catch { }
                };
                privacyLink.LinkClicked += delegate
                {
                    MessageBox.Show(dlg,
                        T("NitroHub stores all preferences, logs, and reports locally on your device. No telemetry or personal data is collected or transmitted.",
                        "NitroHub 的所有偏好设置、日志及电池报告均仅保存在您的本地设备中，不会收集或上传任何遥测或个人数据。"),
                        T("Privacy Policy", "隐私策略"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                };
                feedbackLink.LinkClicked += delegate
                {
                    try { Process.Start(new ProcessStartInfo(IssuesUrl) { UseShellExecute = true }); }
                    catch { }
                };

                dlg.Controls.AddRange(new Control[] { nameLbl, verLbl, copyLbl, webLink, privacyLink, feedbackLink });
                UiDpi.ScaleDialog(dlg);
                dlg.ShowDialog(hostForm);
            }
        }

        private void UpdateVersionSubtitle()
        {
            if (updateSubtitleLabel == null) return;
            string text = T("Current version: ", "当前版本: ") + ChargeLimitForm.GetApplicationVersion();
            if (!String.IsNullOrEmpty(latestAvailableTag))
                text += " -> " + latestAvailableTag;
            else if (updateCheckedUpToDate)
                text += T(" (Up to date)", " (已是最新)");
            updateSubtitleLabel.Text = text;
        }

        private void StartUpdateCheck()
        {
            if (checkUpdateBtn == null || !checkUpdateBtn.Enabled) return;
            checkUpdateBtn.Enabled = false;
            checkUpdateBtn.Text = T("Checking...", "检查中...");
            string currentVer = ChargeLimitForm.GetApplicationVersion();

            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                string latestTag = null;
                string releasePage = ReleasesUrl;
                string errorMsg = null;
                bool noReleaseYet = false;

                try
                {
                    // Enable TLS 1.2 (3072) on .NET 4.x where the Tls12 enum constant may not exist at compile time.
                    ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create(LatestReleaseApiUrl);
                    req.Method = "GET";
                    req.UserAgent = "NitroHub/" + currentVer;
                    req.Accept = "application/vnd.github+json";
                    req.Timeout = 10000;
                    req.ReadWriteTimeout = 10000;

                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    using (Stream stream = resp.GetResponseStream())
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        string json = reader.ReadToEnd();
                        Match tagMatch = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
                        if (tagMatch.Success) latestTag = tagMatch.Groups[1].Value.Trim();
                        Match urlMatch = Regex.Match(json, "\"html_url\"\\s*:\\s*\"(https://github\\.com/[^\"]+/releases/[^\"]+)\"");
                        if (urlMatch.Success && urlMatch.Groups[1].Value.StartsWith(RepoUrl + "/", StringComparison.OrdinalIgnoreCase))
                            releasePage = urlMatch.Groups[1].Value.Trim();
                    }
                }
                catch (WebException wex)
                {
                    HttpWebResponse httpResp = wex.Response as HttpWebResponse;
                    if (httpResp != null && httpResp.StatusCode == HttpStatusCode.NotFound)
                        noReleaseYet = true;
                    else
                        errorMsg = wex.Message;
                }
                catch (Exception ex)
                {
                    errorMsg = ex.Message;
                }

                try
                {
                    if (IsDisposed || !IsHandleCreated) return;
                    BeginInvoke(new MethodInvoker(delegate
                    {
                        if (IsDisposed) return;
                        checkUpdateBtn.Enabled = true;
                        checkUpdateBtn.Text = T("Check for Updates", "检查更新");

                        if (!String.IsNullOrEmpty(errorMsg))
                        {
                            latestAvailableTag = null;
                            updateCheckedUpToDate = false;
                            UpdateVersionSubtitle();
                            MessageBox.Show(hostForm,
                                T("Could not check for updates right now.\n\n", "暂时无法检查更新。\n\n") + errorMsg,
                                T("Software Updates", "软件更新"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }

                        if (noReleaseYet || String.IsNullOrEmpty(latestTag) || CompareVersionStrings(latestTag, currentVer) <= 0)
                        {
                            latestAvailableTag = null;
                            updateCheckedUpToDate = true;
                            UpdateVersionSubtitle();
                            MessageBox.Show(hostForm,
                                T("You are running the latest version of NitroHub (", "当前已是最新版本的 NitroHub (") + currentVer + ").",
                                T("Software Updates", "软件更新"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                            return;
                        }

                        latestAvailableTag = latestTag;
                        updateCheckedUpToDate = false;
                        UpdateVersionSubtitle();
                        if (MessageBox.Show(hostForm,
                            T("A new version of NitroHub is available: ", "发现新版本 NitroHub: ") + latestTag +
                            T("\n\nOpen the GitHub Releases page to download it?", "\n\n是否打开 GitHub 发布页下载？"),
                            T("Software Updates", "软件更新"), MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                        {
                            try { Process.Start(new ProcessStartInfo(releasePage) { UseShellExecute = true }); }
                            catch { }
                        }
                    }));
                }
                catch { }
            });
        }

        private static int CompareVersionStrings(string a, string b)
        {
            int[] pa = ParseVersionParts(a);
            int[] pb = ParseVersionParts(b);
            int len = Math.Max(pa.Length, pb.Length);
            for (int i = 0; i < len; i++)
            {
                int va = i < pa.Length ? pa[i] : 0;
                int vb = i < pb.Length ? pb[i] : 0;
                if (va != vb) return va.CompareTo(vb);
            }
            return 0;
        }

        private static int[] ParseVersionParts(string version)
        {
            if (String.IsNullOrEmpty(version)) return new int[] { 0 };
            string s = version.Trim();
            if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s.Substring(1);
            int cut = s.IndexOfAny(new char[] { '-', '+' });
            if (cut >= 0) s = s.Substring(0, cut);
            string[] tokens = s.Split('.');
            int[] nums = new int[tokens.Length];
            for (int i = 0; i < tokens.Length; i++)
            {
                int n;
                Int32.TryParse(tokens[i], out n);
                nums[i] = n;
            }
            return nums;
        }
    }
}
