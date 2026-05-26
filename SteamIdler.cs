// SteamIdler.cs  –  Steam Card Idler  (professional UI edition)
//
// Three ways to connect:
//   A) Browser Cookie Login  – no API key, shows games with card drops
//   B) Steam Web API Key     – shows full library (SteamID auto-detected)
//   C) Manual / Quick Idle   – type any AppID, instant idle, no login needed
//
// Build:  run build.bat

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using HtmlAgilityPack;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;

namespace SteamIdler
{
    // =========================================================================
    //  Design tokens
    // =========================================================================

    static class Pal
    {
        // Backgrounds
        public static readonly Color BgWindow   = Color.FromArgb(15,  22,  31);   // darkest
        public static readonly Color BgBar      = Color.FromArgb(20,  27,  38);   // header/footer
        public static readonly Color BgCard     = Color.FromArgb(26,  37,  52);   // card normal
        public static readonly Color BgCardHov  = Color.FromArgb(33,  47,  65);   // card hover
        public static readonly Color BgCardRun  = Color.FromArgb(20,  45,  38);   // card running (green tint)
        public static readonly Color BgPanel    = Color.FromArgb(18,  26,  36);   // inner panels
        public static readonly Color BgInput    = Color.FromArgb(24,  34,  48);   // text inputs
        public static readonly Color BgQuick    = Color.FromArgb(13,  19,  28);   // quick-idle strip

        // Text
        public static readonly Color TxtPri     = Color.FromArgb(198, 214, 226);  // primary text
        public static readonly Color TxtSec     = Color.FromArgb(110, 140, 160);  // secondary/muted
        public static readonly Color TxtDim     = Color.FromArgb(65,  90,  110);  // very muted

        // Accents
        public static readonly Color Accent     = Color.FromArgb(100, 195, 245);  // Steam blue
        public static readonly Color Green      = Color.FromArgb(80,  210, 150);  // running/idling
        public static readonly Color Orange     = Color.FromArgb(245, 166,  35);  // drops badge
        public static readonly Color Red        = Color.FromArgb(210,  65,   50); // danger/stop
        public static readonly Color Separator  = Color.FromArgb(30,  42,  58);   // thin lines

        // Buttons
        public static readonly Color BtnBlue    = Color.FromArgb(32,  90, 150);
        public static readonly Color BtnGreen   = Color.FromArgb(40, 140,  90);
        public static readonly Color BtnRed     = Color.FromArgb(150, 40,  35);
        public static readonly Color BtnGray    = Color.FromArgb(48,  62,  78);
    }

    static class Fnt
    {
        public static readonly Font Title     = new Font("Segoe UI", 14f,   FontStyle.Bold);
        public static readonly Font CardName  = new Font("Segoe UI", 10.5f, FontStyle.Bold);
        public static readonly Font CardSub   = new Font("Segoe UI", 8.5f);
        public static readonly Font Badge     = new Font("Segoe UI", 7.5f,  FontStyle.Bold);
        public static readonly Font Stat      = new Font("Segoe UI", 8.5f);
        public static readonly Font Btn       = new Font("Segoe UI", 9f);
        public static readonly Font BtnSm     = new Font("Segoe UI", 8.5f);
        public static readonly Font Label     = new Font("Segoe UI", 9f);
        public static readonly Font LabelBold = new Font("Segoe UI", 9f,    FontStyle.Bold);
        public static readonly Font Input     = new Font("Segoe UI", 9.5f);
    }

    // Shared animation clock (~20 fps)
    static class Anim
    {
        public static double Phase;        // 0..2π, cycles every ~3 s
        public static event Action Tick;

        static System.Windows.Forms.Timer _t;

        static Anim()
        {
            _t          = new System.Windows.Forms.Timer();
            _t.Interval = 50;
            _t.Tick    += (s, e) =>
            {
                Phase = (Phase + 0.12) % (Math.PI * 2);
                if (Tick != null) Tick();
            };
            _t.Start();
        }
    }

    // =========================================================================
    //  GDI+ helpers
    // =========================================================================

    static class Draw
    {
        public static GraphicsPath RoundedRect(RectangleF r, float rad)
        {
            float d = rad * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X,           r.Y,            d, d, 180, 90);
            p.AddArc(r.Right - d,   r.Y,            d, d, 270, 90);
            p.AddArc(r.Right - d,   r.Bottom - d,   d, d, 0,   90);
            p.AddArc(r.X,           r.Bottom - d,   d, d, 90,  90);
            p.CloseFigure();
            return p;
        }

        public static void SetHQ(Graphics g)
        {
            g.SmoothingMode     = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode   = PixelOffsetMode.HighQuality;
        }

        public static Color Blend(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)(a.A + (b.A - a.A) * t),
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        public static Color Lighten(Color c, float f)
        {
            return Color.FromArgb(c.A,
                Math.Min(255, (int)(c.R + (255 - c.R) * f)),
                Math.Min(255, (int)(c.G + (255 - c.G) * f)),
                Math.Min(255, (int)(c.B + (255 - c.B) * f)));
        }

        public static Color Darken(Color c, float f)
        {
            return Color.FromArgb(c.A,
                (int)(c.R * (1 - f)),
                (int)(c.G * (1 - f)),
                (int)(c.B * (1 - f)));
        }
    }

    // =========================================================================
    //  SystemHelper – prevent system sleep while idling
    // =========================================================================

    static class SystemHelper
    {
        const uint ES_CONTINUOUS       = 0x80000000u;
        const uint ES_SYSTEM_REQUIRED  = 0x00000001u;
        const uint ES_DISPLAY_REQUIRED = 0x00000002u;

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern uint SetThreadExecutionState(uint esFlags);

        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        public static void PreventSleep()
        {
            SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);
        }

        public static void AllowSleep()
        {
            SetThreadExecutionState(ES_CONTINUOUS);
        }

        /// <summary>
        /// Tells Windows the process handles DPI scaling itself, so the OS does not
        /// bitmap-stretch the window on high-DPI displays (which causes blurry text).
        /// Safe to call on Vista+ and ignored on older systems.
        /// </summary>
        public static void EnableDpiAwareness()
        {
            try { SetProcessDPIAware(); } catch { /* not available on very old OS */ }
        }
    }

    // =========================================================================
    //  Dark menu renderer + color table for ContextMenuStrip
    // =========================================================================

    class DarkColorTable : ProfessionalColorTable
    {
        public override Color MenuItemSelected               { get { return Pal.BgCardHov; } }
        public override Color MenuItemSelectedGradientBegin  { get { return Pal.BgCardHov; } }
        public override Color MenuItemSelectedGradientEnd    { get { return Pal.BgCardHov; } }
        public override Color MenuItemPressedGradientBegin   { get { return Pal.BgCard;    } }
        public override Color MenuItemPressedGradientEnd     { get { return Pal.BgCard;    } }
        public override Color MenuItemBorder                 { get { return Pal.Separator; } }
        public override Color ToolStripDropDownBackground    { get { return Pal.BgBar;     } }
        public override Color ImageMarginGradientBegin       { get { return Pal.BgPanel;   } }
        public override Color ImageMarginGradientMiddle      { get { return Pal.BgPanel;   } }
        public override Color ImageMarginGradientEnd         { get { return Pal.BgPanel;   } }
        public override Color SeparatorDark                  { get { return Pal.Separator; } }
        public override Color SeparatorLight                 { get { return Pal.Separator; } }
    }

    class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        // Shared singleton – avoids allocating a new renderer on every right-click
        public static readonly DarkMenuRenderer Instance = new DarkMenuRenderer();

        public DarkMenuRenderer() : base(new DarkColorTable()) { }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Pal.TxtPri : Pal.TxtDim;
            base.OnRenderItemText(e);
        }
    }

    // =========================================================================
    //  TimedWebClient – WebClient with a hard request timeout
    // =========================================================================

    class TimedWebClient : WebClient
    {
        /// <summary>Request timeout in milliseconds (default 30 s).</summary>
        public int TimeoutMs = 30000;

        protected override WebRequest GetWebRequest(Uri address)
        {
            WebRequest req = base.GetWebRequest(address);
            if (req != null) req.Timeout = TimeoutMs;
            return req;
        }
    }

    // =========================================================================
    //  SteamButton – fully custom-drawn button with hover/press states
    // =========================================================================

    class SteamButton : Control, IButtonControl
    {
        public Color        BaseColor    = Pal.BtnGray;
        public DialogResult DialogResult { get; set; }
        bool _hover, _press;

        public void NotifyDefault(bool value) { }
        public void PerformClick() { if (Enabled) OnClick(EventArgs.Empty); }

        public SteamButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height       = 32;
            Cursor       = Cursors.Hand;
            Font         = Fnt.Btn;
            DialogResult = DialogResult.None;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g  = e.Graphics;
            Draw.SetHQ(g);
            var rc = new RectangleF(0, 0, Width, Height);

            Color bg = !Enabled  ? Draw.Darken(BaseColor, 0.40f)
                     : _press    ? Draw.Darken(BaseColor, 0.15f)
                     : _hover    ? Draw.Lighten(BaseColor, 0.18f)
                     : BaseColor;

            using (var path = Draw.RoundedRect(rc, 3))
            using (var br   = new SolidBrush(bg))
                g.FillPath(br, path);

            // Top highlight only when active (not disabled, not pressed)
            if (Enabled && !_press)
                using (var p = new Pen(Color.FromArgb(40, 255, 255, 255), 1))
                    g.DrawLine(p, 2, 1, Width - 3, 1);

            Color txtColor = Enabled ? Pal.TxtPri : Pal.TxtDim;
            using (var sf = new StringFormat { Alignment = StringAlignment.Center,
                                               LineAlignment = StringAlignment.Center })
            using (var br = new SolidBrush(txtColor))
                g.DrawString(Text, Font, br, rc, sf);
        }

        protected override void OnMouseEnter(EventArgs e)  { _hover = true;  Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e)  { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { _press = true; Invalidate(); }
            base.OnMouseDown(e);
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            _press = false;
            Invalidate();
            if (e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location)
                && DialogResult != DialogResult.None)
            {
                var form = FindForm();
                if (form != null) form.DialogResult = DialogResult;
            }
            base.OnMouseUp(e);
        }
    }

    // =========================================================================
    //  GameCard – fully owner-drawn game row
    // =========================================================================

    class GameCard : Control
    {
        public  int    AppId;
        public  string GameName;
        public  int    PlaytimeMin;
        public  int    RemainingDrops;
        public  bool   Checked   { get { return _checked; } }
        public  bool   IsRunning { get { return _running;  } }
        public  Action StopAction;
        public  Action CheckedChanged;   // fired when the checkbox toggles

        bool     _checked, _hover, _running;
        Image    _image;
        bool     _imageFailed;
        double   _pulse;
        bool     _animSubscribed;
        DateTime _startTime;
        string   _elapsedStr;

        static readonly Rectangle CHK = new Rectangle(12, 35, 18, 18);
        const int IMG_X = 44, IMG_Y = 9, IMG_W = 154, IMG_H = 72;
        const int TX    = 210;    // text left edge

        public GameCard(int appId, string name, int playMin, int drops)
        {
            AppId          = appId;
            GameName       = name;
            PlaytimeMin    = playMin;
            RemainingDrops = drops;

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            Height    = 90;
            Dock      = DockStyle.Top;
            Cursor    = Cursors.Default;
            BackColor = Pal.BgCard;
        }

        public void SetChecked(bool v)
        {
            if (_checked == v) return;
            _checked = v;
            Invalidate();
            if (CheckedChanged != null) CheckedChanged();
        }
        public void ToggleCheck()
        {
            _checked = !_checked;
            Invalidate();
            if (CheckedChanged != null) CheckedChanged();
        }

        public void SetImage(Image img)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { Invoke(new Action<Image>(SetImage), img); return; }
            Image old = _image;
            _image = img;
            _imageFailed = false;
            if (old != null) old.Dispose();
            Invalidate();
        }

        public void MarkImageFailed()
        {
            if (IsDisposed) return;
            if (InvokeRequired) { Invoke(new Action(MarkImageFailed)); return; }
            _imageFailed = true;
            Invalidate();
        }

        public void SetRunning(bool on)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { Invoke(new Action<bool>(SetRunning), on); return; }
            _running = on;
            if (on)
            {
                _startTime  = DateTime.Now;
                _elapsedStr = "0:00";
            }
            if (on && !_animSubscribed)
            {
                Anim.Tick   += OnAnimTick;
                _animSubscribed = true;
            }
            else if (!on && _animSubscribed)
            {
                Anim.Tick   -= OnAnimTick;
                _animSubscribed = false;
            }
            Invalidate();
        }

        void OnAnimTick()
        {
            if (IsDisposed || !_running) return;
            _pulse = Anim.Phase;
            var span = DateTime.Now - _startTime;
            _elapsedStr = span.TotalHours >= 1
                ? string.Format("{0}h {1:D2}m", (int)span.TotalHours, span.Minutes)
                : string.Format("{0}:{1:D2}", (int)span.TotalMinutes, span.Seconds);
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true;  Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                ToggleCheck();
            }
            else if (e.Button == MouseButtons.Right)
            {
                var menu       = new ContextMenuStrip();
                menu.Renderer  = DarkMenuRenderer.Instance;
                menu.BackColor = Pal.BgBar;
                menu.ForeColor = Pal.TxtPri;
                menu.Font      = Fnt.Label;

                int aid = AppId;
                var storeItem = new ToolStripMenuItem("Open Steam Store Page");
                storeItem.Click += (ms, me) =>
                {
                    try { Process.Start("https://store.steampowered.com/app/" + aid.ToString()); }
                    catch { }
                };
                menu.Items.Add(storeItem);

                if (_running && StopAction != null)
                {
                    menu.Items.Add(new ToolStripSeparator());
                    var stopItem       = new ToolStripMenuItem("Stop Idling");
                    stopItem.ForeColor = Pal.Red;
                    Action sa = StopAction;
                    stopItem.Click    += (ms, me) => sa();
                    menu.Items.Add(stopItem);
                }

                menu.Show(this, e.Location);
            }
            base.OnMouseClick(e);
        }

        // Double-click opens the Steam store page
        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
                try { Process.Start("https://store.steampowered.com/app/" + AppId.ToString()); }
                catch { }
            base.OnMouseDoubleClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Draw.SetHQ(g);

            // ── Background ──────────────────────────────────────────────────
            Color bgC = _running ? Pal.BgCardRun
                      : _hover   ? Pal.BgCardHov
                      : Pal.BgCard;
            if (_checked && !_running) bgC = Draw.Blend(bgC, Pal.Accent, 0.06f);
            g.Clear(bgC);

            // ── Left accent bar (pulsing green = running, solid blue = checked)
            if (_running)
            {
                float pulse = (float)(0.55 + 0.45 * Math.Sin(_pulse));
                using (var br = new SolidBrush(Color.FromArgb((int)(255 * pulse), Pal.Green)))
                    g.FillRectangle(br, 0, 0, 4, Height);
            }
            else if (_checked)
            {
                using (var br = new SolidBrush(Pal.Accent))
                    g.FillRectangle(br, 0, 0, 3, Height);
            }

            // ── Bottom separator ────────────────────────────────────────────
            using (var p = new Pen(Pal.Separator, 1))
                g.DrawLine(p, 0, Height - 1, Width, Height - 1);

            // ── Checkbox ────────────────────────────────────────────────────
            var chkF = new RectangleF(CHK.X, CHK.Y, CHK.Width, CHK.Height);
            if (_checked)
            {
                using (var path = Draw.RoundedRect(chkF, 3))
                using (var br   = new SolidBrush(Pal.Accent))
                    g.FillPath(br, path);

                var pts = new PointF[] {
                    new PointF(CHK.X + 3,                   CHK.Y + CHK.Height / 2f),
                    new PointF(CHK.X + CHK.Width / 2f - 1,  CHK.Y + CHK.Height - 4f),
                    new PointF(CHK.X + CHK.Width - 3,        CHK.Y + 4f)
                };
                using (var pen = new Pen(Color.White, 2f)
                    { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    g.DrawLines(pen, pts);
            }
            else
            {
                using (var path = Draw.RoundedRect(chkF, 3))
                using (var pen  = new Pen(_hover ? Pal.TxtSec : Pal.TxtDim, 1.5f))
                    g.DrawPath(pen, path);
            }

            // ── Game image ───────────────────────────────────────────────────
            var imgRect = new Rectangle(IMG_X, IMG_Y, IMG_W, IMG_H);
            if (_image != null)
            {
                try { g.DrawImage(_image, imgRect); } catch { /* image disposed by race — ignore */ }
            }
            else
            {
                using (var br = new SolidBrush(Color.FromArgb(15, 22, 32)))
                    g.FillRectangle(br, imgRect);
                using (var sf = new StringFormat { Alignment = StringAlignment.Center,
                                                   LineAlignment = StringAlignment.Center })
                using (var br = new SolidBrush(Pal.TxtDim))
                    g.DrawString(_imageFailed ? "no image" : "loading...",
                                 Fnt.CardSub, br, (RectangleF)imgRect, sf);
            }
            using (var pen = new Pen(Color.FromArgb(35, 255, 255, 255), 1))
                g.DrawRectangle(pen, imgRect);

            // ── Game name ────────────────────────────────────────────────────
            int rightEdge = Width - 16;
            var nameRect  = new RectangleF(TX, 14, rightEdge - TX - (_running ? 84 : 0), 24);
            using (var nsf = new StringFormat { Trimming = StringTrimming.EllipsisCharacter,
                                                FormatFlags = StringFormatFlags.NoWrap })
            using (var br  = new SolidBrush(Pal.TxtPri))
                g.DrawString(GameName, Fnt.CardName, br, nameRect, nsf);

            // ── Playtime ─────────────────────────────────────────────────────
            int ph = PlaytimeMin / 60, pm = PlaytimeMin % 60;
            string timeStr = ph > 0
                ? string.Format("{0}h {1}m played", ph, pm)
                : string.Format("{0}m played", pm);
            using (var br = new SolidBrush(Pal.TxtSec))
                g.DrawString(timeStr, Fnt.CardSub, br, TX, 44);

            // ── Card-drop badge ───────────────────────────────────────────────
            if (RemainingDrops > 0)
            {
                string dtxt       = RemainingDrops == 1 ? "1 drop left"
                                  : RemainingDrops.ToString() + " drops left";
                var   badgeRect   = new RectangleF(TX, 62, 100, 19);
                using (var path   = Draw.RoundedRect(badgeRect, 4))
                using (var fill   = new SolidBrush(Color.FromArgb(200, 140, 35)))
                    g.FillPath(fill, path);
                using (var sf     = new StringFormat { Alignment = StringAlignment.Center,
                                                       LineAlignment = StringAlignment.Center })
                using (var br     = new SolidBrush(Color.White))
                    g.DrawString(dtxt, Fnt.Badge, br, badgeRect, sf);
            }

            // ── Running indicator ─────────────────────────────────────────────
            if (_running)
            {
                double a    = 0.55 + 0.45 * Math.Sin(_pulse);
                Color  dotC = Color.FromArgb((int)(255 * a), Pal.Green);

                using (var br = new SolidBrush(dotC))
                    g.FillEllipse(br, Width - 100, 18, 10, 10);

                using (var br = new SolidBrush(Pal.Green))
                    g.DrawString("IDLING", Fnt.Badge, br, Width - 88, 16);

                using (var br = new SolidBrush(Pal.TxtSec))
                    g.DrawString(_elapsedStr ?? "0:00", Fnt.CardSub, br, Width - 88, 33);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_animSubscribed) { Anim.Tick -= OnAnimTick; _animSubscribed = false; }
                if (_image != null)  { _image.Dispose(); _image = null; }
            }
            base.Dispose(disposing);
        }
    }

    // =========================================================================
    //  AppConfig
    // =========================================================================

    static class AppConfig
    {
        // ── Version ──────────────────────────────────────────────────────────
        public const string AppVersion  = "1.0.0";

        // ── Auth mode identifiers (single source of truth) ────────────────
        public const string ModeCookies = "cookies";
        public const string ModeApiKey  = "apikey";
        public const string ModeManual  = "manual";

        static readonly string _dir;
        public static readonly string CacheDir;
        public static readonly string RunDir;
        static readonly string _cfgFile;

        public static string AuthMode    = ModeCookies;
        public static string ApiKey      = "";
        public static string SteamId     = "";
        public static string SessionId   = "";
        public static string LoginSecure = "";

        static AppConfig()
        {
            _dir     = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SteamCardIdler");
            CacheDir = Path.Combine(_dir, "imgcache");
            RunDir   = Path.Combine(_dir, "running");
            _cfgFile = Path.Combine(_dir, "config.txt");
            Directory.CreateDirectory(CacheDir);
            Directory.CreateDirectory(RunDir);
            Load();
        }

        static void Load()
        {
            if (!File.Exists(_cfgFile)) return;
            foreach (var line in File.ReadAllLines(_cfgFile))
            {
                var p = line.Split(new char[] { '=' }, 2);
                if (p.Length != 2) continue;
                switch (p[0])
                {
                    case "AuthMode":    AuthMode    = p[1]; break;
                    case "ApiKey":      ApiKey      = p[1]; break;
                    case "SteamId":     SteamId     = p[1]; break;
                    case "SessionId":   SessionId   = p[1]; break;
                    case "LoginSecure": LoginSecure = p[1]; break;
                }
            }
        }

        public static void Save()
        {
            File.WriteAllLines(_cfgFile, new string[] {
                "AuthMode="    + AuthMode,
                "ApiKey="      + ApiKey,
                "SteamId="     + SteamId,
                "SessionId="   + SessionId,
                "LoginSecure=" + LoginSecure,
            });
        }

        public static bool HasCookies() { return !string.IsNullOrEmpty(SessionId) && !string.IsNullOrEmpty(LoginSecure); }
        public static bool HasApiKey()  { return !string.IsNullOrEmpty(ApiKey)    && !string.IsNullOrEmpty(SteamId); }
    }

    // =========================================================================
    //  SteamHelper – registry SteamID detection
    // =========================================================================

    static class SteamHelper
    {
        public static string DetectSteamId64()
        {
            try
            {
                var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
                if (key == null) return null;
                object val = key.GetValue("ActiveUser");
                if (val == null) return null;
                uint accountId = Convert.ToUInt32(val);
                if (accountId == 0) return null;
                return ((ulong)accountId + 76561197960265728UL).ToString();
            }
            catch { return null; }
        }
    }

    // =========================================================================
    //  BadgeScraper – cookie-based card-drop discovery
    // =========================================================================

    class BadgeGame
    {
        public int    AppId;
        public string Name;
        public int    RemainingDrops;
        public int    PlaytimeMinutes;
    }

    static class BadgeScraper
    {
        // onProgress(pageNumber) is called after each page is scraped (may be null).
        // Network exceptions are intentionally NOT caught here — they propagate to
        // FetchBadgeMode so the user sees a proper error message.
        public static async Task<List<BadgeGame>> GetGamesWithDropsAsync(
            string sessionId, string loginSecure, Action<int> onProgress = null)
        {
            var results = new List<BadgeGame>();
            for (int page = 1; page <= 50; page++)
            {
                string url  = "https://steamcommunity.com/my/badges/?l=english&p=" + page.ToString();
                string html;
                using (var wc = MakeClient(sessionId, loginSecure))
                    html = await wc.DownloadStringTaskAsync(url);

                results.AddRange(ParsePage(html));
                if (onProgress != null) onProgress(page);
                if (!html.Contains("pagebtn_next")) break;  // no more pages
            }
            return results;
        }

        static List<BadgeGame> ParsePage(string html)
        {
            var list = new List<BadgeGame>();
            var doc  = new HtmlAgilityPack.HtmlDocument();
            doc.LoadHtml(html);
            var rows = doc.DocumentNode.SelectNodes("//div[contains(@class,'badge_row') and @data-appid]");
            if (rows == null) return list;

            foreach (var row in rows)
            {
                int appId;
                if (!int.TryParse(row.GetAttributeValue("data-appid", ""), out appId) || appId == 0) continue;

                var dn = row.SelectSingleNode(".//span[contains(@class,'progress_info_bold')]");
                if (dn == null) continue;
                string dt = dn.InnerText.Trim();
                if (!dt.Contains("drop")) continue;

                int drops;
                var nm = Regex.Match(dt, @"\d+");
                if (!int.TryParse(nm.Value, out drops) || drops <= 0) continue;

                var nn   = row.SelectSingleNode(".//div[contains(@class,'badge_title')]");
                string name = nn != null
                    ? HtmlEntity.DeEntitize(nn.InnerText.Trim().Split('\n')[0].Trim())
                    : "App " + appId.ToString();

                int mins = 0;
                var tn = row.SelectSingleNode(".//*[contains(@class,'badge_title_playtime_hours')]");
                if (tn != null)
                {
                    double hh;
                    double.TryParse(Regex.Match(tn.InnerText, @"[\d.]+").Value, out hh);
                    mins = (int)(hh * 60);
                }

                list.Add(new BadgeGame { AppId = appId, Name = name, RemainingDrops = drops, PlaytimeMinutes = mins });
            }
            return list;
        }

        static TimedWebClient MakeClient(string sid, string sec)
        {
            var wc = new TimedWebClient();
            wc.Headers[HttpRequestHeader.Cookie]    = "sessionid=" + sid + "; steamLoginSecure=" + sec;
            wc.Headers[HttpRequestHeader.UserAgent] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/120.0";
            return wc;
        }
    }

    // =========================================================================
    //  CookieLoginDialog – embedded browser login
    // =========================================================================

    class CookieLoginDialog : Form
    {
        WebBrowser  _browser;
        Label       _statusLbl;
        SteamButton _doneBtn;

        public string CapturedSessionId   { get; private set; }
        public string CapturedLoginSecure { get; private set; }
        public bool   LoggedIn            { get { return !string.IsNullOrEmpty(CapturedSessionId); } }

        [DllImport("wininet.dll", SetLastError = true, CharSet = CharSet.Auto)]
        static extern bool InternetGetCookieEx(string url, string name,
            StringBuilder data, ref int size, int flags, IntPtr reserved);
        const int INTERNET_COOKIE_HTTPONLY = 0x2000;

        public CookieLoginDialog()
        {
            Text          = "Log in to Steam  –  Steam Card Idler";
            Size          = new Size(960, 720);
            StartPosition = FormStartPosition.CenterParent;
            BackColor     = Pal.BgWindow;

            var bar       = new Panel();
            bar.Dock      = DockStyle.Top;
            bar.Height    = 50;
            bar.BackColor = Pal.BgBar;

            _statusLbl           = new Label();
            _statusLbl.Text      = "Log in below. The window closes automatically when detected.";
            _statusLbl.Font      = Fnt.Label;
            _statusLbl.ForeColor = Pal.TxtSec;
            _statusLbl.Location  = new Point(14, 17);
            _statusLbl.AutoSize  = true;

            _doneBtn           = new SteamButton();
            _doneBtn.Text      = "Done";
            _doneBtn.BaseColor = Pal.BtnGreen;
            _doneBtn.Size      = new Size(88, 30);
            _doneBtn.Anchor    = AnchorStyles.Right | AnchorStyles.Top;
            _doneBtn.Click    += (s, e) => TryCapture();
            bar.Resize        += (s, e) => { _doneBtn.Location = new Point(bar.Width - 100, 10); };

            bar.Controls.Add(_statusLbl);
            bar.Controls.Add(_doneBtn);

            _browser                        = new WebBrowser();
            _browser.Dock                   = DockStyle.Fill;
            _browser.ScriptErrorsSuppressed = true;
            _browser.Navigated             += (s, e) =>
            {
                if (e.Url == null) return;
                string u = e.Url.ToString().ToLower();
                if (u.Contains("steamcommunity.com") && !u.Contains("/login/") && !u.Contains("/openid/"))
                    TryCapture();
            };

            Controls.Add(_browser);
            Controls.Add(bar);
            _browser.Navigate("https://steamcommunity.com/login/home/?goto=");
        }

        void TryCapture()
        {
            string sid    = ReadCookie("https://steamcommunity.com", "sessionid");
            string secure = ReadCookie("https://steamcommunity.com", "steamLoginSecure");
            if (string.IsNullOrEmpty(secure))
                secure = ReadCookie("https://steamcommunity.com", "steamLogin");

            if (!string.IsNullOrEmpty(sid) && !string.IsNullOrEmpty(secure))
            {
                CapturedSessionId   = sid;
                CapturedLoginSecure = secure;
                _statusLbl.Text      = "Logged in!  Closing...";
                _statusLbl.ForeColor = Pal.Green;
                var t      = new System.Windows.Forms.Timer();
                t.Interval = 700;
                t.Tick    += (s, e) => { t.Stop(); DialogResult = DialogResult.OK; Close(); };
                t.Start();
            }
            else
            {
                _statusLbl.Text      = "Not logged in yet — complete the Steam login form.";
                _statusLbl.ForeColor = Pal.Orange;
            }
        }

        string ReadCookie(string url, string name)
        {
            int size = 1024;
            var sb   = new StringBuilder(size);
            if (!InternetGetCookieEx(url, name, sb, ref size, INTERNET_COOKIE_HTTPONLY, IntPtr.Zero)) return null;
            string raw = sb.ToString();
            int eq     = raw.IndexOf('=');
            return eq >= 0 ? raw.Substring(eq + 1) : raw;
        }
    }

    // =========================================================================
    //  SettingsForm  – mode tabs: Browser Login / API Key / Manual
    // =========================================================================

    class SettingsForm : Form
    {
        SteamButton _tabCookies, _tabApiKey, _tabManual;
        Panel       _pCookies, _pApiKey, _pManual;
        Label       _cookieStatus;
        TextBox     _keyBox, _sidBox;

        public SettingsForm()
        {
            Text            = "Settings";
            Size            = new Size(560, 380);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            StartPosition   = FormStartPosition.CenterParent;
            BackColor       = Pal.BgWindow;
            ForeColor       = Pal.TxtPri;
            Font            = Fnt.Label;

            // ── Header ────────────────────────────────────────────────────────
            var hdr       = new Panel();
            hdr.Dock      = DockStyle.Top;
            hdr.Height    = 52;
            hdr.BackColor = Pal.BgBar;
            hdr.Paint    += (s, e) =>
            {
                var g = e.Graphics;
                Draw.SetHQ(g);
                using (var br = new SolidBrush(Pal.TxtPri))
                    g.DrawString("Settings", Fnt.LabelBold, br, 16, 18);
                using (var p = new Pen(Pal.Separator, 1))
                    g.DrawLine(p, 0, hdr.Height - 1, hdr.Width, hdr.Height - 1);
            };

            // ── Mode tab row ──────────────────────────────────────────────────
            var tabRow       = new Panel();
            tabRow.Dock      = DockStyle.Top;
            tabRow.Height    = 44;
            tabRow.BackColor = Pal.BgPanel;
            tabRow.Paint    += (s, e) =>
            {
                using (var p = new Pen(Pal.Separator, 1))
                    e.Graphics.DrawLine(p, 0, tabRow.Height - 1, tabRow.Width, tabRow.Height - 1);
            };

            _tabCookies          = MakeTabBtn("Browser Login");
            _tabApiKey           = MakeTabBtn("API Key");
            _tabManual           = MakeTabBtn("Manual");
            _tabCookies.Location = new Point(12, 6);
            _tabApiKey.Location  = new Point(_tabCookies.Right + 6, 6);
            _tabManual.Location  = new Point(_tabApiKey.Right  + 6, 6);
            _tabCookies.Click   += (s, e) => SelectTab(0);
            _tabApiKey.Click    += (s, e) => SelectTab(1);
            _tabManual.Click    += (s, e) => SelectTab(2);
            tabRow.Controls.Add(_tabCookies);
            tabRow.Controls.Add(_tabApiKey);
            tabRow.Controls.Add(_tabManual);

            // ── Content area ──────────────────────────────────────────────────
            var content       = new Panel();
            content.Dock      = DockStyle.Fill;
            content.BackColor = Pal.BgWindow;
            content.Padding   = new Padding(18, 16, 18, 0);

            _pCookies = MakeContentPanel();
            _pApiKey  = MakeContentPanel();
            _pManual  = MakeContentPanel();
            BuildCookiePanel();
            BuildApiKeyPanel();
            BuildManualPanel();
            content.Controls.Add(_pManual);
            content.Controls.Add(_pApiKey);
            content.Controls.Add(_pCookies);

            // ── Footer ────────────────────────────────────────────────────────
            var footer       = new Panel();
            footer.Dock      = DockStyle.Bottom;
            footer.Height    = 56;
            footer.BackColor = Pal.BgBar;
            footer.Paint    += (s, e) =>
            {
                using (var p = new Pen(Pal.Separator, 1))
                    e.Graphics.DrawLine(p, 0, 0, footer.Width, 0);
            };

            var saveBtn        = new SteamButton();
            saveBtn.Text       = "Save";
            saveBtn.BaseColor  = Pal.BtnGreen;
            saveBtn.Size       = new Size(110, 32);
            saveBtn.Anchor     = AnchorStyles.Right | AnchorStyles.Top;
            saveBtn.DialogResult = DialogResult.OK;
            saveBtn.Click     += OnSave;

            var cancelBtn        = new SteamButton();
            cancelBtn.Text       = "Cancel";
            cancelBtn.BaseColor  = Pal.BtnGray;
            cancelBtn.Size       = new Size(110, 32);
            cancelBtn.Anchor     = AnchorStyles.Right | AnchorStyles.Top;
            cancelBtn.DialogResult = DialogResult.Cancel;

            footer.Resize += (s, e) =>
            {
                saveBtn.Location   = new Point(footer.Width - 124, 12);
                cancelBtn.Location = new Point(saveBtn.Left - 118, 12);
            };
            footer.Controls.Add(saveBtn);
            footer.Controls.Add(cancelBtn);

            Controls.Add(content);
            Controls.Add(tabRow);
            Controls.Add(hdr);
            Controls.Add(footer);

            AcceptButton = saveBtn;
            CancelButton = cancelBtn;

            if      (AppConfig.AuthMode == AppConfig.ModeApiKey)  SelectTab(1);
            else if (AppConfig.AuthMode == AppConfig.ModeManual)  SelectTab(2);
            else                                                   SelectTab(0);
        }

        void SelectTab(int idx)
        {
            _tabCookies.BaseColor = idx == 0 ? Pal.BtnBlue : Pal.BtnGray;
            _tabApiKey.BaseColor  = idx == 1 ? Pal.BtnBlue : Pal.BtnGray;
            _tabManual.BaseColor  = idx == 2 ? Pal.BtnBlue : Pal.BtnGray;
            _tabCookies.Invalidate(); _tabApiKey.Invalidate(); _tabManual.Invalidate();
            _pCookies.Visible = idx == 0;
            _pApiKey.Visible  = idx == 1;
            _pManual.Visible  = idx == 2;
        }

        void BuildCookiePanel()
        {
            _cookieStatus      = Lbl("", new Point(0, 0));
            _cookieStatus.Size = new Size(500, 42);
            _cookieStatus.Font = Fnt.Label;
            UpdateCookieStatus();

            var loginBtn       = new SteamButton();
            loginBtn.Text      = "Open Browser — Log in to Steam";
            loginBtn.BaseColor = Pal.BtnBlue;
            loginBtn.Size      = new Size(256, 32);
            loginBtn.Location  = new Point(0, 50);
            loginBtn.Click    += (s, e) =>
            {
                using (var dlg = new CookieLoginDialog())
                {
                    if (dlg.ShowDialog(this) == DialogResult.OK && dlg.LoggedIn)
                    {
                        AppConfig.SessionId   = dlg.CapturedSessionId;
                        AppConfig.LoginSecure = dlg.CapturedLoginSecure;
                        string det = SteamHelper.DetectSteamId64();
                        if (!string.IsNullOrEmpty(det)) AppConfig.SteamId = det;
                        UpdateCookieStatus();
                    }
                }
            };

            var clearBtn       = new SteamButton();
            clearBtn.Text      = "Forget Login";
            clearBtn.BaseColor = Pal.BtnRed;
            clearBtn.Size      = new Size(120, 32);
            clearBtn.Location  = new Point(264, 50);
            clearBtn.Click    += (s, e) =>
            {
                if (MessageBox.Show("Clear your saved Steam login cookies?\nYou will need to log in again.",
                        "Forget Login", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return;
                AppConfig.SessionId = AppConfig.LoginSecure = "";
                UpdateCookieStatus();
            };

            var note           = Lbl("Tip: you only need to do this once. Cookies are saved locally.", new Point(0, 94));
            note.ForeColor     = Pal.TxtDim;

            _pCookies.Controls.Add(_cookieStatus);
            _pCookies.Controls.Add(loginBtn);
            _pCookies.Controls.Add(clearBtn);
            _pCookies.Controls.Add(note);
        }

        void BuildApiKeyPanel()
        {
            _pApiKey.Controls.Add(Lbl("Steam Web API Key  (steamcommunity.com/dev/apikey):", new Point(0, 0)));
            _keyBox = Input(new Point(0, 20), AppConfig.ApiKey, 500);
            _pApiKey.Controls.Add(_keyBox);

            _pApiKey.Controls.Add(Lbl("Steam ID64:", new Point(0, 58)));

            var detectBtn       = new SteamButton();
            detectBtn.Text      = "Auto-detect from Steam";
            detectBtn.BaseColor = Pal.BtnBlue;
            detectBtn.Size      = new Size(200, 30);
            detectBtn.Location  = new Point(0, 76);
            detectBtn.Click    += (s, e) =>
            {
                string id = SteamHelper.DetectSteamId64();
                if (!string.IsNullOrEmpty(id))
                    _sidBox.Text = id;
                else
                    MessageBox.Show("Steam not running or user not detected.\nFind yours at steamidfinder.com",
                        "Not found", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };

            _sidBox          = Input(new Point(208, 76), AppConfig.SteamId, 292);
            _sidBox.Height   = 30;

            var note         = Lbl("Your full library will be shown. Sort by least played to find games with cards.", new Point(0, 116));
            note.ForeColor   = Pal.TxtDim;

            _pApiKey.Controls.Add(detectBtn);
            _pApiKey.Controls.Add(_sidBox);
            _pApiKey.Controls.Add(note);
        }

        void BuildManualPanel()
        {
            _pManual.Controls.Add(Lbl("No setup required.", new Point(0, 0)));
            var msg        = Lbl(
                "Use the Quick Idle bar at the bottom of the main window.\n" +
                "Type any AppID (visible in a game's Steam store URL) and click Start or press Enter.\n\n" +
                "Steam will register those games as running immediately — no account login needed.",
                new Point(0, 22));
            msg.Size       = new Size(500, 80);
            msg.ForeColor  = Pal.TxtSec;
            _pManual.Controls.Add(msg);
        }

        void UpdateCookieStatus()
        {
            if (AppConfig.HasCookies())
            {
                _cookieStatus.Text      = "Status:  Logged in.  Click 'Refresh' in the main window to load your library.";
                _cookieStatus.ForeColor = Pal.Green;
            }
            else
            {
                _cookieStatus.Text      = "Status:  Not logged in.  Click the button below to open the login window.";
                _cookieStatus.ForeColor = Pal.Orange;
            }
        }

        void OnSave(object sender, EventArgs e)
        {
            string active = _pCookies.Visible ? AppConfig.ModeCookies
                          : _pApiKey.Visible  ? AppConfig.ModeApiKey
                          :                     AppConfig.ModeManual;

            if (active == AppConfig.ModeApiKey)
            {
                ulong dummy;
                if (string.IsNullOrEmpty(_keyBox.Text.Trim()) || string.IsNullOrEmpty(_sidBox.Text.Trim()))
                { Warn("API Key and SteamID64 are required for this mode."); DialogResult = DialogResult.None; return; }
                if (_sidBox.Text.Trim().Length != 17 || !ulong.TryParse(_sidBox.Text.Trim(), out dummy))
                { Warn("SteamID64 must be exactly 17 digits."); DialogResult = DialogResult.None; return; }
                AppConfig.ApiKey   = _keyBox.Text.Trim();
                AppConfig.SteamId  = _sidBox.Text.Trim();
            }

            if (active == AppConfig.ModeCookies && !AppConfig.HasCookies())
            {
                if (MessageBox.Show("You haven't logged in yet. Save anyway?", "Not logged in",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.No)
                { DialogResult = DialogResult.None; return; }
            }

            AppConfig.AuthMode = active;
        }

        void Warn(string msg)
        {
            MessageBox.Show(msg, "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        SteamButton MakeTabBtn(string text)
        {
            var b       = new SteamButton();
            b.Text      = text;
            b.BaseColor = Pal.BtnGray;
            b.Size      = new Size(132, 32);
            b.Font      = Fnt.BtnSm;
            return b;
        }

        Panel MakeContentPanel()
        {
            var p       = new Panel();
            p.Dock      = DockStyle.Fill;
            p.BackColor = Color.Transparent;
            p.Visible   = false;
            return p;
        }

        Label Lbl(string text, Point loc)
        {
            var l       = new Label();
            l.Text      = text;
            l.Location  = loc;
            l.AutoSize  = true;
            l.BackColor = Color.Transparent;
            l.ForeColor = Pal.TxtPri;
            return l;
        }

        TextBox Input(Point loc, string val, int w)
        {
            var t         = new TextBox();
            t.Location    = loc;
            t.Width       = w;
            t.BackColor   = Pal.BgInput;
            t.ForeColor   = Pal.TxtPri;
            t.BorderStyle = BorderStyle.FixedSingle;
            t.Font        = Fnt.Input;
            t.Text        = val;
            return t;
        }
    }

    // =========================================================================
    //  MainForm
    // =========================================================================

    class MainForm : Form
    {
        // UI
        Panel       _listPanel;
        TextBox     _search;
        Label       _statsLbl;
        Label       _loadingLbl;
        SteamButton _runBtn, _stopBtn, _idleDropsBtn;
        TextBox     _quickBox;
        NotifyIcon  _tray;

        // State
        Dictionary<int, GameCard> _cards       = new Dictionary<int, GameCard>();
        Dictionary<int, Process>  _procs       = new Dictionary<int, Process>();
        SemaphoreSlim             _imgThrottle  = new SemaphoreSlim(8);
        string _exeDir;
        bool   _preventedSleep;
        bool   _fetching;

        // Win32: cue-banner (placeholder) text for TextBox controls
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, string lParam);
        static void SetPlaceholder(TextBox tb, string hint)
        {
            SendMessage(tb.Handle, 0x1501 /*EM_SETCUEBANNER*/, 1, hint);
        }

        static readonly string[] RequiredFiles = new string[]
            { "steam-idle.exe", "CSteamworks.dll", "steam_api.dll", "Steamworks.NET.dll" };

        public MainForm()
        {
            _exeDir     = Path.GetDirectoryName(Application.ExecutablePath);
            Text        = "Steam Card Idler  v" + AppConfig.AppVersion;
            Size        = new Size(1000, 740);
            MinimumSize = new Size(820, 560);
            BackColor   = Pal.BgWindow;
            ForeColor   = Pal.TxtPri;
            Font        = Fnt.Label;
            Icon        = SystemIcons.Application;

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);

            BuildUI();

            // Auto-detect SteamID if missing
            if (string.IsNullOrEmpty(AppConfig.SteamId))
            {
                string det = SteamHelper.DetectSteamId64();
                if (!string.IsNullOrEmpty(det)) AppConfig.SteamId = det;
            }

            InitTray();

            // Defer placeholder text + first-fetch until the form handle exists.
            // Calling BeginInvoke from the constructor (before handle creation) throws.
            Load += (s, e) =>
            {
                SetPlaceholder(_search,   "Search games...");
                SetPlaceholder(_quickBox, "e.g.  431960  or  431960, 570, 1091500");

                if (!AppConfig.HasCookies() && !AppConfig.HasApiKey()
                    && AppConfig.AuthMode != AppConfig.ModeManual)
                    BeginInvoke(new Action(OpenSettings));
                else
                    BeginInvoke(new Action(FetchLibrary));
            };
        }

        // ---- Tray icon ------------------------------------------------------

        void InitTray()
        {
            _tray              = new NotifyIcon();
            _tray.Icon         = SystemIcons.Application;
            _tray.Text         = "Steam Card Idler";
            _tray.Visible      = true;
            _tray.DoubleClick += (s, e) => RestoreWindow();

            var ctxMenu       = new ContextMenuStrip();
            ctxMenu.Renderer  = DarkMenuRenderer.Instance;
            ctxMenu.BackColor = Pal.BgBar;
            ctxMenu.ForeColor = Pal.TxtPri;
            ctxMenu.Font      = Fnt.Label;

            var openItem     = new ToolStripMenuItem("Open");
            openItem.Font    = new Font(Fnt.Label, FontStyle.Bold);
            openItem.Click  += (s, e) => RestoreWindow();

            var stopAllItem    = new ToolStripMenuItem("Stop All Idling");
            stopAllItem.Click += (s, e) => StopAll();

            var exitItem    = new ToolStripMenuItem("Exit");
            exitItem.Click += (s, e) =>
            {
                StopAll();
                _tray.Visible = false;
                Application.Exit();
            };

            ctxMenu.Items.Add(openItem);
            ctxMenu.Items.Add(new ToolStripSeparator());
            ctxMenu.Items.Add(stopAllItem);
            ctxMenu.Items.Add(new ToolStripSeparator());
            ctxMenu.Items.Add(exitItem);

            _tray.ContextMenuStrip = ctxMenu;
        }

        void RestoreWindow()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        // ---- UI Construction ------------------------------------------------

        void BuildUI()
        {
            // ── Header ────────────────────────────────────────────────────────
            var hdr       = new Panel();
            hdr.Dock      = DockStyle.Top;
            hdr.Height    = 64;
            hdr.BackColor = Pal.BgBar;
            hdr.Paint    += (s, e) =>
            {
                var g = e.Graphics;
                Draw.SetHQ(g);
                using (var br = new SolidBrush(Pal.TxtPri))
                    g.DrawString("STEAM CARD IDLER", Fnt.Title, br, 16, 19);
                using (var p = new Pen(Pal.Separator, 1))
                    g.DrawLine(p, 0, hdr.Height - 1, hdr.Width, hdr.Height - 1);

                // Mode pill
                string modeText = AppConfig.AuthMode == AppConfig.ModeCookies ? "Cookie Login"
                               : AppConfig.AuthMode == AppConfig.ModeApiKey   ? "API Key Mode"
                               : "Manual Mode";
                var mr = new RectangleF(188, 22, 100, 20);
                using (var path = Draw.RoundedRect(mr, 8))
                using (var br2  = new SolidBrush(Color.FromArgb(50, Pal.Accent)))
                    g.FillPath(br2, path);
                using (var sf  = new StringFormat { Alignment = StringAlignment.Center,
                                                    LineAlignment = StringAlignment.Center })
                using (var br2 = new SolidBrush(Pal.Accent))
                    g.DrawString(modeText, Fnt.Badge, br2, mr, sf);
            };

            _search             = new TextBox();
            _search.Width       = 240;
            _search.BackColor   = Pal.BgInput;
            _search.ForeColor   = Pal.TxtPri;
            _search.BorderStyle = BorderStyle.FixedSingle;
            _search.Font        = Fnt.Input;
            _search.TextChanged += (s, e) => ApplyFilter();

            var refreshBtn       = new SteamButton();
            refreshBtn.Text      = "Refresh";
            refreshBtn.BaseColor = Pal.BtnGray;
            refreshBtn.Size      = new Size(88, 30);
            refreshBtn.Anchor    = AnchorStyles.Right | AnchorStyles.Top;
            refreshBtn.Click    += (s, e) => FetchLibrary();

            var settingsBtn       = new SteamButton();
            settingsBtn.Text      = "Settings";
            settingsBtn.BaseColor = Pal.BtnGray;
            settingsBtn.Size      = new Size(88, 30);
            settingsBtn.Anchor    = AnchorStyles.Right | AnchorStyles.Top;
            settingsBtn.Click    += (s, e) => OpenSettings();

            hdr.Resize += (s, e) =>
            {
                settingsBtn.Location = new Point(hdr.Width - settingsBtn.Width - 14, 17);
                refreshBtn.Location  = new Point(settingsBtn.Left - refreshBtn.Width - 6, 17);
                _search.Location     = new Point(refreshBtn.Left - _search.Width - 10, 19);
                _search.Height       = 26;
            };
            hdr.Controls.Add(_search);
            hdr.Controls.Add(refreshBtn);
            hdr.Controls.Add(settingsBtn);

            // ── Stats bar ─────────────────────────────────────────────────────
            var statsBar       = new Panel();
            statsBar.Dock      = DockStyle.Top;
            statsBar.Height    = 32;
            statsBar.BackColor = Pal.BgPanel;
            statsBar.Paint    += (s, e) =>
            {
                using (var p = new Pen(Pal.Separator, 1))
                    e.Graphics.DrawLine(p, 0, statsBar.Height - 1, statsBar.Width, statsBar.Height - 1);
            };

            _statsLbl           = new Label();
            _statsLbl.Text      = "No library loaded";
            _statsLbl.Font      = Fnt.Stat;
            _statsLbl.ForeColor = Pal.TxtSec;
            _statsLbl.AutoSize  = true;
            _statsLbl.Location  = new Point(16, 8);
            statsBar.Controls.Add(_statsLbl);

            // ── Scrollable game list ───────────────────────────────────────────
            _listPanel            = new Panel();
            _listPanel.Dock       = DockStyle.Fill;
            _listPanel.BackColor  = Pal.BgWindow;
            _listPanel.AutoScroll = true;
            _listPanel.Padding    = new Padding(0);

            _loadingLbl           = new Label();
            _loadingLbl.Text      = GetPlaceholderText();
            _loadingLbl.Dock      = DockStyle.Fill;
            _loadingLbl.TextAlign = ContentAlignment.MiddleCenter;
            _loadingLbl.Font      = Fnt.CardSub;
            _loadingLbl.ForeColor = Pal.TxtDim;
            _loadingLbl.BackColor = Color.Transparent;
            _listPanel.Controls.Add(_loadingLbl);

            // ── Bottom strip ──────────────────────────────────────────────────
            var btm       = new Panel();
            btm.Dock      = DockStyle.Bottom;
            btm.Height    = 120;
            btm.BackColor = Pal.BgBar;
            btm.Paint    += (s, e) =>
            {
                using (var p = new Pen(Pal.Separator, 1))
                    e.Graphics.DrawLine(p, 0, 0, btm.Width, 0);
            };

            // Quick Idle row
            var quickRow       = new Panel();
            quickRow.Dock      = DockStyle.Top;
            quickRow.Height    = 64;
            quickRow.BackColor = Pal.BgQuick;
            quickRow.Paint    += (s, e) =>
            {
                using (var p = new Pen(Pal.Separator, 1))
                    e.Graphics.DrawLine(p, 0, quickRow.Height - 1, quickRow.Width, quickRow.Height - 1);
            };

            var qlbl       = new Label();
            qlbl.Text      = "Quick Idle:";
            qlbl.Font      = Fnt.Stat;
            qlbl.ForeColor = Pal.TxtSec;
            qlbl.AutoSize  = true;
            qlbl.Location  = new Point(16, 16);

            _quickBox             = new TextBox();
            _quickBox.Width       = 260;
            _quickBox.BackColor   = Pal.BgInput;
            _quickBox.ForeColor   = Pal.TxtPri;
            _quickBox.BorderStyle = BorderStyle.FixedSingle;
            _quickBox.Font        = Fnt.Input;
            _quickBox.KeyDown    += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; RunQuickIdle(); }
            };

            var qStart       = new SteamButton();
            qStart.Text      = "Start";
            qStart.BaseColor = Pal.BtnGreen;
            qStart.Size      = new Size(78, 30);
            qStart.Click    += (s, e) => RunQuickIdle();

            var qStop       = new SteamButton();
            qStop.Text      = "Stop All";
            qStop.BaseColor = Pal.BtnRed;
            qStop.Size      = new Size(78, 30);
            qStop.Click    += (s, e) => StopAll();

            var qHintLbl       = new Label();
            qHintLbl.Text      = "AppID or comma-separated list";
            qHintLbl.Font      = new Font("Segoe UI", 7.5f);
            qHintLbl.ForeColor = Pal.TxtDim;
            qHintLbl.AutoSize  = true;

            quickRow.Resize += (s, e) =>
            {
                int x              = qlbl.Right + 10;
                _quickBox.Location = new Point(x, 11);
                _quickBox.Height   = 28;
                qHintLbl.Location  = new Point(x + 2, _quickBox.Bottom + 2);
                qStart.Location    = new Point(_quickBox.Right + 8, 10);
                qStop.Location     = new Point(qStart.Right + 6, 10);
            };
            quickRow.Controls.Add(qlbl);
            quickRow.Controls.Add(_quickBox);
            quickRow.Controls.Add(qHintLbl);
            quickRow.Controls.Add(qStart);
            quickRow.Controls.Add(qStop);

            // Action row
            var actRow       = new Panel();
            actRow.Dock      = DockStyle.Fill;
            actRow.BackColor = Pal.BgBar;

            var selAll       = new SteamButton();
            selAll.Text      = "Select All";
            selAll.BaseColor = Pal.BtnGray;
            selAll.Size      = new Size(100, 30);
            selAll.Location  = new Point(12, 12);
            selAll.Click    += (s, e) => SetAllChecked(true);

            var clrAll       = new SteamButton();
            clrAll.Text      = "Clear";
            clrAll.BaseColor = Pal.BtnGray;
            clrAll.Size      = new Size(72, 30);
            clrAll.Location  = new Point(120, 12);
            clrAll.Click    += (s, e) => SetAllChecked(false);

            _idleDropsBtn           = new SteamButton();
            _idleDropsBtn.Text      = "Idle All Drops";
            _idleDropsBtn.BaseColor = Pal.BtnBlue;
            _idleDropsBtn.Size      = new Size(118, 30);
            _idleDropsBtn.Location  = new Point(200, 12);
            _idleDropsBtn.Click    += (s, e) => IdleAllDrops();

            _runBtn           = new SteamButton();
            _runBtn.Text      = "Run Selected";
            _runBtn.BaseColor = Pal.BtnGreen;
            _runBtn.Size      = new Size(128, 32);
            _runBtn.Anchor    = AnchorStyles.Right | AnchorStyles.Top;
            _runBtn.Click    += (s, e) => RunSelected();

            _stopBtn           = new SteamButton();
            _stopBtn.Text      = "Stop All";
            _stopBtn.BaseColor = Pal.BtnGray;
            _stopBtn.Size      = new Size(100, 32);
            _stopBtn.Enabled   = false;
            _stopBtn.Anchor    = AnchorStyles.Right | AnchorStyles.Top;
            _stopBtn.Click    += (s, e) => StopAll();

            actRow.Resize += (s, e) =>
            {
                _runBtn.Location  = new Point(actRow.Width - _runBtn.Width - 14, 10);
                _stopBtn.Location = new Point(_runBtn.Left - _stopBtn.Width - 6, 10);
            };

            actRow.Controls.Add(selAll);
            actRow.Controls.Add(clrAll);
            actRow.Controls.Add(_idleDropsBtn);
            actRow.Controls.Add(_runBtn);
            actRow.Controls.Add(_stopBtn);

            btm.Controls.Add(actRow);
            btm.Controls.Add(quickRow);

            Controls.Add(_listPanel);
            Controls.Add(statsBar);
            Controls.Add(hdr);
            Controls.Add(btm);
        }

        string GetPlaceholderText()
        {
            if (AppConfig.AuthMode == AppConfig.ModeManual)
                return "Manual mode  –  use Quick Idle below to idle any AppID without logging in.";
            return "Click Refresh to load your Steam library.\nMake sure Steam is running.";
        }

        // ---- Settings -------------------------------------------------------

        void OpenSettings()
        {
            using (var dlg = new SettingsForm())
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                AppConfig.Save();
                _loadingLbl.Text = GetPlaceholderText();
                Invalidate(true);  // repaint mode pill
                RefreshStatus();   // update mode-sensitive buttons (e.g. Idle All Drops)
                FetchLibrary();
            }
        }

        // ---- Library fetch --------------------------------------------------

        async void FetchLibrary()
        {
            // Manual mode: show help text and clear any previously loaded library
            if (AppConfig.AuthMode == AppConfig.ModeManual)
            {
                _listPanel.SuspendLayout();
                foreach (var c in _cards.Values) { _listPanel.Controls.Remove(c); c.Dispose(); }
                _cards.Clear();
                _loadingLbl.Text = GetPlaceholderText();
                if (!_listPanel.Controls.Contains(_loadingLbl))
                    _listPanel.Controls.Add(_loadingLbl);
                _listPanel.ResumeLayout(true);
                UpdateStats();
                return;
            }

            if (_fetching) return;
            _fetching = true;
            try
            {
                // Stop any running games before clearing the card list
                if (_procs.Count > 0) StopAll();

                // Clear stale search filter so all fresh results are visible
                _search.Text = "";

                _listPanel.SuspendLayout();
                foreach (var c in _cards.Values) { _listPanel.Controls.Remove(c); c.Dispose(); }
                _cards.Clear();

                _loadingLbl.Text = "Loading library...";
                if (!_listPanel.Controls.Contains(_loadingLbl))
                    _listPanel.Controls.Add(_loadingLbl);
                _listPanel.ResumeLayout(true);
                UpdateStats();

                if      (AppConfig.AuthMode == AppConfig.ModeCookies) await FetchBadgeMode();
                else if (AppConfig.AuthMode == AppConfig.ModeApiKey)  await FetchApiMode();
            }
            finally { _fetching = false; }
        }

        async Task FetchBadgeMode()
        {
            if (!AppConfig.HasCookies())
            { _loadingLbl.Text = "Not logged in.\n\nOpen Settings → Browser Login."; return; }

            try
            {
                _loadingLbl.Text = "Scraping badge pages… (page 1)";
                var games = await BadgeScraper.GetGamesWithDropsAsync(
                    AppConfig.SessionId, AppConfig.LoginSecure,
                    page =>
                    {
                        if (!IsDisposed)
                            _loadingLbl.Text = "Scraping badge pages… (page " + page.ToString() + ")";
                    });

                _listPanel.SuspendLayout();
                _listPanel.Controls.Remove(_loadingLbl);

                foreach (var g in games)
                {
                    var card         = new GameCard(g.AppId, g.Name, g.PlaytimeMinutes, g.RemainingDrops);
                    card.CheckedChanged = UpdateStats;
                    _cards[g.AppId]  = card;
                    _listPanel.Controls.Add(card);
                    int id = g.AppId;
                    var _ = LoadImageAsync(id);
                }

                _listPanel.ResumeLayout(true);

                if (games.Count == 0)
                {
                    _loadingLbl.Text = "No games with card drops remaining — or login expired.\nTry Settings → Browser Login again.";
                    _listPanel.Controls.Add(_loadingLbl);
                }
                UpdateStats();
            }
            catch (Exception ex)
            {
                _loadingLbl.Text = "Badge page load failed.\n\n" + HumanizeNetworkError(ex) + "\n\nTry logging in again via Settings.";
                if (!_listPanel.Controls.Contains(_loadingLbl)) _listPanel.Controls.Add(_loadingLbl);
            }
        }

        async Task FetchApiMode()
        {
            if (!AppConfig.HasApiKey())
            { _loadingLbl.Text = "API key or SteamID not configured.\n\nOpen Settings."; return; }

            try
            {
                string url = "https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/"
                    + "?key="    + AppConfig.ApiKey
                    + "&steamid=" + AppConfig.SteamId
                    + "&include_appinfo=1&include_played_free_games=1&format=json";

                string json;
                using (var wc = new TimedWebClient())
                    json = await wc.DownloadStringTaskAsync(url);

                var gamesToken = JObject.Parse(json)["response"]["games"];
                if (gamesToken == null || gamesToken.Type == Newtonsoft.Json.Linq.JTokenType.Null)
                {
                    _loadingLbl.Text = "No games found. Check API key / SteamID in Settings.";
                    _listPanel.Controls.Add(_loadingLbl);
                    return;
                }

                var games  = (JArray)gamesToken;
                var sorted = games.OrderBy(g => g["playtime_forever"] != null ? (int)g["playtime_forever"] : 0).ToList();

                _listPanel.SuspendLayout();
                _listPanel.Controls.Remove(_loadingLbl);

                foreach (var g in sorted)
                {
                    int    aid  = (int)g["appid"];
                    string name = (string)g["name"] ?? ("App " + aid.ToString());
                    int    mins = g["playtime_forever"] != null ? (int)g["playtime_forever"] : 0;
                    var card    = new GameCard(aid, name, mins, 0);
                    card.CheckedChanged = UpdateStats;
                    _cards[aid] = card;
                    _listPanel.Controls.Add(card);
                    int id = aid;
                    var _ = LoadImageAsync(id);
                }

                _listPanel.ResumeLayout(true);
                if (sorted.Count == 0)
                {
                    _loadingLbl.Text = "No games found. Check API key / SteamID in Settings.";
                    _listPanel.Controls.Add(_loadingLbl);
                }
                UpdateStats();
            }
            catch (Exception ex)
            {
                _loadingLbl.Text = "Failed to load library.\n\n" + HumanizeNetworkError(ex) + "\n\nCheck Settings.";
                if (!_listPanel.Controls.Contains(_loadingLbl)) _listPanel.Controls.Add(_loadingLbl);
            }
        }

        // ---- Image loading --------------------------------------------------

        async Task LoadImageAsync(int appId)
        {
            await _imgThrottle.WaitAsync();
            try
            {
                string path = Path.Combine(AppConfig.CacheDir, appId.ToString() + ".jpg");
                Image img   = await Task.Run<Image>(() =>
                {
                    byte[] data;
                    if (File.Exists(path))
                        data = File.ReadAllBytes(path);
                    else
                    {
                        using (var wc = new TimedWebClient())
                            data = wc.DownloadData("https://cdn.cloudflare.steamstatic.com/steam/apps/"
                                + appId.ToString() + "/capsule_sm_120.jpg");
                        try { File.WriteAllBytes(path, data); } catch { }
                    }
                    // Single-decode: Bitmap(Stream) fully decodes JPEG at construction time.
                    // The previous pattern (new Bitmap(Image.FromStream(...))) created two GDI
                    // objects and leaked the inner Image on every load.
                    using (var ms = new MemoryStream(data))
                        return new Bitmap(ms);
                });
                GameCard card;
                if (_cards.TryGetValue(appId, out card)) card.SetImage(img);
                else img.Dispose();
            }
            catch
            {
                // Image download or decode failed — mark the card so "loading..." doesn't linger forever
                GameCard card;
                if (_cards.TryGetValue(appId, out card)) card.MarkImageFailed();
            }
            finally { _imgThrottle.Release(); }
        }

        // ---- Filter & stats -------------------------------------------------

        void ApplyFilter()
        {
            string q = _search.Text.ToLowerInvariant();
            _listPanel.SuspendLayout();
            foreach (var c in _cards.Values)
                c.Visible = q.Length == 0 || c.GameName.ToLowerInvariant().Contains(q);
            _listPanel.ResumeLayout(true);
        }

        void SetAllChecked(bool v)
        {
            // Detach the per-card UpdateStats hook during bulk change, then fire once at the end
            foreach (var c in _cards.Values)
            {
                if (!c.Visible) continue;
                var saved = c.CheckedChanged;
                c.CheckedChanged = null;
                c.SetChecked(v);
                c.CheckedChanged = saved;
            }
            UpdateStats();
        }

        void UpdateStats()
        {
            int total    = _cards.Count;
            int drops    = _cards.Values.Count(c => c.RemainingDrops > 0);
            int selected = _cards.Values.Count(c => c.Checked);
            int running  = _procs.Count;

            if (total == 0 && running == 0)
            { _statsLbl.Text = "No library loaded"; return; }

            string t = total.ToString() + " games";
            if (drops    > 0) t += "  ·  " + drops.ToString()    + " with card drops";
            if (selected > 0) t += "  ·  " + selected.ToString() + " selected";
            if (running  > 0) t += "  ·  " + running.ToString()  + " currently idling";
            _statsLbl.Text = t;
        }

        // ---- Quick Idle (no auth needed) ------------------------------------

        void RunQuickIdle()
        {
            string raw = _quickBox.Text.Trim();
            if (string.IsNullOrEmpty(raw)) return;
            if (!CheckIdleExe()) return;

            var  parts   = raw.Split(new char[] { ',', ' ', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            bool started = false;
            foreach (var p in parts)
            {
                int appId;
                if (!int.TryParse(p.Trim(), out appId) || appId <= 0) continue;
                if (_procs.ContainsKey(appId)) continue;

                if (!_cards.ContainsKey(appId))
                {
                    var card      = new GameCard(appId, "App " + appId.ToString(), 0, 0);
                    card.CheckedChanged = UpdateStats;
                    _cards[appId] = card;
                    _listPanel.Controls.Add(card);
                    int id = appId;
                    var _ = LoadImageAsync(id);
                }
                StartOne(appId);
                started = true;
            }
            if (started) { _quickBox.Clear(); UpdateStats(); RefreshStatus(); }
        }

        // ---- Library idle ---------------------------------------------------

        void RunSelected()
        {
            if (!CheckIdleExe()) return;
            var selected = _cards.Values.Where(c => c.Checked).Select(c => c.AppId).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("Check at least one game to idle.", "Nothing selected",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            // Stop any games no longer in the selection
            foreach (var id in _procs.Keys.Except(selected).ToList()) StopOne(id);
            // Start newly selected games
            foreach (var id in selected) if (!_procs.ContainsKey(id)) StartOne(id);
            UpdateStats();
            RefreshStatus();
        }

        void IdleAllDrops()
        {
            if (!CheckIdleExe()) return;
            var withDrops = _cards.Values
                .Where(c => c.Visible && c.RemainingDrops > 0)
                .Select(c => c.AppId).ToList();
            if (withDrops.Count == 0)
            {
                MessageBox.Show("No visible games with card drops remaining.",
                    "Nothing to idle", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            foreach (var id in withDrops)
                if (!_procs.ContainsKey(id)) StartOne(id);
            UpdateStats();
            RefreshStatus();
        }

        // ---- Process management --------------------------------------------

        void StartOne(int appId)
        {
            try
            {
                string work = Path.Combine(AppConfig.RunDir, appId.ToString());
                Directory.CreateDirectory(work);
                foreach (var f in RequiredFiles)
                {
                    string src = Path.Combine(_exeDir, f);
                    string dst = Path.Combine(work,    f);
                    if (File.Exists(src) && !File.Exists(dst)) File.Copy(src, dst);
                }
                File.WriteAllText(Path.Combine(work, "steam_appid.txt"), appId.ToString());

                var psi              = new ProcessStartInfo();
                psi.FileName         = Path.Combine(work, "steam-idle.exe");
                psi.Arguments        = appId.ToString();
                psi.WorkingDirectory = work;
                psi.CreateNoWindow   = true;
                psi.UseShellExecute  = false;

                var proc = Process.Start(psi);
                if (proc == null)
                    throw new InvalidOperationException("Process.Start returned null — steam-idle.exe may be missing or blocked.");

                // Detect when steam-idle.exe exits on its own (Steam closed, crash, etc.)
                proc.EnableRaisingEvents = true;
                int capturedId = appId;
                proc.Exited += (s, e) =>
                {
                    // Guard: form may already be disposed when a background process exits
                    if (IsDisposed || !IsHandleCreated) return;
                    BeginInvoke(new Action(() =>
                    {
                        // ContainsKey check is inside BeginInvoke (UI thread) to avoid data race
                        if (_procs.ContainsKey(capturedId))
                        {
                            StopOne(capturedId);
                            UpdateStats();
                            RefreshStatus();
                        }
                    }));
                };

                _procs[appId] = proc;

                GameCard card;
                if (_cards.TryGetValue(appId, out card))
                {
                    card.StopAction = () =>
                    {
                        if (InvokeRequired)
                            BeginInvoke(new Action(() => { StopOne(capturedId); UpdateStats(); RefreshStatus(); }));
                        else
                            { StopOne(capturedId); UpdateStats(); RefreshStatus(); }
                    };
                    card.SetRunning(true);
                }

                if (!_preventedSleep)
                {
                    SystemHelper.PreventSleep();
                    _preventedSleep = true;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not start App " + appId.ToString() + ":\n" + ex.Message,
                    "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        void StopOne(int appId)
        {
            Process proc;
            if (_procs.TryGetValue(appId, out proc))
            {
                try { if (!proc.HasExited) proc.Kill(); } catch { }
                _procs.Remove(appId);
                try { proc.Dispose(); } catch { }
            }
            GameCard card;
            if (_cards.TryGetValue(appId, out card))
            {
                card.StopAction = null;
                card.SetRunning(false);
            }
            if (_procs.Count == 0 && _preventedSleep)
            {
                SystemHelper.AllowSleep();
                _preventedSleep = false;
            }
        }

        void StopAll()
        {
            foreach (var id in _procs.Keys.ToList()) StopOne(id);
            UpdateStats();
            RefreshStatus();
        }

        void RefreshStatus()
        {
            int n = _procs.Count;

            _stopBtn.BaseColor = n > 0 ? Pal.BtnRed : Pal.BtnGray;
            _stopBtn.Enabled   = n > 0;
            _stopBtn.Invalidate();

            // "Idle All Drops" only applies in cookie mode — API mode has no drop-count data
            _idleDropsBtn.Visible = AppConfig.AuthMode == AppConfig.ModeCookies;

            // Window title — include version when not idling
            string baseTitle = "Steam Card Idler  v" + AppConfig.AppVersion;
            Text = n > 0
                ? "Steam Card Idler  —  " + n.ToString() + (n == 1 ? " game idling" : " games idling")
                : baseTitle;

            // Tray tooltip (64-char limit enforced by Windows)
            if (_tray != null)
            {
                string tip = n > 0 ? "Steam Card Idler — " + n.ToString() + " idling" : "Steam Card Idler";
                _tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
            }
        }

        bool CheckIdleExe()
        {
            if (File.Exists(Path.Combine(_exeDir, "steam-idle.exe"))) return true;
            MessageBox.Show(
                "steam-idle.exe not found next to SteamIdler.exe.\n\nPlace these files in the same folder:\n" +
                "  steam-idle.exe\n  CSteamworks.dll\n  steam_api.dll\n  Steamworks.NET.dll",
                "Missing Files", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        // ---- Window events --------------------------------------------------

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // If the user closes the window while games are idling, send to tray
            if (e.CloseReason == CloseReason.UserClosing && _procs.Count > 0)
            {
                e.Cancel = true;
                Hide();
                _tray.ShowBalloonTip(3500, "Still Idling",
                    _procs.Count.ToString() + " game(s) running in the background.\n"
                    + "Right-click the tray icon to stop or exit.",
                    ToolTipIcon.Info);
                return;
            }
            StopAll();
            if (_tray != null) _tray.Visible = false;
            base.OnFormClosing(e);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState == FormWindowState.Minimized && _procs.Count > 0)
            {
                Hide();
                _tray.ShowBalloonTip(2500, "Minimized to Tray",
                    "Steam Card Idler is still running — "
                    + _procs.Count.ToString() + " game(s) idling.",
                    ToolTipIcon.Info);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_imgThrottle != null) { _imgThrottle.Dispose(); _imgThrottle = null; }
                if (_tray        != null) { _tray.Visible = false;  _tray.Dispose(); _tray = null; }
            }
            base.Dispose(disposing);
        }

        // ---- Helpers -------------------------------------------------------

        /// <summary>Converts common WebExceptions into readable one-liners for the UI.</summary>
        static string HumanizeNetworkError(Exception ex)
        {
            var we = ex as WebException;
            if (we != null)
            {
                if (we.Status == WebExceptionStatus.Timeout)
                    return "Connection timed out. Check your internet connection and try again.";
                if (we.Status == WebExceptionStatus.NameResolutionFailure)
                    return "Could not reach Steam servers. Check your internet connection.";
                if (we.Status == WebExceptionStatus.ProtocolError && we.Response != null)
                {
                    var resp = (HttpWebResponse)we.Response;
                    if (resp.StatusCode == HttpStatusCode.Unauthorized)
                        return "Login expired (401). Please log in again via Settings.";
                    if (resp.StatusCode == HttpStatusCode.Forbidden)
                        return "Access denied (403). Check your credentials in Settings.";
                    if (resp.StatusCode == HttpStatusCode.ServiceUnavailable)
                        return "Steam servers are temporarily unavailable (503). Try again in a moment.";
                    return "Server returned error " + ((int)resp.StatusCode).ToString()
                         + " — " + resp.StatusDescription;
                }
            }
            return ex.Message;
        }
    }

    // =========================================================================
    //  Entry point
    // =========================================================================

    static class Program
    {
        [STAThread]
        static void Main()
        {
            // ── Single-instance guard ─────────────────────────────────────────
            bool createdNew;
            var mutex = new Mutex(true, "SteamCardIdler_SingleInstance", out createdNew);
            if (!createdNew)
            {
                MessageBox.Show(
                    "Steam Card Idler is already running.\nCheck the system tray.",
                    "Already Running", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // ── Global exception handlers ─────────────────────────────────────
            // Catch unhandled exceptions thrown on the UI thread
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) =>
            {
                MessageBox.Show(
                    "An unexpected error occurred:\n\n" + e.Exception.Message,
                    "Steam Card Idler — Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            // Catch unhandled exceptions thrown on background threads
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                var ex  = e.ExceptionObject as Exception;
                string msg = ex != null ? ex.Message : e.ExceptionObject.ToString();
                MessageBox.Show(
                    "A fatal error occurred:\n\n" + msg,
                    "Steam Card Idler — Fatal Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            // ── DPI awareness ─────────────────────────────────────────────────
            // Must be called BEFORE any window is created. Prevents Windows from
            // bitmap-stretching the form on high-DPI displays (which blurs text).
            SystemHelper.EnableDpiAwareness();

            // ── Networking ────────────────────────────────────────────────────
            // Steam API and image CDN require TLS 1.2+
            ServicePointManager.SecurityProtocol =
                SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());

            GC.KeepAlive(mutex);  // prevent GC from releasing the mutex before app exits
        }
    }
}
