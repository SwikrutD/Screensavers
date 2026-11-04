// Nocturne - a quiet, dark screensaver.
// A dim moon in its real current phase, a sparse twinkling sky, and the time
// written in words. Everything drifts very slowly to avoid burn-in.
//
// Screensaver args: /s (run), /p <hwnd> (preview), /c (settings)
// Debug: /shot <file.png> [phase 0..1]  renders one 1920x1080 frame.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

static class Program
{
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int index, int value);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    public static readonly Stopwatch Clock = Stopwatch.StartNew();

    [STAThread]
    static void Main(string[] args)
    {
        SetProcessDPIAware();
        Application.EnableVisualStyles();

        string mode = "/s";
        string arg2 = null;
        if (args.Length > 0)
        {
            string a = args[0].ToLowerInvariant().Trim();
            if (a.Length > 2 && a[2] == ':') { arg2 = a.Substring(3); a = a.Substring(0, 2); }
            mode = a;
            if (arg2 == null && args.Length > 1) arg2 = args[1];
        }

        if (mode == "/c")
        {
            MessageBox.Show("Nocturne has no settings.\n\nThe moon shows its real phase tonight.",
                "Nocturne", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        else if (mode == "/p" && arg2 != null)
        {
            IntPtr parent = new IntPtr(long.Parse(arg2));
            SaverForm form = new SaverForm(parent);
            Application.Run(form);
        }
        else if (mode == "/shot" && arg2 != null)
        {
            Scene scene = new Scene(1920, 1080, true, 7);
            if (args.Length > 2) scene.PhaseOverride = double.Parse(args[2], CultureInfo.InvariantCulture);
            using (Bitmap bmp = new Bitmap(1920, 1080))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                scene.Render(g, 30.0, DateTime.Now);
                bmp.Save(arg2, ImageFormat.Png);
            }
        }
        else
        {
            Screen primary = Screen.PrimaryScreen;
            int seed = 1;
            foreach (Screen s in Screen.AllScreens)
            {
                SaverForm f = new SaverForm(s.Bounds, s.Primary, seed++);
                f.Show();
            }
            Application.Run();
        }
    }
}

class SaverForm : Form
{
    Scene scene;
    Timer timer;
    bool preview;
    bool primary;
    int seed;
    IntPtr parentHwnd;
    Point mouseStart;
    bool haveMouseStart;

    public SaverForm(Rectangle bounds, bool primary, int seed)
    {
        this.primary = primary;
        this.seed = seed;
        Init();
        StartPosition = FormStartPosition.Manual;
        Bounds = bounds;
        TopMost = true;
        Cursor.Hide();
    }

    public SaverForm(IntPtr parent)
    {
        preview = true;
        primary = true;
        seed = 1;
        parentHwnd = parent;
        Init();
        CreateHandle();
        Program.SetParent(Handle, parent);
        Program.SetWindowLong(Handle, -16, Program.GetWindowLong(Handle, -16) | 0x40000000); // WS_CHILD
        Program.RECT rc;
        Program.GetClientRect(parent, out rc);
        Location = new Point(0, 0);
        Size = new Size(rc.Right, rc.Bottom);
    }

    void Init()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        BackColor = Color.Black;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);
        timer = new Timer();
        timer.Interval = 33;
        timer.Tick += delegate
        {
            if (preview && !Program.IsWindow(parentHwnd)) { Application.Exit(); return; }
            Invalidate();
        };
        timer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        int w = ClientSize.Width, h = ClientSize.Height;
        if (w <= 0 || h <= 0) return;
        if (scene == null || scene.Width != w || scene.Height != h)
            scene = new Scene(w, h, primary, seed);
        scene.Render(e.Graphics, Program.Clock.Elapsed.TotalSeconds, DateTime.Now);
    }

    void Quit()
    {
        if (preview) return;
        Application.Exit();
    }

    protected override void OnKeyDown(KeyEventArgs e) { Quit(); }
    protected override void OnMouseDown(MouseEventArgs e) { Quit(); }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (preview) return;
        Point p = Cursor.Position;
        if (!haveMouseStart) { mouseStart = p; haveMouseStart = true; return; }
        if (Math.Abs(p.X - mouseStart.X) > 10 || Math.Abs(p.Y - mouseStart.Y) > 10) Quit();
    }
}

class Star
{
    public float X, Y, Size;
    public double BaseAlpha, Speed, Phase;
    public Color Tint;
}

class Scene
{
    public readonly int Width, Height;
    public double? PhaseOverride;

    readonly bool primary;
    readonly Random rng;
    readonly Star[] stars;
    readonly Bitmap background;
    readonly Font mainFont, subFont;
    readonly Dictionary<string, float> widthCache = new Dictionary<string, float>();
    readonly SolidBrush brush = new SolidBrush(Color.White);
    readonly StringFormat typo;

    Bitmap moon;
    double moonPhase = -1;
    int moonRadius;

    string curText, prevText;
    double textChangedAt = -100;

    double nextShoot, shootStart = -100;
    PointF shootFrom;
    PointF shootDir;

    const double MoonBrightness = 0.62;  // overall cap so a full moon stays gentle
    static readonly Color GlowColor = Color.FromArgb(120, 135, 160);

    public Scene(int w, int h, bool primary, int seed)
    {
        Width = w;
        Height = h;
        this.primary = primary;
        rng = new Random(seed * 7919 + Environment.TickCount);

        background = BakeBackground(w, h);

        int count = Math.Max(10, w * h / 28000);
        stars = new Star[count];
        for (int i = 0; i < count; i++)
        {
            Star s = new Star();
            s.X = (float)rng.NextDouble();
            s.Y = (float)rng.NextDouble();
            double big = rng.NextDouble();
            s.Size = (float)(0.7 + big * big * big * 1.6) * Math.Max(0.5f, h / 1080f);
            s.BaseAlpha = 35 + rng.NextDouble() * rng.NextDouble() * 140;
            s.Speed = 0.15 + rng.NextDouble() * 0.6;
            s.Phase = rng.NextDouble() * Math.PI * 2;
            double tint = rng.NextDouble();
            s.Tint = tint < 0.2 ? Color.FromArgb(255, 226, 196)
                   : tint < 0.5 ? Color.FromArgb(196, 212, 255)
                   : Color.FromArgb(232, 236, 246);
            stars[i] = s;
        }

        mainFont = new Font("Segoe UI Light", Math.Max(6f, h * 0.075f), FontStyle.Regular, GraphicsUnit.Pixel);
        subFont = new Font("Segoe UI", Math.Max(5f, h * 0.0128f), FontStyle.Regular, GraphicsUnit.Pixel);
        typo = (StringFormat)StringFormat.GenericTypographic.Clone();
        typo.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;

        nextShoot = 40 + rng.NextDouble() * 60;
    }

    public void Render(Graphics g, double t, DateTime now)
    {
        g.DrawImageUnscaled(background, 0, 0);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        DrawConstellations(g, t);
        DrawStars(g, t);
        DrawShootingStar(g, t);

        if (primary)
        {
            double phase = PhaseOverride.HasValue ? PhaseOverride.Value : MoonPhase(now);
            int r = Math.Max(6, (int)(Height * 0.17));
            if (moon == null || r != moonRadius || Math.Abs(phase - moonPhase) > 0.002)
            {
                if (moon != null) moon.Dispose();
                moon = BakeMoon(r, phase);
                moonRadius = r;
                moonPhase = phase;
            }

            // Slow wander across the middle of the screen (periods of many minutes).
            double cx = Width * (0.5 + 0.17 * Math.Sin(t / 410.0));
            double cy = Height * (0.38 + 0.07 * Math.Sin(t / 290.0 + 1.3));
            g.DrawImageUnscaled(moon, (int)(cx - moon.Width / 2.0), (int)(cy - moon.Height / 2.0));

            DrawText(g, t, now, phase, (float)cx, (float)(cy + r + Height * 0.085));
        }

        // Gentle fade-in from black.
        double fade = Math.Min(1.0, t / 3.0);
        if (fade < 1.0)
        {
            brush.Color = Color.FromArgb((int)((1 - fade) * 255), 0, 0, 0);
            g.FillRectangle(brush, 0, 0, Width, Height);
        }
    }

    void DrawStars(Graphics g, double t)
    {
        float drift = (float)(t * 0.22);
        foreach (Star s in stars)
        {
            float x = (s.X * Width + drift) % Width;
            float y = s.Y * Height;
            double a = s.BaseAlpha * (0.55 + 0.45 * Math.Sin(t * s.Speed + s.Phase));
            brush.Color = Color.FromArgb(Clamp((int)a), s.Tint);
            g.FillEllipse(brush, x - s.Size / 2, y - s.Size / 2, s.Size, s.Size);
        }
    }

    // Faint constellations behind everything, drifting with the star field.
    // Points are (x, y) in a unit box; Links index pairs into Points.
    class Constellation
    {
        public float X, Y, Scale;          // origin and size, as fractions of screen height
        public float[] Points;
        public int[] Links;
        public Constellation(float x, float y, float scale, float[] pts, int[] links)
        { X = x; Y = y; Scale = scale; Points = pts; Links = links; }
    }

    static readonly Constellation[] Constellations =
    {
        // Big Dipper
        new Constellation(0.04f, 0.10f, 0.26f,
            new float[] { 0,0.55f, 0.18f,0.62f, 0.34f,0.74f, 0.52f,0.80f, 0.55f,0.5f, 0.82f,0.5f, 0.88f,0.74f },
            new int[] { 0,1, 1,2, 2,3, 3,4, 4,5, 5,6, 6,3 }),
        // Cassiopeia
        new Constellation(0.62f, 0.07f, 0.20f,
            new float[] { 0,0.2f, 0.22f,0.7f, 0.5f,0.35f, 0.76f,0.8f, 1.0f,0.3f },
            new int[] { 0,1, 1,2, 2,3, 3,4 }),
        // Orion
        new Constellation(0.30f, 0.60f, 0.30f,
            new float[] { 0.15f,0.05f, 0.85f,0.10f, 0.35f,0.50f, 0.50f,0.52f, 0.65f,0.54f, 0.10f,0.95f, 0.90f,0.92f },
            new int[] { 0,2, 1,4, 2,3, 3,4, 2,5, 4,6 }),
        // Cygnus
        new Constellation(0.82f, 0.50f, 0.22f,
            new float[] { 0.5f,0, 0.5f,0.35f, 0.5f,1.0f, 0.1f,0.4f, 0.9f,0.3f },
            new int[] { 0,1, 1,2, 3,1, 1,4 }),
        // Scorpius-ish hook
        new Constellation(0.02f, 0.66f, 0.22f,
            new float[] { 0.1f,0, 0.3f,0.15f, 0.4f,0.35f, 0.42f,0.6f, 0.55f,0.8f, 0.8f,0.9f, 1.0f,0.7f },
            new int[] { 0,1, 1,2, 2,3, 3,4, 4,5, 5,6 }),
    };

    void DrawConstellations(Graphics g, double t)
    {
        float drift = (float)(t * 0.22);
        float unit = Height;
        using (Pen pen = new Pen(Color.FromArgb(26, 150, 170, 210), Math.Max(1f, Height / 1080f)))
        {
            foreach (Constellation c in Constellations)
            {
                float span = 0;
                for (int i = 0; i < c.Points.Length; i += 2) span = Math.Max(span, c.Points[i]);
                float w = span * c.Scale * unit;
                float baseX = ((c.X * Width + drift) % Width + Width) % Width;
                // Draw twice so one wraps cleanly across the right edge.
                for (int pass = 0; pass < 2; pass++)
                {
                    float ox = baseX - pass * Width;
                    if (ox > Width || ox + w < 0) continue;
                    float oy = c.Y * Height;
                    for (int i = 0; i < c.Links.Length; i += 2)
                    {
                        int a = c.Links[i] * 2, b = c.Links[i + 1] * 2;
                        g.DrawLine(pen, ox + c.Points[a] * c.Scale * unit, oy + c.Points[a + 1] * c.Scale * unit,
                                        ox + c.Points[b] * c.Scale * unit, oy + c.Points[b + 1] * c.Scale * unit);
                    }
                    float dot = Math.Max(2f, Height / 400f);
                    brush.Color = Color.FromArgb(90, 200, 215, 245);
                    for (int i = 0; i < c.Points.Length; i += 2)
                    {
                        float px = ox + c.Points[i] * c.Scale * unit, py = oy + c.Points[i + 1] * c.Scale * unit;
                        g.FillEllipse(brush, px - dot / 2, py - dot / 2, dot, dot);
                    }
                }
            }
        }
    }

    void DrawShootingStar(Graphics g, double t)
    {
        const double duration = 1.1;
        if (t > nextShoot)
        {
            shootStart = t;
            nextShoot = t + 150 + rng.NextDouble() * 240;
            shootFrom = new PointF((float)(Width * (0.15 + rng.NextDouble() * 0.7)),
                                   (float)(Height * (0.05 + rng.NextDouble() * 0.3)));
            double ang = (20 + rng.NextDouble() * 25) * Math.PI / 180;
            float dirX = rng.Next(2) == 0 ? -1 : 1;
            float len = Height * 0.28f;
            shootDir = new PointF((float)Math.Cos(ang) * dirX * len, (float)Math.Sin(ang) * len);
        }

        double u = (t - shootStart) / duration;
        if (u < 0 || u > 1) return;

        double ease = 1 - (1 - u) * (1 - u);
        double tail = 0.35 * Math.Sin(Math.PI * u);
        PointF head = new PointF(shootFrom.X + shootDir.X * (float)ease, shootFrom.Y + shootDir.Y * (float)ease);
        PointF back = new PointF(head.X - shootDir.X * (float)tail, head.Y - shootDir.Y * (float)tail);
        if (Math.Abs(head.X - back.X) < 1 && Math.Abs(head.Y - back.Y) < 1) return;

        int alpha = Clamp((int)(95 * Math.Sin(Math.PI * u)));
        using (LinearGradientBrush lg = new LinearGradientBrush(back, head,
            Color.FromArgb(0, 210, 220, 240), Color.FromArgb(alpha, 210, 220, 240)))
        using (Pen pen = new Pen(lg, Math.Max(1f, Height / 900f)))
        {
            g.DrawLine(pen, back, head);
        }
    }

    void DrawText(Graphics g, double t, DateTime now, double phase, float cx, float y)
    {
        string words = now.ToString("h:mm", CultureInfo.InvariantCulture);
        if (words != curText)
        {
            prevText = curText;
            curText = words;
            textChangedAt = curText != null && prevText != null ? t : -100;
        }

        double k = Math.Min(1.0, Math.Max(0.0, (t - textChangedAt) / 2.5));
        k = k * k * (3 - 2 * k);
        float tracking = mainFont.Size * 0.05f;
        if (k < 1 && prevText != null)
            DrawTracked(g, prevText, mainFont, Color.FromArgb((int)(150 * (1 - k)), 168, 176, 190), cx, y, tracking);
        DrawTracked(g, curText, mainFont, Color.FromArgb((int)(150 * k), 168, 176, 190), cx, y, tracking);

        string sub = now.ToString("dddd   MMMM d", CultureInfo.GetCultureInfo("en-US"))
                     + "   ·   " + PhaseName(phase);
        DrawTracked(g, sub.ToUpperInvariant(), subFont, Color.FromArgb(105, 120, 130, 145),
            cx, y + mainFont.Size * 1.5f, subFont.Size * 0.32f);
    }

    void DrawTracked(Graphics g, string text, Font font, Color color, float cx, float y, float tracking)
    {
        if (string.IsNullOrEmpty(text) || color.A == 0) return;
        float total = 0;
        float[] widths = new float[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            widths[i] = CharWidth(g, font, text[i]);
            total += widths[i] + (i < text.Length - 1 ? tracking : 0);
        }
        float x = cx - total / 2;
        brush.Color = color;
        for (int i = 0; i < text.Length; i++)
        {
            g.DrawString(text[i].ToString(), font, brush, x, y, typo);
            x += widths[i] + tracking;
        }
    }

    float CharWidth(Graphics g, Font font, char c)
    {
        string key = font.Size + "|" + c;
        float w;
        if (!widthCache.TryGetValue(key, out w))
        {
            w = g.MeasureString(c.ToString(), font, PointF.Empty, typo).Width;
            widthCache[key] = w;
        }
        return w;
    }

    // ---------- Moon ----------

    static double MoonPhase(DateTime now)
    {
        DateTime epoch = new DateTime(2000, 1, 6, 18, 14, 0, DateTimeKind.Utc); // a known new moon
        double days = (now.ToUniversalTime() - epoch).TotalDays;
        double p = (days / 29.530588853) % 1.0;
        return p < 0 ? p + 1 : p;
    }

    static string PhaseName(double p)
    {
        if (p < 0.03 || p > 0.97) return "New Moon";
        if (p < 0.22) return "Waxing Crescent";
        if (p < 0.28) return "First Quarter";
        if (p < 0.47) return "Waxing Gibbous";
        if (p < 0.53) return "Full Moon";
        if (p < 0.72) return "Waning Gibbous";
        if (p < 0.78) return "Last Quarter";
        return "Waning Crescent";
    }

    // Rendered from real NASA LRO data (SVS CGI Moon Kit): an albedo mosaic of the
    // near side plus surface slopes from the LOLA elevation model, so craters and
    // mountains catch the light along the terminator.

    const double TexLonSpan = 200.0;  // textures cover longitude -100..+100
    const double Relief = 3.5;        // slope exaggeration so relief reads at screen size

    class Map
    {
        public int W, H;
        public float[] R, G, B;
        public double Sample(float[] ch, double u, double v)
        {
            u = Math.Max(0, Math.Min(W - 1.001, u - 0.5));
            v = Math.Max(0, Math.Min(H - 1.001, v - 0.5));
            int x = (int)u, y = (int)v;
            double fx = u - x, fy = v - y;
            int i = y * W + x;
            double a = ch[i], b = ch[i + 1], c = ch[i + W], d = ch[i + W + 1];
            return a + (b - a) * fx + (c - a) * fy + (a - b - c + d) * fx * fy;
        }
    }

    static Map LoadMap(string resource, int maxWidth)
    {
        using (System.IO.Stream s = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
        using (Bitmap src = new Bitmap(s))
        {
            int w = Math.Max(16, Math.Min(src.Width, maxWidth));
            int h = Math.Max(16, (int)((long)src.Height * w / src.Width));
            using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    using (ImageAttributes ia = new ImageAttributes())
                    {
                        ia.SetWrapMode(WrapMode.TileFlipXY);
                        g.DrawImage(src, new Rectangle(0, 0, w, h), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
                    }
                }
                BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                byte[] buf = new byte[data.Stride * h];
                Marshal.Copy(data.Scan0, buf, 0, buf.Length);
                bmp.UnlockBits(data);
                Map m = new Map();
                m.W = w; m.H = h;
                m.R = new float[w * h]; m.G = new float[w * h]; m.B = new float[w * h];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int o = y * data.Stride + x * 4, i = y * w + x;
                        m.B[i] = buf[o]; m.G[i] = buf[o + 1]; m.R[i] = buf[o + 2];
                    }
                return m;
            }
        }
    }

    // Slopes are stored as 128 + 127*tanh(slope / 0.15).
    static double DecodeSlope(double v)
    {
        double e = Math.Max(-0.995, Math.Min(0.995, (v - 128) / 127.5));
        return 0.15 * 0.5 * Math.Log((1 + e) / (1 - e));
    }

    static Bitmap BakeMoon(int r, double phase)
    {
        // Size textures so a texel is about half a screen pixel at the disc centre.
        int texWidth = (int)(2.0 * r * TexLonSpan * Math.PI / 180);
        Map albedo = LoadMap("moon_albedo.jpg", texWidth);
        Map slope = LoadMap("moon_slope.png", texWidth);

        int pad = (int)(r * 1.7);
        int size = 2 * (r + pad);
        Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        BitmapData data = bmp.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        byte[] buf = new byte[data.Stride * size];

        double ang = phase * Math.PI * 2;
        double lx = Math.Sin(ang), lz = -Math.Cos(ang);     // sun direction; waxing lights the right side
        double litFraction = (1 - Math.Cos(ang)) / 2;
        const double earthshine = 0.05;
        const double highland = 165.0;                      // typical highland albedo in the map
        double half = size / 2.0;
        double deg = 180 / Math.PI;
        double[] sub = { 0.25, 0.75 };

        for (int py = 0; py < size; py++)
        {
            int row = py * data.Stride;
            for (int px = 0; px < size; px++)
            {
                double dx = (px + 0.5 - half) / r;
                double dy = (py + 0.5 - half) / r;
                double d = Math.Sqrt(dx * dx + dy * dy);

                // Soft halo, scaled by how much of the moon is lit.
                double glowA = d > 0.9 ? 0.11 * litFraction * Math.Exp(-(d - 1) * 2.4) : 0;
                if (d > 0.9 && d < 1) glowA = 0.11 * litFraction;

                double cov = 0, mr = 0, mg = 0, mb = 0;
                if (d < 1 + 1.5 / r)
                {
                    int hits = 0;
                    for (int sj = 0; sj < 2; sj++)
                        for (int si = 0; si < 2; si++)
                        {
                            double sx = (px + sub[si] - half) / r;
                            double sy = -(py + sub[sj] - half) / r;    // y up
                            double d2 = sx * sx + sy * sy;
                            if (d2 >= 1) continue;
                            hits++;
                            double sz = Math.Sqrt(1 - d2);
                            double lat = Math.Asin(sy), lon = Math.Atan2(sx, sz);
                            double u = (lon * deg + TexLonSpan / 2) / TexLonSpan;
                            double v = (90 - lat * deg) / 180;

                            double au = u * albedo.W, av = v * albedo.H;
                            double ar = albedo.Sample(albedo.R, au, av);
                            double ag = albedo.Sample(albedo.G, au, av);
                            double ab = albedo.Sample(albedo.B, au, av);

                            double su = u * slope.W, sv = v * slope.H;
                            double relief = Relief * SmoothStep(0.0, 0.35, sz);   // foreshortened at the limb
                            double se = DecodeSlope(slope.Sample(slope.R, su, sv)) * relief;
                            double sn = DecodeSlope(slope.Sample(slope.G, su, sv)) * relief;

                            // Tilt the sphere normal by the local east/north slopes.
                            double sinLat = Math.Sin(lat), cosLat = Math.Cos(lat);
                            double sinLon = Math.Sin(lon), cosLon = Math.Cos(lon);
                            double nx = sx - se * cosLon + sn * sinLat * sinLon;
                            double ny = sy - sn * cosLat;
                            double nz = sz + se * sinLon + sn * sinLat * cosLon;
                            double nl = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                            nx /= nl; ny /= nl; nz /= nl;

                            // Lommel-Seeliger: the moon's flat, dusty look (no limb darkening at full).
                            double mu0 = nx * lx + nz * lz;
                            double mu = Math.Max(0.08, sz);
                            double sun = mu0 > 0 ? Math.Min(1.4, 2 * mu0 / (mu0 + mu)) : 0;
                            // Soften the geometric terminator a hair so it isn't a hard edge.
                            sun *= SmoothStep(-0.02, 0.06, sx * lx + sz * lz);

                            double lum = (ar + ag + ab) / 3;
                            ar = lum + (ar - lum) * 0.6; ag = lum + (ag - lum) * 0.6; ab = lum + (ab - lum) * 0.6;
                            double k = 255 * MoonBrightness / highland;
                            double es = earthshine * sz;
                            mr += ar * k * (sun + es * 0.85);
                            mg += ag * k * (sun + es * 0.95);
                            mb += ab * k * (sun + es * 1.15);
                        }
                    if (hits > 0) { mr /= hits; mg /= hits; mb /= hits; cov = hits / 4.0; }
                }

                double gA = glowA * (1 - cov);
                double outA = cov + gA;
                byte A = 0, R = 0, G = 0, B = 0;
                if (outA > 0.001)
                {
                    double dith = Hash(px, py + 7777);
                    R = (byte)Clamp((int)((mr * cov + GlowColor.R * gA) / outA + dith));
                    G = (byte)Clamp((int)((mg * cov + GlowColor.G * gA) / outA + dith));
                    B = (byte)Clamp((int)((mb * cov + GlowColor.B * gA) / outA + dith));
                    A = (byte)Clamp((int)(outA * 255 + dith));  // dithered to avoid glow banding
                }
                int o = row + px * 4;
                buf[o] = B; buf[o + 1] = G; buf[o + 2] = R; buf[o + 3] = A;
            }
        }

        Marshal.Copy(buf, 0, data.Scan0, buf.Length);
        bmp.UnlockBits(data);
        return bmp;
    }

    // Near-black vertical gradient, dithered so the dark steps don't band.
    static Bitmap BakeBackground(int w, int h)
    {
        Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
        BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        byte[] buf = new byte[data.Stride * h];
        for (int y = 0; y < h; y++)
        {
            double k = (double)y / Math.Max(1, h - 1);
            double r = 5 - 3.5 * k, g = 7 - 5 * k, b = 13 - 10 * k;
            int row = y * data.Stride;
            for (int x = 0; x < w; x++)
            {
                double n = Hash(x, y);
                int o = row + x * 4;
                buf[o] = (byte)Clamp((int)(b + n));
                buf[o + 1] = (byte)Clamp((int)(g + n));
                buf[o + 2] = (byte)Clamp((int)(r + n));
                buf[o + 3] = 255;
            }
        }
        Marshal.Copy(buf, 0, data.Scan0, buf.Length);
        bmp.UnlockBits(data);
        return bmp;
    }

    static double SmoothStep(double a, double b, double x)
    {
        double t = Math.Max(0, Math.Min(1, (x - a) / (b - a)));
        return t * t * (3 - 2 * t);
    }

    static double Hash(int x, int y)
    {
        unchecked
        {
            int n = x * 374761393 + y * 668265263;
            n = (n ^ (n >> 13)) * 1274126177;
            n ^= n >> 16;
            return (n & 0x7fffffff) / (double)int.MaxValue;
        }
    }

    static int Clamp(int v) { return v < 0 ? 0 : v > 255 ? 255 : v; }
}
