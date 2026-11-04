// Borealis - a quiet, dark screensaver.
// Faint northern lights ripple over a mountain lake at night, with the time
// written in words on the water. Everything is dim and moves slowly.
//
// Screensaver args: /s (run), /p <hwnd> (preview), /c (settings)
// Debug: /shot <file.png> [seconds]  renders one 1920x1080 frame.
//        /bench <file.txt>          writes the average render time per frame.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
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
            MessageBox.Show("Borealis has no settings.\n\nThe lights come and go on their own.",
                "Borealis", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        else if (mode == "/p" && arg2 != null)
        {
            IntPtr parent = new IntPtr(long.Parse(arg2));
            SaverForm form = new SaverForm(parent);
            Application.Run(form);
        }
        else if (mode == "/shot" && arg2 != null)
        {
            double t = args.Length > 2 ? double.Parse(args[2], CultureInfo.InvariantCulture) : 120;
            Scene scene = new Scene(1920, 1080, true, 7);
            using (Bitmap bmp = new Bitmap(1920, 1080))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                scene.Render(g, t, DateTime.Now);
                bmp.Save(arg2, ImageFormat.Png);
            }
        }
        else if (mode == "/bench" && arg2 != null)
        {
            Scene scene = new Scene(1920, 1080, true, 7);
            using (Bitmap bmp = new Bitmap(1920, 1080, PixelFormat.Format32bppPArgb))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                Stopwatch sw = Stopwatch.StartNew();
                for (int i = 0; i < 100; i++) scene.Render(g, 60 + i * 0.04, DateTime.Now);
                System.IO.File.WriteAllText(arg2, (sw.Elapsed.TotalMilliseconds / 100).ToString("0.0") + " ms/frame");
            }
        }
        else
        {
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
        timer.Interval = 50;
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

    readonly bool primary;
    readonly Random rng;
    readonly int noiseSeed;
    readonly Star[] stars;
    readonly Font mainFont, subFont;
    readonly Dictionary<string, float> widthCache = new Dictionary<string, float>();
    readonly SolidBrush brush = new SolidBrush(Color.White);
    readonly StringFormat typo;

    // The sky and lake are computed at low resolution each frame and scaled up;
    // the aurora is soft enough that nothing is lost.
    const int Downscale = 4;
    readonly int lw, lh, horizonL;
    readonly int horizon;
    readonly float[] lowR, lowG, lowB;
    readonly double[] bgR, bgG, bgB;
    readonly int[] ux0, ux1, uwx, uy0, uy1;   // bilinear upscale taps (x weights out of 256)
    readonly float[] uwy;
    readonly int[] dither;
    readonly Bitmap full;

    // Mountain silhouettes and their reflection, baked once (wider than the screen so they can drift).
    readonly byte[] land;
    readonly int landW, landMargin;

    string curText, prevText;
    double textChangedAt = -100;

    const int Curtains = 3;
    const double Peak = 0.34;   // brightest the aurora can get, as a fraction of full white
    static readonly double[] CurtainBase = { 0.64, 0.50, 0.76 };
    static readonly double[] CurtainLen = { 0.50, 0.40, 0.30 };

    public Scene(int w, int h, bool primary, int seed)
    {
        Width = w;
        Height = h;
        this.primary = primary;
        rng = new Random(seed * 7919 + Environment.TickCount);
        noiseSeed = rng.Next(1, 100000);

        horizon = (int)(h * 0.66);
        lw = Math.Max(16, w / Downscale);
        lh = Math.Max(9, h / Downscale);
        horizonL = (int)Math.Round(horizon * (double)lh / h);
        lowR = new float[lw * lh]; lowG = new float[lw * lh]; lowB = new float[lw * lh];
        full = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        ux0 = new int[w]; ux1 = new int[w]; uwx = new int[w];
        uy0 = new int[h]; uy1 = new int[h]; uwy = new float[h];
        float[] wx = new float[w];
        UpscaleTaps(w, lw, ux0, ux1, wx);
        for (int i = 0; i < w; i++) uwx[i] = (int)(wx[i] * 256);
        UpscaleTaps(h, lh, uy0, uy1, uwy);
        dither = new int[64 * 64];
        for (int i = 0; i < dither.Length; i++) dither[i] = (int)(Hash(i & 63, i >> 6) * 256);

        // Night sky: deep navy at the top, a touch lighter toward the horizon.
        bgR = new double[lh]; bgG = new double[lh]; bgB = new double[lh];
        for (int y = 0; y < lh; y++)
        {
            if (y < horizonL)
            {
                double k = Math.Pow((double)y / Math.Max(1, horizonL - 1), 1.6);
                bgR[y] = 3 + 6 * k; bgG[y] = 4 + 8 * k; bgB[y] = 10 + 13 * k;
            }
            else
            {
                bgR[y] = 2; bgG[y] = 3; bgB[y] = 7;
            }
        }

        landMargin = Math.Max(4, w / 40);
        landW = w + 2 * landMargin;
        land = BakeLand(landW, h, horizon);

        int count = Math.Max(8, w * h / 26000);
        stars = new Star[count];
        for (int i = 0; i < count; i++)
        {
            Star s = new Star();
            s.X = (float)rng.NextDouble();
            s.Y = (float)(rng.NextDouble() * rng.NextDouble()) * 0.62f;   // denser toward the top
            double big = rng.NextDouble();
            s.Size = (float)(0.7 + big * big * big * 1.4) * Math.Max(0.5f, h / 1080f);
            s.BaseAlpha = 30 + rng.NextDouble() * rng.NextDouble() * 130;
            s.Speed = 0.15 + rng.NextDouble() * 0.6;
            s.Phase = rng.NextDouble() * Math.PI * 2;
            double tint = rng.NextDouble();
            s.Tint = tint < 0.2 ? Color.FromArgb(255, 226, 196)
                   : tint < 0.5 ? Color.FromArgb(196, 212, 255)
                   : Color.FromArgb(232, 236, 246);
            stars[i] = s;
        }

        // A quiet italic serif for the time, like a line written by the water.
        mainFont = PickFont(new[] { "Sitka Display", "Georgia" }, Math.Max(7f, h * 0.05f), FontStyle.Italic);
        subFont = new Font("Segoe UI", Math.Max(5f, h * 0.0135f), FontStyle.Regular, GraphicsUnit.Pixel);
        typo = (StringFormat)StringFormat.GenericTypographic.Clone();
        typo.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
    }

    public void Render(Graphics g, double t, DateTime now)
    {
        // The land sways a few pixels over many minutes so nothing sits still.
        int dx = (int)Math.Round(landMargin * Math.Sin(t / 530.0)) - landMargin;

        RenderSky(t);
        Compose(dx);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.DrawImageUnscaled(full, 0, 0);
        g.CompositingMode = CompositingMode.SourceOver;

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        DrawStars(g, t, dx);

        if (primary)
        {
            float cx = (float)(Width * (0.5 + 0.12 * Math.Sin(t / 470.0)));
            float cy = (float)(Height * (0.80 + 0.03 * Math.Sin(t / 330.0 + 0.7)));
            DrawText(g, t, now, cx, cy);
        }

        double fade = Math.Min(1.0, t / 4.0);
        if (fade < 1.0)
        {
            brush.Color = Color.FromArgb((int)((1 - fade) * 255), 0, 0, 0);
            g.FillRectangle(brush, 0, 0, Width, Height);
        }
    }

    // ---------- Aurora ----------

    void RenderSky(double t)
    {
        double[] edge = new double[lw * Curtains];
        double[] len = new double[lw * Curtains];
        double[] gain = new double[lw * Curtains];

        // Overall activity swells and fades over a few minutes.
        double activity = 0.35 + 0.65 * SmoothStep(0.25, 0.75, Fbm(t / 140.0, noiseSeed + 900));

        for (int c = 0; c < Curtains; c++)
        {
            int s = noiseSeed + c * 101;
            double life = SmoothStep(0.3, 0.7, Fbm(t / 95.0 + c * 3.7, s + 60));
            for (int x = 0; x < lw; x++)
            {
                double u = (double)x / lh;   // aspect-correct horizontal coordinate
                double bend = Fbm(u * 0.9 + t * 0.010, s) - 0.5;
                double fold = 0.12 * Math.Sin(u * 2.3 + t * 0.017 + c * 2.0);
                edge[c * lw + x] = horizonL * (CurtainBase[c] + 0.55 * bend + fold);

                double ray = Noise(u * 24 + t * 0.045, s + 30);
                ray = 0.45 + 0.55 * ray * ray;
                double ray2 = Noise(u * 61 - t * 0.03, s + 40);
                ray *= 0.8 + 0.2 * ray2;
                len[c * lw + x] = horizonL * CurtainLen[c] * (0.6 + 0.8 * ray);

                double patch = SmoothStep(0.30, 0.70, Fbm(u * 0.7 - t * 0.012, s + 20));
                gain[c * lw + x] = 2.6 * (0.08 + 0.92 * patch) * ray * (0.35 + 0.65 * life) * activity;
            }
        }

        Parallel.For(0, horizonL, delegate(int y)
        {
            for (int x = 0; x < lw; x++)
            {
                double ar = 0, ag = 0, ab = 0;
                for (int c = 0; c < Curtains; c++)
                {
                    int i = c * lw + x;
                    double k = gain[i];
                    if (k < 0.002) continue;
                    double rows = edge[i] - y;           // low-res rows above the lower edge
                    double d = rows / len[i];            // 0 at the lower edge, 1 at the curtain's height
                    if (d > 3.0 || rows < -15) continue;
                    double v, haze, green;
                    if (rows < 0)
                    {
                        v = 0.6 * Exp(rows / 2.5);             // crisp lower edge, continuous so it doesn't alias
                        haze = 0.10 * Exp(d * 1.5);
                        green = 1;
                    }
                    else
                    {
                        double q = Exp(-d * 0.72);
                        green = q * q;                          // green low down, violet at the tops of the rays
                        v = (0.6 + 0.4 * (1 - Exp(-rows / 2.5))) * green * q;
                        haze = 0.10 * green;
                    }
                    v *= k;
                    haze *= k;
                    double violet = 1 - green;
                    ar += v * (0.20 * green + 0.55 * violet * 0.7) + haze * 0.10;
                    ag += v * (1.00 * green + 0.22 * violet * 0.7) + haze * 0.45;
                    ab += v * (0.60 * green + 0.95 * violet * 0.7) + haze * 0.35;
                }
                int o = y * lw + x;
                lowR[o] = (float)(bgR[y] + 255 * Peak * (1 - Exp(-ar)));
                lowG[o] = (float)(bgG[y] + 255 * Peak * (1 - Exp(-ag)));
                lowB[o] = (float)(bgB[y] + 255 * Peak * (1 - Exp(-ab)));
            }
        });

        for (int y = horizonL; y < lh; y++)
        {
            {
                // The lake: a darkened, rippling mirror of the sky.
                double depth = (double)(y - horizonL) / Math.Max(1, lh - horizonL);
                int ys = Math.Max(0, Math.Min(horizonL - 1, 2 * horizonL - y - 1));
                double amp = 0.4 + 2.2 * depth;
                double atten = 0.42 * (1 - 0.55 * depth) * (0.92 + 0.08 * Math.Sin(y * 0.8 - t * 0.6));
                for (int x = 0; x < lw; x++)
                {
                    double xs = x + amp * Math.Sin(y * 0.55 + t * 0.7 + Math.Sin(x * 0.05 + t * 0.2) * 1.5);
                    int xi = Math.Max(0, Math.Min(lw - 1, (int)Math.Round(xs)));
                    int o = ys * lw + xi;
                    int p = y * lw + x;
                    lowR[p] = (float)(bgR[y] + (lowR[o] - bgR[ys] * 0.5) * atten);
                    lowG[p] = (float)(bgG[y] + (lowG[o] - bgG[ys] * 0.5) * atten);
                    lowB[p] = (float)(bgB[y] + (lowB[o] - bgB[ys] * 0.5) * atten);
                }
            }
        }
    }

    static void UpscaleTaps(int n, int ln, int[] i0, int[] i1, float[] wt)
    {
        for (int i = 0; i < n; i++)
        {
            double f = (i + 0.5) * ln / n - 0.5;
            f = Math.Max(0, Math.Min(ln - 1, f));
            int a = (int)f;
            i0[i] = a;
            i1[i] = Math.Min(ln - 1, a + 1);
            wt[i] = (float)(f - a);
        }
    }

    // Scales the low-res sky up to full size, dithers it, and lays the land over it.
    // Runs in horizontal bands across the CPU cores.
    void Compose(int dx)
    {
        BitmapData data = full.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        IntPtr scan0 = data.Scan0;
        int stride = data.Stride;
        int bands = Math.Min(Height, Environment.ProcessorCount * 2);
        Parallel.For(0, bands, delegate(int band)
        {
            ComposeRows(Height * band / bands, Height * (band + 1) / bands, dx, scan0, stride);
        });
        full.UnlockBits(data);
    }

    unsafe void ComposeRows(int y0, int y1, int dx, IntPtr scan0, int stride)
    {
        int w = Width;
        int* pr = stackalloc int[lw];
        int* pg = stackalloc int[lw];
        int* pb = stackalloc int[lw];
        fixed (int* pwx = uwx, pd = dither, px0 = ux0, px1 = ux1)
        fixed (byte* pl = land)
        for (int y = y0; y < y1; y++)
        {
            // Blend the two source rows once (colours in 1/256ths), then interpolate across.
            int r0 = uy0[y] * lw, r1 = uy1[y] * lw;
            float fy = uwy[y];
            for (int i = 0; i < lw; i++)
            {
                pr[i] = (int)((lowR[r0 + i] + (lowR[r1 + i] - lowR[r0 + i]) * fy) * 256);
                pg[i] = (int)((lowG[r0 + i] + (lowG[r1 + i] - lowG[r0 + i]) * fy) * 256);
                pb[i] = (int)((lowB[r0 + i] + (lowB[r1 + i] - lowB[r0 + i]) * fy) * 256);
            }
            int* pdr = pd + ((y & 63) << 6);
            byte* lrow = pl + (y * landW - dx) * 4;
            uint* outp = (uint*)((byte*)scan0 + y * stride);
            for (int x = 0; x < w; x++)
            {
                int a = px0[x], b = px1[x];
                int fx = pwx[x];
                int n = pdr[x & 63];
                int rr = pr[a] + (((pr[b] - pr[a]) * fx) >> 8);
                int gg = pg[a] + (((pg[b] - pg[a]) * fx) >> 8);
                int bb = pb[a] + (((pb[b] - pb[a]) * fx) >> 8);

                byte* l = lrow + x * 4;
                int la = l[3];
                if (la != 0)
                {
                    int k = la + (la >> 7);   // 0..256
                    bb += (((l[0] << 8) - bb) * k) >> 8;
                    gg += (((l[1] << 8) - gg) * k) >> 8;
                    rr += (((l[2] << 8) - rr) * k) >> 8;
                }
                int ir = (rr + n) >> 8, ig = (gg + n) >> 8, ib = (bb + n) >> 8;
                if (ir > 255) ir = 255; if (ig > 255) ig = 255; if (ib > 255) ib = 255;
                if (ir < 0) ir = 0; if (ig < 0) ig = 0; if (ib < 0) ib = 0;
                outp[x] = 0xFF000000u | (uint)((ir << 16) | (ig << 8) | ib);
            }
        }
    }

    void DrawStars(Graphics g, double t, int dx)
    {
        float drift = (float)(t * 0.15);
        foreach (Star s in stars)
        {
            float x = (s.X * Width + drift) % Width;
            float y = s.Y * horizon;
            int lx = (int)x - dx, ly = (int)y;
            if (lx >= 0 && lx < landW && ly < Height && land[(ly * landW + lx) * 4 + 3] != 0) continue;   // behind a hill
            double a = s.BaseAlpha * (0.55 + 0.45 * Math.Sin(t * s.Speed + s.Phase));
            brush.Color = Color.FromArgb(Clamp((int)a), s.Tint);
            g.FillEllipse(brush, x - s.Size / 2, y - s.Size / 2, s.Size, s.Size);
        }
    }

    // ---------- Land ----------

    byte[] BakeLand(int w, int h, int hy)
    {
        double[] far = new double[w], near = new double[w];
        double scale = h;
        for (int x = 0; x < w; x++)
        {
            double u = x / scale;
            double ux = (double)x / w;
            double farEnv = 0.45 + 0.55 * Fbm(u * 0.6, noiseSeed + 500);
            far[x] = hy - h * (0.012 + 0.13 * farEnv * Ridged(u * 2.2, noiseSeed + 510));
            // Near hills frame the sides and leave the middle of the lake open.
            double nearEnv = SmoothStep(0.36, 0.02, ux) + SmoothStep(0.64, 0.98, ux);
            near[x] = hy - h * (0.004 + 0.17 * nearEnv * Ridged(u * 3.1, noiseSeed + 520));
        }

        byte[] buf = new byte[w * 4 * h];
        int shore = Math.Max(1, h / 540);

        for (int x = 0; x < w; x++)
        {
            int top = (int)Math.Floor(Math.Min(far[x], near[x]));
            int bottom = Math.Min(h, hy + (hy - top) + 1);
            for (int y = Math.Max(0, top); y < bottom; y++)
            {
                double r, gg, b, a;
                if (y < hy)
                {
                    double cn = Coverage(y, near[x]);
                    double cf = Coverage(y, far[x]);
                    // Far ridge picks up a little haze; near hills are nearly black.
                    double k = Math.Max(0, (y - far[x]) / (h * 0.15));
                    double fr = 7 + 2 * k, fg = 9 + 3 * k, fb = 16 + 4 * k;
                    r = 3 * cn + fr * (1 - cn);
                    gg = 4 * cn + fg * (1 - cn);
                    b = 8 * cn + fb * (1 - cn);
                    a = Math.Max(cn, cf);
                }
                else
                {
                    // Reflection, slightly softened and darker.
                    double my = hy - (y - hy) * 0.92 - 0.5;
                    double cn = Coverage(my, near[x]);
                    double cf = Coverage(my, far[x]);
                    r = 2 * cn + 4 * (1 - cn);
                    gg = 3 * cn + 5 * (1 - cn);
                    b = 6 * cn + 10 * (1 - cn);
                    a = Math.Max(cn, cf) * 0.97;
                    if (y - hy < shore) a = 1;
                }
                if (a <= 0) continue;
                double n = Hash(x, y + 3131) - 0.5;
                int o = (y * w + x) * 4;
                buf[o] = (byte)Clamp((int)(b + n + 0.5));
                buf[o + 1] = (byte)Clamp((int)(gg + n + 0.5));
                buf[o + 2] = (byte)Clamp((int)(r + n + 0.5));
                buf[o + 3] = (byte)Clamp((int)(a * 255 + 0.5));
            }
        }

        return buf;
    }

    // How much of pixel row y lies below a ridge line at height edge.
    static double Coverage(double y, double edge)
    {
        return Math.Max(0, Math.Min(1, y + 1 - edge));
    }

    // ---------- Text ----------

    void DrawText(Graphics g, double t, DateTime now, float cx, float y)
    {
        string words = TimeInWords(now);
        if (words != curText)
        {
            prevText = curText;
            curText = words;
            textChangedAt = curText != null && prevText != null ? t : -100;
        }

        double k = Math.Min(1.0, Math.Max(0.0, (t - textChangedAt) / 2.5));
        k = k * k * (3 - 2 * k);
        // Pale, faintly aurora-tinted grey.
        if (k < 1 && prevText != null)
            DrawCentered(g, prevText, mainFont, Color.FromArgb((int)(150 * (1 - k)), 178, 202, 196), cx, y);
        DrawCentered(g, curText, mainFont, Color.FromArgb((int)(150 * k), 178, 202, 196), cx, y);

        string sub = now.ToString("dddd  ·  MMMM d", CultureInfo.GetCultureInfo("en-US"));
        DrawTracked(g, sub.ToUpperInvariant(), subFont, Color.FromArgb(100, 128, 150, 150),
            cx, y + mainFont.Size * 1.55f, subFont.Size * 0.35f);
    }

    void DrawCentered(Graphics g, string text, Font font, Color color, float cx, float y)
    {
        if (string.IsNullOrEmpty(text) || color.A == 0) return;
        float w;
        string key = font.Size + "|" + text;
        if (!widthCache.TryGetValue(key, out w))
        {
            w = g.MeasureString(text, font, PointF.Empty, typo).Width;
            widthCache[key] = w;
        }
        brush.Color = color;
        g.DrawString(text, font, brush, cx - w / 2, y, typo);
    }

    static Font PickFont(string[] families, float size, FontStyle style)
    {
        foreach (string name in families)
        {
            Font f = new Font(name, size, style, GraphicsUnit.Pixel);
            if (f.Name == name) return f;
            f.Dispose();
        }
        return new Font(FontFamily.GenericSerif, size, style, GraphicsUnit.Pixel);
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

    static readonly string[] Hours =
        { "twelve", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven" };
    static readonly string[] Ones =
        { "", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven",
          "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen" };

    static string Number(int n)
    {
        if (n < 20) return Ones[n];
        return n % 10 == 0 ? "twenty" : "twenty-" + Ones[n % 10];
    }

    static string TimeInWords(DateTime now)
    {
        int h = now.Hour, m = now.Minute;
        if (m == 0)
        {
            if (h == 0) return "midnight";
            if (h == 12) return "noon";
            return Hours[h % 12] + " o'clock";
        }
        string hour = Hours[h % 12];
        string next = Hours[(h + 1) % 12];
        if (m == 15) return "quarter past " + hour;
        if (m == 30) return "half past " + hour;
        if (m == 45) return "quarter to " + next;
        if (m < 30) return Number(m) + (m == 1 ? " minute" : "") + " past " + hour;
        int left = 60 - m;
        return Number(left) + (left == 1 ? " minute" : "") + " to " + next;
    }

    // e^x for x <= 0, from a table; plenty accurate for 8-bit colour.
    const int ExpSteps = 128;
    static readonly double[] ExpTable = BuildExpTable();

    static double[] BuildExpTable()
    {
        double[] e = new double[24 * ExpSteps + 2];
        for (int i = 0; i < e.Length; i++) e[i] = Math.Exp(-(double)i / ExpSteps);
        return e;
    }

    static double Exp(double x)
    {
        double f = -x * ExpSteps;
        if (f <= 0) return 1;
        if (f >= 24 * ExpSteps) return 0;
        int i = (int)f;
        return ExpTable[i] + (ExpTable[i + 1] - ExpTable[i]) * (f - i);
    }

    // ---------- Noise ----------

    static double Noise(double x, int seed)
    {
        double f = Math.Floor(x);
        int i = (int)f;
        double u = x - f;
        u = u * u * (3 - 2 * u);
        double a = Hash(i, seed), b = Hash(i + 1, seed);
        return a + (b - a) * u;
    }

    static double Fbm(double x, int seed)
    {
        return (Noise(x, seed) * 0.5 + Noise(x * 2.03 + 11.3, seed + 1) * 0.25
              + Noise(x * 4.11 + 3.7, seed + 2) * 0.125) / 0.875;
    }

    static double Ridged(double x, int seed)
    {
        double sum = 0, amp = 0.5, norm = 0;
        for (int o = 0; o < 5; o++)
        {
            double n = 1 - Math.Abs(2 * Noise(x, seed + o) - 1);
            sum += n * n * amp;
            norm += amp;
            x = x * 2.07 + 5.1;
            amp *= 0.5;
        }
        return sum / norm;
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
