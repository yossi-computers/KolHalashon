using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace KolHalashonKiosk
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Log.Init();
            Log.Write("==== הפעלה ====");
            Log.Write("ExecutablePath=" + Application.ExecutablePath);
            Log.Write("User=" + Environment.UserName + "  OS=" + Environment.OSVersion);

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                Log.Write("UnhandledException: " + (e.ExceptionObject as Exception)?.ToString());
            Application.ThreadException += (s, e) =>
                Log.Write("ThreadException: " + e.Exception);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try { EnsureWebView2(); }
            catch (Exception ex) { Log.Write("EnsureWebView2 נכשל: " + ex); }
            Log.Write("מפעיל את החלון הראשי...");
            Application.Run(new MainForm());
            Log.Write("==== סגירה ====");
        }

        // בודק אם WebView2 Runtime מותקן, ואם לא - מתקין אוטומטית מהקובץ המוטמע (bootstrapper).
        static void EnsureWebView2()
        {
            try
            {
                var v = CoreWebView2Environment.GetAvailableBrowserVersionString();
                if (!string.IsNullOrEmpty(v)) { Log.Write("WebView2 Runtime מותקן, גרסה " + v); return; }
            }
            catch (Exception ex) { Log.Write("WebView2 Runtime לא נמצא: " + ex.Message); }

            try
            {
                string tmp = Path.Combine(Path.GetTempPath(), "WebView2Installer.exe");
                var asm = Assembly.GetExecutingAssembly();
                string res = null;
                // חיפוש לפי שם מדויק ולא לפי סיומת ".exe": מוטמע בתוכנה גם ffmpeg.exe.
                foreach (var n in asm.GetManifestResourceNames())
                    if (n.EndsWith("MicrosoftEdgeWebview2Setup.exe", StringComparison.OrdinalIgnoreCase)) { res = n; break; }
                if (res == null) throw new Exception("לא נמצא מתקין מוטמע");
                using (var s = asm.GetManifestResourceStream(res))
                using (var f = File.Create(tmp))
                    s.CopyTo(f);

                MessageBox.Show(
                    "להפעלת התוכנה נדרש רכיב WebView2. תתבצע כעת התקנה אוטומטית (ייתכן שתופיע בקשת הרשאת מנהל).",
                    "התקנת רכיב נדרש", MessageBoxButtons.OK, MessageBoxIcon.Information);

                var psi = new ProcessStartInfo(tmp, "/silent /install") { UseShellExecute = true };
                var p = Process.Start(psi);
                p.WaitForExit();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "לא ניתן היה להתקין את WebView2 אוטומטית.\nאנא התקינו ידנית את 'Microsoft Edge WebView2 Runtime'.\n\n" + ex.Message,
                    "שגיאת התקנה", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    // לוג שגיאות לקובץ (כמה מיקומים עד שאחד ניתן לכתיבה - חשוב בקיוסק עם הרשאות מוגבלות).
    static class Log
    {
        private static string path;
        private static readonly object gate = new object();

        public static void Init()
        {
            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KolHalashonKiosk", "log.txt"),
                Path.Combine(Path.GetTempPath(), "KolHalashonKiosk-log.txt"),
                Path.Combine(Path.GetDirectoryName(Application.ExecutablePath) ?? ".", "KolHalashonKiosk-log.txt"),
            };
            foreach (var c in candidates)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(c));
                    File.AppendAllText(c, "", Encoding.UTF8);
                    path = c;
                    return;
                }
                catch { }
            }
        }

        public static void Write(string msg)
        {
            if (path == null) return;
            try
            {
                lock (gate)
                    File.AppendAllText(path,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + msg + Environment.NewLine,
                        Encoding.UTF8);
            }
            catch { }
        }

        public static string Path_ => path;
    }

    public class MainForm : Form
    {
        private readonly WebView2 web = new WebView2();
        private readonly WebView2 apiWeb = new WebView2();   // נסתר: מבצע את קריאות ה-API דרך דפדפן אמיתי (עוקף Cloudflare)
        private readonly WebView2 dlWeb = new WebView2();    // נסתר: דפדפן ייעודי להורדות בלבד (ללא קוד של האתר)
        private CoreWebView2Environment env;

        // ----- הגדרות (config.json בתיקיית המשתמש) -----
        private string configPath;
        private string title = "הורדת שיעורים - קול הלשון";
        private string username = "";
        private string password = "";
        private string downloadFolder = "";
        private string defaultQuality = "audio";
        // מצב קיוסק: ההורדה מותרת רק להתקן חיצוני (נגן/אונקי/כרטיס זיכרון) ולא למחשב עצמו.
        private bool kioskDeviceOnly = false;
        // מסך ההתחברות מוסתר כברירת מחדל: אף סוג הורדה אינו דורש חשבון יותר.
        // הקוד נשאר במקומו וניתן להחזרה ע"י showLogin=true בקובץ ההגדרות.
        private bool showLogin = false;
        private string adminPassword = "000000";
        private string exitPassword = "000000";
        private bool requireExitPassword = false;   // ברירת מחדל: יציאה ללא סיסמה (ניתן להפעיל בניהול)
        private bool kioskFullscreen = true;
        private int idleSeconds = 0;
        private string baseUrl = KhlBrowser.DefaultApiBase;
        private string siteKey = "8ea2pe8";
        private int maxParallelDownloads = 4;
        private int sessionMinutes = 30;   // משך חיבור משתמש; 0 = ללא הגבלה
        private bool allowVideo = true;    // הצגת כפתור הורדת וידאו
        private bool allowHd = true;       // הצגת כפתור הורדת HD
        private string currentTargetId = MediaTargets.PcId;   // יעד ההורדות הנוכחי
        private string authToken = "";   // ה-token שנלכד מהתחברות באתר (נשמר מוצפן להתחברות אוטומטית בהפעלה הבאה)
        private DateTime loginTime = DateTime.MinValue;   // מתי בוצעה ההתחברות הנוכחית (לחישוב פקיעה)
        private System.Windows.Forms.Timer sessionTimer;

        private KhlBrowser khl;
        private DownloadManager downloads;
        private readonly SemaphoreSlim loginGate = new SemaphoreSlim(1, 1);

        // חלון ניהול
        private Form adminForm;
        private WebView2 adminWeb;
        private bool adminCanClose = false;

        // חלון אימות Cloudflare (כשהדפדפן הנסתר נחסם ונדרש "אני לא רובוט")
        private Form verifyForm;
        private WebView2 verifyWeb;
        private System.Windows.Forms.Timer verifyPoll;
        private Label verifyStatus;
        private DateTime lastVerifyOpen = DateTime.MinValue;
        private bool verifyBusy;

        // חלון התחברות דרך האתר (לכידת token)
        private Form siteLoginForm;
        private WebView2 siteLoginWeb;
        private System.Windows.Forms.Timer siteLoginPoll;

        public MainForm()
        {
            Text = "Kol Halashon Kiosk";
            BackColor = Color.FromArgb(15, 23, 42);
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            // ה-WebView הנסתר של ה-API - מאחורי הראשי (מוסתר לגמרי ע"י web שממלא את החלון).
            // גודל סביר ולא 2x2: חלון זעיר נראה חריג לבדיקות הבוט של Cloudflare ומקשה על פתרון האתגר.
            apiWeb.Size = new Size(480, 360);
            apiWeb.Location = new Point(0, 0);
            Controls.Add(apiWeb);

            dlWeb.Size = new Size(480, 360);
            dlWeb.Location = new Point(0, 0);
            Controls.Add(dlWeb);

            web.Dock = DockStyle.Fill;
            Controls.Add(web);
            web.BringToFront();

            bool initStarted = false;
            Shown += async (s, e) =>
            {
                if (initStarted) return;
                initStarted = true;
                try { Log.Write("Shown: מתחיל InitAsync"); await InitAsync(); Log.Write("InitAsync הסתיים"); }
                catch (Exception ex)
                {
                    Log.Write("InitAsync נכשל: " + ex);
                    MessageBox.Show("שגיאה באתחול הממשק. ודאו ש-WebView2 Runtime מותקן.\n\n" + ex.Message +
                        "\n\nפרטים נכתבו ללוג:\n" + (Log.Path_ ?? "(לא ניתן לכתוב לוג)"),
                        "שגיאה", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    Application.Exit();
                }
            };
        }

        private async Task InitAsync()
        {
            string appDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KolHalashonKiosk");
            Directory.CreateDirectory(appDir);
            string udf = Path.Combine(appDir, "WebView2");
            Directory.CreateDirectory(udf);
            configPath = Path.Combine(appDir, "config.json");

            LoadSettings();
            ApplyWindowMode();

            // Chromium מאט/מקפיא רינדור וטיימרים בחלונות מוסתרים או ברקע. הדפדפן הנסתר שלנו מוסתר
            // מאחורי הממשק - וחניקה כזו עלולה למנוע מסקריפט האתגר של Cloudflare לסיים את עבודתו.
            var envOptions = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments =
                    "--disable-background-timer-throttling --disable-renderer-backgrounding --disable-backgrounding-occluded-windows"
            };
            env = await CoreWebView2Environment.CreateAsync(null, udf, envOptions);

            // ה-WebView הנסתר: מבצע את קריאות ה-API וההורדות דרך דפדפן אמיתי (עוקף את אתגר Cloudflare)
            await web.EnsureCoreWebView2Async(env);       // ודא HWND לפני apiWeb (שניהם על אותה סביבה)
            await apiWeb.EnsureCoreWebView2Async(env);
            await dlWeb.EnsureCoreWebView2Async(env);
            khl = new KhlBrowser(apiWeb, baseUrl, siteKey);
            khl.AttachDownloadView(dlWeb);   // ההורדות רצות בדפדפן נפרד, לא בדף האתר

            // התחברות אוטומטית מ-token שמור - רק אם משך החיבור שהוגדר טרם פג (אחרת נדרשת התחברות מחדש)
            if (!string.IsNullOrEmpty(authToken))
            {
                bool expired = sessionMinutes > 0 &&
                    (loginTime == DateTime.MinValue || (DateTime.Now - loginTime).TotalMinutes >= sessionMinutes);
                if (expired) { Log.Write("החיבור השמור פג - נדרשת התחברות מחדש"); authToken = ""; loginTime = DateTime.MinValue; SaveSettings(); }
                else khl.SetToken(authToken);
            }
            StartSessionTimer();
            // ניווט ל-www2 והמתנה ל-Cloudflare ברקע (לא חוסם את עליית הממשק); לאחר מכן משקפים מצב התחברות.
            // אם הדפדפן הנסתר לא הצליח לעבור את Cloudflare - פותחים חלון אימות שבו המשתמש מאשר "אני אנושי".
            _ = khl.InitAsync().ContinueWith(t => MarshalToUi(() =>
            {
                PostLoginState();
                bool ok = t.Status == TaskStatus.RanToCompletion && t.Result;
                if (!ok) OpenVerifyWindow(false);

                // בדיקת הורדה מקצה לקצה לצורכי אבחון, ללא ממשק: הגדירו KHL_SELFTEST_DOWNLOAD=<fileId>
                // בדיקת התחברות בשם משתמש/סיסמה ללא ממשק: KHL_SELFTEST_LOGIN=user:pass (או 1 לפרטים השמורים)
                string testLogin = Environment.GetEnvironmentVariable("KHL_SELFTEST_LOGIN");
                if (ok && !string.IsNullOrWhiteSpace(testLogin))
                {
                    string u = username, p = password;
                    int sep = testLogin.IndexOf(':');
                    if (sep > 0) { u = testLogin.Substring(0, sep); p = testLogin.Substring(sep + 1); }
                    _ = SelfTestLoginAsync(u, p);
                }

                // בדיקת חיפוש: רושם ליומן את תוצאות החיפוש ואת פרטי השיעור הראשון מסוג "שיעור"
                string testSearch = Environment.GetEnvironmentVariable("KHL_SELFTEST_SEARCH");
                if (ok && !string.IsNullOrWhiteSpace(testSearch)) _ = SelfTestSearchAsync(testSearch);

                // בדיקת הורדה דרך התור האמיתי (כולל בניית הנתיב ביעד הנבחר): KHL_SELFTEST_QUEUE=<fileId>
                string testQueue = Environment.GetEnvironmentVariable("KHL_SELFTEST_QUEUE");
                if (ok && !string.IsNullOrWhiteSpace(testQueue))
                    foreach (var part in testQueue.Split(','))
                        if (long.TryParse(part.Trim(), out var qid))
                        {
                            Log.Write("בדיקת תור: מוסיף " + qid);
                            downloads.Enqueue(qid, KhlBrowser.Quality.Audio, "בדיקת יעד " + qid, "בדיקה", "");
                        }

                string testIds = Environment.GetEnvironmentVariable("KHL_SELFTEST_DOWNLOAD");
                if (ok && !string.IsNullOrWhiteSpace(testIds))
                    foreach (var part in testIds.Split(','))
                        if (long.TryParse(part.Trim(), out var tid))
                            _ = SelfTestDownloadAsync(tid);
            }), TaskScheduler.Default);

            _ = Task.Run(CleanStaging);
            downloads = new DownloadManager(khl, EnsureLoggedInAsync, ResolveDownloadFolder(), maxParallelDownloads);
            downloads.OnUpdate += OnDownloadUpdate;

            // בדיקות: התקן מדומה נבחר אוטומטית כיעד (ראו MediaTargets)
            string simDevice = Environment.GetEnvironmentVariable("KHL_TEST_DEVICE");
            if (!string.IsNullOrWhiteSpace(simDevice)) { currentTargetId = simDevice.Trim(); ApplyDownloadTarget(); }

            var core = web.CoreWebView2;
            // תמונות הרבנים מוגשות מהמטמון המקומי דרך מארח וירטואלי. בלי זה הדף היה
            // צריך כתובת file:// - שדפדפן חוסם מדף שנטען מ-NavigateToString.
            try
            {
                Directory.CreateDirectory(RavCacheFolder());
                core.SetVirtualHostNameToFolderMapping(RavCacheHost, RavCacheFolder(),
                    CoreWebView2HostResourceAccessKind.Allow);
            }
            catch (Exception ex) { Log.Write("מיפוי מטמון התמונות נכשל: " + ex.Message); }
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.WebMessageReceived += OnWebMessage;
            core.NavigationCompleted += (s, e) => { try { Activate(); web.Focus(); } catch { } };

            core.NavigateToString(BuildKioskHtml());
        }

        private async Task SelfTestSearchAsync(string keyword)
        {
            try
            {
                string json = await khl.SearchAsync(keyword);
                Log.Write("בדיקת חיפוש: " + json.Substring(0, Math.Min(1400, json.Length)));
                using var doc = JsonDocument.Parse(json);
                bool didShiur = false, didSubject = false;
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    int ty = el.TryGetProperty("SearchItemType", out var t) ? t.GetInt32() : 0;
                    long id = el.TryGetProperty("SearchItemId", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt64() : -1;
                    long big = el.TryGetProperty("SearchItemBigId", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt64() : -1;
                    string sid = el.TryGetProperty("SearchItemStrId", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : "";
                    string txt = el.TryGetProperty("SearchItemTextHebrew", out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : "";

                    if (ty == 10 && big > 0 && !didShiur)
                    {
                        didShiur = true;
                        string det = await khl.GetShiurDetailsAsync(big);
                        Log.Write("בדיקת חיפוש: פרטי שיעור(" + big + ") = " + det.Substring(0, Math.Min(300, det.Length)));
                    }
                    else if (ty != 2 && ty != 10 && !didSubject)
                    {
                        didSubject = true;
                        Log.Write("בדיקת חיפוש: פריט סוג " + ty + " id=" + id + " big=" + big + " str=" + sid + " טקסט=" + txt);
                        string res = await khl.GetSearchResultsAsync(ty, id, big, sid, txt, 0, 5);
                        Log.Write("בדיקת חיפוש: תוצאות = " + res.Substring(0, Math.Min(700, res.Length)));
                    }
                    if (didShiur && didSubject) break;
                }
            }
            catch (Exception ex) { Log.Write("בדיקת חיפוש נכשלה: " + ex.Message); }
        }

        private async Task SelfTestLoginAsync(string u, string p)
        {
            try
            {
                Log.Write("בדיקת התחברות עצמית: משתמש=" + u + " אורך סיסמה=" + (p ?? "").Length);
                bool ok = await khl.LoginAsync(u, p);
                Log.Write("בדיקת התחברות עצמית: " + (ok ? "הצליחה" : "נכשלה"));
            }
            catch (Exception ex) { Log.Write("בדיקת התחברות עצמית נכשלה: " + ex.Message); }
        }

        // הורדת קובץ בודד לתיקייה זמנית ורישום התוצאה ליומן - לבדיקת שרשרת ההורדה כולה בלי הממשק.
        private async Task SelfTestDownloadAsync(long fileId)
        {
            string path = Path.Combine(Path.GetTempPath(), "khl-selftest-" + fileId + ".mp3");
            try
            {
                Log.Write("בדיקת הורדה עצמית: fileId=" + fileId);
                try { if (File.Exists(path)) File.Delete(path); } catch { }
                await khl.DownloadToFileAsync(fileId, KhlBrowser.Quality.Audio,
                    () => khl.GetDownloadKeyAsync(fileId), path, (d, t) => { }, CancellationToken.None);
                Log.Write("בדיקת הורדה עצמית הצליחה: " + path + "  גודל=" + new FileInfo(path).Length);
            }
            catch (Exception ex) { Log.Write("בדיקת הורדה עצמית נכשלה: " + ex.Message); }
        }

        private void ApplyWindowMode()
        {
            if (kioskFullscreen)
            {
                FormBorderStyle = FormBorderStyle.None;
                WindowState = FormWindowState.Maximized;
            }
            else
            {
                FormBorderStyle = FormBorderStyle.Sizable;
                WindowState = FormWindowState.Normal;
                Width = 1280; Height = 820;
                StartPosition = FormStartPosition.CenterScreen;
            }
        }

        private string ResolveDownloadFolder()
        {
            return string.IsNullOrWhiteSpace(downloadFolder)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "קול הלשון")
                : downloadFolder;
        }

        // ----- ניתוב הודעות מהממשק -----
        private async void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            JsonDocument doc = null;
            try
            {
                doc = JsonDocument.Parse(args.WebMessageAsJson);
                var root = doc.RootElement;
                string type = root.GetProperty("type").GetString();

                switch (type)
                {
                    case "ready":
                        PushSettingsToUi();
                        PostLoginState();
                        PushTargets();
                        break;

                    case "search":
                    {
                        string kw = root.GetProperty("keyword").GetString();
                        await DoSearchAsync(kw);
                        break;
                    }

                    // מסכי עיון: רבנים / נושאים / סדרות / ערוצים. מחזירים רשימה בלבד;
                    // הפתיחה של פריט נעשית בהודעות openRav / openSearchItem הקיימות.
                    case "browse":
                    {
                        string what = root.TryGetProperty("what", out var bw) ? bw.GetString() : "";
                        int fromRow = root.TryGetProperty("fromRow", out var bf) ? bf.GetInt32() : 0;
                        int order = root.TryGetProperty("order", out var bo2) ? bo2.GetInt32() : 1;
                        int lang = root.TryGetProperty("lang", out var bl2) ? bl2.GetInt32() : -1;
                        string txt = root.TryGetProperty("text", out var bt2) ? bt2.GetString() : "";
                        await DoBrowseAsync(what, fromRow, order, lang, txt);
                        break;
                    }

                    // נושא מעץ הנושאים: דורש מזהה אב + מזהה הנושא עצמו
                    case "openSubject":
                    {
                        long mainId = root.TryGetProperty("mainId", out var sm) ? sm.GetInt64() : -1;
                        long subId  = root.TryGetProperty("subjectId", out var ss2) ? ss2.GetInt64() : -1;
                        string txt2 = root.TryGetProperty("text", out var st2) ? st2.GetString() : "";
                        int fr3  = root.TryGetProperty("fromRow", out var sf) ? sf.GetInt32() : 0;
                        int ord3 = root.TryGetProperty("order", out var so3) ? so3.GetInt32() : 7;
                        int lng3 = root.TryGetProperty("lang", out var sl3) ? sl3.GetInt32() : -1;
                        await DoSubjectAsync(mainId, subId, txt2, fr3, ord3, lng3);
                        break;
                    }

                    // לימוד יומי: שיעורי היום שנבחר + הכותרת + נתוני בורר התאריך העברי
                    case "openDaily":
                    {
                        long dailyId = root.TryGetProperty("dailyId", out var dq) ? dq.GetInt64() : -1;
                        long parentId = root.TryGetProperty("parentId", out var dp) ? dp.GetInt64() : 21;
                        string iso = root.TryGetProperty("date", out var dd) ? dd.GetString() : null;
                        int dfr = root.TryGetProperty("fromRow", out var df) ? df.GetInt32() : 0;
                        int dor = root.TryGetProperty("order", out var do2) ? do2.GetInt32() : 7;
                        int dln = root.TryGetProperty("lang", out var dl) ? dl.GetInt32() : -1;
                        string dtx = root.TryGetProperty("text", out var dx) ? dx.GetString() : "";
                        await DoDailyAsync(dailyId, parentId, iso, dtx, dfr, dor, dln);
                        break;
                    }

                    // המרת תאריך עברי שנבחר בבורר לתאריך לועזי
                    case "hebPick":
                    {
                        int hy = root.TryGetProperty("y", out var py) ? py.GetInt32() : 0;
                        int hm = root.TryGetProperty("m", out var pm) ? pm.GetInt32() : 1;
                        int hd = root.TryGetProperty("d", out var pd) ? pd.GetInt32() : 1;
                        PostToUi("{\"type\":\"hebPicked\",\"date\":" +
                                 JsonSerializer.Serialize(HebrewToIso(hy, hm, hd)) + "}");
                        break;
                    }

                    // פרשה / דף בגמרא - עוברים ב-endpoint של דף הבית
                    case "openHomeQuery":
                    {
                        int qt = root.TryGetProperty("queryType", out var hq) ? hq.GetInt32() : 5;
                        long pid = root.TryGetProperty("parashaId", out var hp) ? hp.GetInt64() : -1;
                        long mid = root.TryGetProperty("masechetId", out var hm) ? hm.GetInt64() : -1;
                        long daf = root.TryGetProperty("dafNo", out var hd) ? hd.GetInt64() : -1;
                        string htx = root.TryGetProperty("text", out var ht) ? ht.GetString() : "";
                        int hfr = root.TryGetProperty("fromRow", out var hf) ? hf.GetInt32() : 0;
                        int hor = root.TryGetProperty("order", out var ho) ? ho.GetInt32() : 7;
                        int hln = root.TryGetProperty("lang", out var hl) ? hl.GetInt32() : -1;
                        await DoHomeQueryAsync(qt, pid, mid, daf, htx, hfr, hor, hln);
                        break;
                    }

                    // הדף גילה שתמונת רב חסרה במטמון - מורידים אותה לפעם הבאה
                    case "cacheRavImg":
                    {
                        string f = root.TryGetProperty("file", out var cf) ? cf.GetString() : null;
                        _ = CacheRavImageAsync(f);
                        break;
                    }

                    // אפשרויות הסינון של הספרים (נושא / רב / שפה / עזרים)
                    case "booksFilters":
                    {
                        string ftx = root.TryGetProperty("text", out var ft) ? ft.GetString() : "";
                        int for2 = root.TryGetProperty("order", out var fo) ? fo.GetInt32() : 7;
                        int fln = root.TryGetProperty("lang", out var fl2) ? fl2.GetInt32() : -1;
                        string fsel = root.TryGetProperty("filters", out var ff) ? ff.GetRawText() : "[]";
                        await DoBooksFiltersAsync(ftx, for2, fln, fsel);
                        break;
                    }

                    case "books":
                    {
                        string text = root.TryGetProperty("text", out var bt) ? bt.GetString() : "";
                        int fromRow = root.TryGetProperty("fromRow", out var bfr) ? bfr.GetInt32() : 0;
                        int border = root.TryGetProperty("order", out var bo) ? bo.GetInt32() : 7;
                        int blang = root.TryGetProperty("lang", out var bl) ? bl.GetInt32() : -1;
                        string bfil = root.TryGetProperty("filters", out var bf2) ? bf2.GetRawText() : "[]";
                        await DoBooksAsync(text, fromRow, border, blang, bfil);
                        break;
                    }

                    case "openRav":
                    {
                        int ravId = root.GetProperty("ravId").GetInt32();
                        int fromRow = root.TryGetProperty("fromRow", out var fr) ? fr.GetInt32() : 0;
                        await DoRavShiurimAsync(ravId, fromRow);
                        break;
                    }

                    case "openShiur":
                    {
                        long fileId = root.GetProperty("fileId").GetInt64();
                        await DoShiurDetailsAsync(fileId);
                        break;
                    }

                    case "openSearchItem":
                    {
                        int itemType = root.TryGetProperty("itemType", out var it) ? it.GetInt32() : 0;
                        long id = root.TryGetProperty("id", out var iid) ? iid.GetInt64() : -1;
                        long bigId = root.TryGetProperty("bigId", out var bid) ? bid.GetInt64() : -1;
                        string strId = root.TryGetProperty("strId", out var sid) ? sid.GetString() : "";
                        string text = root.TryGetProperty("text", out var tx) ? tx.GetString() : "";
                        int fromRow = root.TryGetProperty("fromRow", out var fr2) ? fr2.GetInt32() : 0;
                        await DoSearchItemAsync(itemType, id, bigId, strId, text, fromRow);
                        break;
                    }

                    case "download":
                    {
                        // מצב קיוסק: אין הורדה למחשב. אם לא נבחר התקן - מבקשים לחבר אחד.
                        if (kioskDeviceOnly && !IsDeviceTarget(currentTargetId))
                        {
                            PostToast("במצב קיוסק ההורדה מתבצעת להתקן בלבד - חברו נגן, אונקי או כרטיס זיכרון.");
                            PushTargets();
                            break;
                        }
                        long fileId = root.GetProperty("fileId").GetInt64();
                        string q = root.TryGetProperty("quality", out var qq) ? qq.GetString() : defaultQuality;
                        string t = root.TryGetProperty("title", out var tt) ? tt.GetString() : null;
                        string r = root.TryGetProperty("rav", out var rr) ? rr.GetString() : null;
                        string tp = root.TryGetProperty("topic", out var tpp) ? tpp.GetString() : null;
                        downloads.Enqueue(fileId, ParseQuality(q), t, r, tp);
                        break;
                    }

                    case "cancelDownload":
                        downloads.Cancel(root.GetProperty("id").GetInt64());
                        break;

                    case "retryDownload":
                    {
                        long fileId = root.GetProperty("fileId").GetInt64();
                        string q = root.TryGetProperty("quality", out var qq) ? qq.GetString() : defaultQuality;
                        string t = root.TryGetProperty("title", out var tt) ? tt.GetString() : null;
                        string r = root.TryGetProperty("rav", out var rr) ? rr.GetString() : null;
                        string tp = root.TryGetProperty("topic", out var tpp) ? tpp.GetString() : null;
                        downloads.Enqueue(fileId, ParseQuality(q), t, r, tp);
                        break;
                    }

                    case "removeDownload":
                        downloads.Remove(root.GetProperty("id").GetInt64());
                        PushDownloadsSnapshot();
                        break;

                    case "clearFinished":
                        downloads.ClearFinished();
                        PushDownloadsSnapshot();
                        break;

                    case "openFolder":
                        OpenDownloadFolder();
                        break;

                    case "openFile":
                        OpenDownloadedFile(root.TryGetProperty("id", out var ofid) ? ofid.GetInt64() : -1);
                        break;

                    // הזרמת וידאו: הממשק מבקש את כתובת המניפסט, ומזרים ממנה בעצמו
                    case "videoStream":
                    {
                        long vid = root.GetProperty("fileId").GetInt64();
                        bool vhd = root.TryGetProperty("hd", out var vh) && vh.ValueKind == JsonValueKind.True;
                        await SendVideoUrlAsync(vid, vhd);
                        break;
                    }

                    case "revealFile":
                        RevealDownloadedFile(root.TryGetProperty("id", out var rfid) ? rfid.GetInt64() : -1);
                        break;

                    case "pickTargetFolder":
                        // דיאלוג מודאלי חייב לרוץ מחוץ למטפל האירוע של WebView2 (אחרת נעילה הדדית)
                        UiDefer(PickDownloadFolder);
                        break;

                    case "listTargets":
                        PushTargets();
                        break;

                    case "setTarget":
                    {
                        string id = root.TryGetProperty("id", out var tid) ? tid.GetString() : MediaTargets.PcId;
                        SetTarget(id);
                        break;
                    }

                    case "login":
                    {
                        string u = root.GetProperty("username").GetString();
                        string p = root.GetProperty("password").GetString();
                        bool remember = root.TryGetProperty("remember", out var rm) && rm.GetBoolean();
                        await DoManualLoginAsync(u, p, remember);
                        break;
                    }

                    case "openSiteLogin":
                        UiDefer(OpenSiteLogin);
                        break;

                    case "logout":
                        LogoutUser("התנתקת");
                        break;

                    case "openAdmin":
                        UiDefer(OpenAdminWindow);
                        break;

                    case "verifyCloudflare":
                        UiDefer(() => OpenVerifyWindow(true));
                        break;

                    case "requestExit":
                    {
                        string p = root.TryGetProperty("password", out var pw) ? pw.GetString() : "";
                        if (!requireExitPassword || VerifyPassword(p, exitPassword))
                        {
                            Log.Write(requireExitPassword ? "יציאה מאושרת ע\"י סיסמה" : "יציאה");
                            UiDefer(Application.Exit);
                        }
                        else
                        {
                            try { web.CoreWebView2.PostWebMessageAsJson("{\"type\":\"exitDenied\"}"); } catch { }
                        }
                        break;
                    }

                    case "quit":
                        UiDefer(Application.Exit);
                        break;
                }
            }
            catch (Exception ex) { Log.Write("OnWebMessage שגיאה: " + ex.Message); }
            finally { doc?.Dispose(); }
        }

        private KhlBrowser.Quality ParseQuality(string q)
        {
            switch ((q ?? "audio").ToLowerInvariant())
            {
                case "video": return KhlBrowser.Quality.Video;
                case "hd": case "hdvideo": return KhlBrowser.Quality.HdVideo;
                case "pdf": return KhlBrowser.Quality.Pdf;
                default: return KhlBrowser.Quality.Audio;
            }
        }

        private async Task DoSearchAsync(string keyword)
        {
            if (string.IsNullOrWhiteSpace(keyword)) return;
            PostBusy(true, "מחפש...");
            try
            {
                Log.Write("חיפוש: " + keyword);
                string json = await khl.SearchAsync(keyword.Trim());
                Log.Write("חיפוש הצליח, אורך תשובה=" + (json?.Length ?? 0));
                PostToUi("{\"type\":\"searchResults\",\"keyword\":" + JsonSerializer.Serialize(keyword) + ",\"items\":" + json + "}");
            }
            catch (Exception ex)
            {
                Log.Write("חיפוש נכשל: " + ex);
                PostToast("שגיאה בחיפוש: " + ex.Message);
                // חסימת Cloudflare - פותחים אימות ידני (פעם אחת, לא בכל חיפוש כושל)
                if (!khl.IsReady) UiDefer(() => OpenVerifyWindow(false));
            }
            finally { PostBusy(false, null); }
        }

        private async Task DoRavShiurimAsync(int ravId, int fromRow)
        {
            PostBusy(true, "טוען שיעורים...");
            try
            {
                string json = await khl.GetRavShiurimAsync(ravId, fromRow, 24);
                PostToUi("{\"type\":\"ravShiurim\",\"ravId\":" + ravId + ",\"fromRow\":" + fromRow +
                         ",\"shiurim\":" + WithHebrewDates(json) + "}");
            }
            catch (Exception ex) { PostToast("שגיאה בטעינת השיעורים: " + ex.Message); }
            finally { PostBusy(false, null); }
        }

        // פתיחת פריט חיפוש שאינו רב ואינו שיעור בודד (נושא/קטגוריה/סדרה/ספר) -> רשימת שיעורים אמיתית.
        private async Task DoSearchItemAsync(int itemType, long id, long bigId, string strId, string text, int fromRow)
        {
            PostBusy(true, "טוען שיעורים...");
            try
            {
                string json = await khl.GetSearchResultsAsync(itemType, id, bigId, strId, text, fromRow, 24);
                string list = "[]";
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("ShiurimList", out var sl)) list = sl.GetRawText();
                }
                catch (Exception ex) { Log.Write("פענוח תוצאות נכשל: " + ex.Message); }

                PostToUi("{\"type\":\"searchShiurim\",\"fromRow\":" + fromRow +
                         ",\"text\":" + JsonSerializer.Serialize(text ?? "") +
                         ",\"shiurim\":" + WithHebrewDates(list) + "}");
            }
            catch (Exception ex) { Log.Write("טעינת תוצאות נכשלה: " + ex.Message); PostToast("שגיאה בטעינת התוצאות: " + ex.Message); }
            finally { PostBusy(false, null); }
        }

        // רשימות המסכים החדשים. רבנים/נושאים/ערוצים נטענים פעם אחת ונשמרים במטמון -
        // הם אינם משתנים במהלך הפעלה, ורשימת הרבנים לבדה שוקלת כחצי מגהבייט.
        private string cachedTopics, cachedChannels, cachedParashot, cachedHumashim, cachedMasechtot,
                       cachedNach, cachedMoadim;

        private async Task DoBrowseAsync(string what, int fromRow, int order, int lang, string text)
        {
            PostBusy(true, "טוען...");
            try
            {
                string json, extra = "";
                switch (what)
                {
                    case "ravs":
                        // שתי הקריאות יחד, כמו באתר: הרשימה עצמה, והספירה שממנה נגזרות
                        // גם כמות התוצאות הכוללת וגם רשימת השפות הזמינות לסינון.
                        json = await khl.SearchRavsAsync(text, lang, order, fromRow, 24);
                        extra = ",\"count\":" + await khl.SearchRavsCountAsync(text, lang);
                        break;
                    case "topics":   json = cachedTopics   ??= await khl.GetTopicsAsync(); break;
                    case "parashot": json = cachedParashot ??= await khl.GetParashotAsync(); break;
                    case "humashim": json = cachedHumashim ??= await khl.GetHumashimAsync(); break;
                    case "masechtot":json = cachedMasechtot??= await khl.GetMasechtotAsync(); break;
                    case "nach":     json = cachedNach      ??= await khl.GetNachAsync(); break;
                    case "moadim":   json = cachedMoadim    ??= await khl.GetMoadimAsync(); break;
                    case "channels": json = cachedChannels ??= await khl.GetChannelsAsync(); break;
                    case "series":   json = await khl.GetAllSeriesAsync(fromRow, 24, order, lang); break;
                    default: return;
                }
                PostToUi("{\"type\":\"browseList\",\"what\":" + JsonSerializer.Serialize(what) +
                         ",\"fromRow\":" + fromRow + ",\"items\":" + json + extra + "}");
            }
            catch (Exception ex)
            {
                Log.Write("טעינת רשימת " + what + " נכשלה: " + ex.Message);
                PostToast("שגיאה בטעינה: " + ex.Message);
            }
            finally { PostBusy(false, null); }
        }

        // ----- תאריך עברי -----
        // ההמרה נעשית כאן ולא בדפדפן: ה-ICU של WebView2 נופל בלוח העברי לתבנית מספרית
        // ("5786 12 11") במקום לאותיות. HebrewCalendar של .NET נותן את הצורה הנכונה,
        // ואף תואם בדיוק לתאריכים שקול הלשון עצמם כותבים בכותרות השיעורים.
        private static readonly CultureInfo HebCulture = CreateHebCulture();

        private static CultureInfo CreateHebCulture()
        {
            var ci = (CultureInfo)new CultureInfo("he-IL").Clone();
            ci.DateTimeFormat.Calendar = new System.Globalization.HebrewCalendar();
            return ci;
        }

        private static string ToHebrewDate(string iso)
        {
            if (string.IsNullOrWhiteSpace(iso)) return "";
            if (!DateTime.TryParse(iso, CultureInfo.InvariantCulture,
                                   System.Globalization.DateTimeStyles.None, out var d)) return "";
            // HebrewCalendar תומך רק ב-1583..2240 לספירה; מחוץ לטווח נשאיר ריק ולא נקרוס.
            try { return d.ToString("dd MMMM yyyy", HebCulture); }
            catch { return ""; }
        }

        // מוסיף לכל שיעור/ספר שדה HebDate. התשובה מהשרת מועברת לממשק כמות שהיא,
        // ולכן ההעשרה נעשית כאן, על ה-JSON, לפני השליחה.
        private static string WithHebrewDates(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return json;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var buf = new System.IO.MemoryStream();
                using (var w = new Utf8JsonWriter(buf))
                {
                    void WriteEnriched(JsonElement el)
                    {
                        if (el.ValueKind != JsonValueKind.Object) { el.WriteTo(w); return; }
                        w.WriteStartObject();
                        string rec = null;
                        foreach (var prop in el.EnumerateObject())
                        {
                            prop.WriteTo(w);
                            if (prop.NameEquals("RecordDate") && prop.Value.ValueKind == JsonValueKind.String)
                                rec = prop.Value.GetString();
                        }
                        w.WriteString("HebDate", ToHebrewDate(rec));
                        w.WriteEndObject();
                    }

                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        w.WriteStartArray();
                        foreach (var el in doc.RootElement.EnumerateArray()) WriteEnriched(el);
                        w.WriteEndArray();
                    }
                    else WriteEnriched(doc.RootElement);
                }
                return Encoding.UTF8.GetString(buf.ToArray());
            }
            catch (Exception ex) { Log.Write("הוספת תאריך עברי נכשלה: " + ex.Message); return json; }
        }

        // שיעורים של נושא מעץ הנושאים.
        private async Task DoSubjectAsync(long mainId, long subjectId, string text, int fromRow, int order, int lang)
        {
            PostBusy(true, "טוען שיעורים...");
            try
            {
                string json = await khl.GetShiurimOfSubjectAsync(mainId, subjectId, fromRow, 24, order, lang);
                PostShiurimList(json, fromRow, text);
            }
            catch (Exception ex) { Log.Write("טעינת נושא נכשלה: " + ex.Message); PostToast("שגיאה: " + ex.Message); }
            finally { PostBusy(false, null); }
        }

        // ----- לימוד יומי -----
        // מזהי הנושאים של "לימוד יומי" הם גם המזהים של פונקציות היום אצל קול הלשון.
        private static readonly Dictionary<long, string> DailyNames = new Dictionary<long, string>
        {
            { 91,  "DAILY_GetTodayDafBavli" },
            { 92,  "DAILY_GetTodayDafYerushalmi" },
            { 93,  "DAILY_GetTodayNavi" },
            { 94,  "DAILY_GetTodayChok" },
            { 95,  "DAILY_GetTodayMishna" },
            { 96,  "DAILY_GetTodayHalacha" },
            { 97,  "DAILY_GetTodayHH" },
            { 104, "DAILY_GetTodayHilchotShabat" },
        };

        public static bool IsDailyTopic(long id) => DailyNames.ContainsKey(id);

        private async Task DoDailyAsync(long dailyId, long parentId, string iso, string text,
            int fromRow, int order, int lang)
        {
            PostBusy(true, "טוען את הלימוד היומי...");
            try
            {
                DateTime day = DateTime.Today;
                if (!string.IsNullOrWhiteSpace(iso) &&
                    DateTime.TryParse(iso, CultureInfo.InvariantCulture,
                                      System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed))
                    day = parsed.Date;

                // הם קובעים 3 בבוקר כדי שהמרת אזורי זמן לא תזיז את היום
                string sendIso = day.ToString("yyyy-MM-ddT03:00:00.000Z", CultureInfo.InvariantCulture);

                string title = "";
                if (DailyNames.TryGetValue(dailyId, out var dailyName))
                {
                    try
                    {
                        string tj = await khl.GetDailyTitleAsync(dailyName, sendIso, false);
                        using var td = JsonDocument.Parse(tj);
                        if (td.RootElement.ValueKind == JsonValueKind.Array && td.RootElement.GetArrayLength() > 0 &&
                            td.RootElement[0].TryGetProperty("VarString", out var vs) &&
                            vs.ValueKind == JsonValueKind.String) title = vs.GetString();
                    }
                    catch (Exception ex) { Log.Write("כותרת לימוד יומי נכשלה: " + ex.Message); }
                }

                PostToUi("{\"type\":\"dailyInfo\"," + HebrewPickerJson(day) +
                         ",\"title\":" + JsonSerializer.Serialize(title ?? "") +
                         ",\"date\":" + JsonSerializer.Serialize(day.ToString("yyyy-MM-dd")) + "}");

                string json = await khl.GetShiurimOfSubjectAsync(dailyId, parentId, fromRow, 24, order, lang, sendIso);
                PostShiurimList(json, fromRow, text);
            }
            catch (Exception ex) { Log.Write("לימוד יומי נכשל: " + ex.Message); PostToast("שגיאה: " + ex.Message); }
            finally { PostBusy(false, null); }
        }

        // נתוני בורר התאריך העברי: השנה, החודשים שבה (12 או 13 בשנה מעוברת) והימים בחודש.
        private static string HebrewPickerJson(DateTime day)
        {
            var hc = (System.Globalization.HebrewCalendar)HebCulture.DateTimeFormat.Calendar;
            try
            {
                int hy = hc.GetYear(day), hm = hc.GetMonth(day), hd = hc.GetDayOfMonth(day);
                var months = new List<string>();
                int mcount = hc.GetMonthsInYear(hy);
                for (int m = 1; m <= mcount; m++)
                {
                    var first = hc.ToDateTime(hy, m, 1, 0, 0, 0, 0);
                    months.Add("{\"n\":" + m +
                               ",\"name\":" + JsonSerializer.Serialize(first.ToString("MMMM", HebCulture)) +
                               ",\"days\":" + hc.GetDaysInMonth(hy, m) + "}");
                }
                var years = new List<string>();
                for (int y = hy - 4; y <= hy + 1; y++)
                    years.Add("{\"n\":" + y +
                              ",\"name\":" + JsonSerializer.Serialize(HebrewYearName(hc, y)) + "}");

                return "\"hebText\":" + JsonSerializer.Serialize(ToHebrewDate(day.ToString("o"))) +
                       ",\"hy\":" + hy + ",\"hm\":" + hm + ",\"hd\":" + hd +
                       ",\"months\":[" + string.Join(",", months) + "]" +
                       ",\"years\":[" + string.Join(",", years) + "]";
            }
            catch (Exception ex)
            {
                Log.Write("בניית בורר תאריך נכשלה: " + ex.Message);
                return "\"hebText\":\"\",\"hy\":0,\"hm\":1,\"hd\":1,\"months\":[],\"years\":[]";
            }
        }

        // שם השנה באותיות ("תשפ\"ו") - נלקח מהעיצוב של יום א' בתשרי באותה שנה
        private static string HebrewYearName(System.Globalization.HebrewCalendar hc, int hy)
        {
            try { return hc.ToDateTime(hy, 1, 1, 0, 0, 0, 0).ToString("yyyy", HebCulture); }
            catch { return hy.ToString(); }
        }

        // המרת בחירה בבורר לתאריך לועזי. יום שאינו קיים בחודש נחתך לאחרון שבו.
        private static string HebrewToIso(int hy, int hm, int hd)
        {
            var hc = (System.Globalization.HebrewCalendar)HebCulture.DateTimeFormat.Calendar;
            try
            {
                if (hm < 1) hm = 1;
                int months = hc.GetMonthsInYear(hy);
                if (hm > months) hm = months;
                int days = hc.GetDaysInMonth(hy, hm);
                if (hd < 1) hd = 1;
                if (hd > days) hd = days;
                return hc.ToDateTime(hy, hm, hd, 0, 0, 0, 0).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                Log.Write("המרת תאריך עברי נכשלה: " + ex.Message);
                return DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
        }

        // שיעורים של פרשה או של דף בגמרא.
        private async Task DoHomeQueryAsync(int queryType, long parashaId, long masechetId, long dafNo,
            string text, int fromRow, int order, int lang)
        {
            PostBusy(true, "טוען שיעורים...");
            try
            {
                string json = await khl.GetHomePageShiurimAsync(queryType, parashaId, masechetId, dafNo,
                                                                fromRow, 24, order, lang);
                PostShiurimList(json, fromRow, text);
            }
            catch (Exception ex) { Log.Write("טעינת שיעורים נכשלה: " + ex.Message); PostToast("שגיאה: " + ex.Message); }
            finally { PostBusy(false, null); }
        }

        // התשובה עשויה להיות מערך ישיר או עטופה ב-ShiurimList - שניהם מטופלים כאן.
        private void PostShiurimList(string json, int fromRow, string text)
        {
            string list = json ?? "[]";
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("ShiurimList", out var sl)) list = sl.GetRawText();
            }
            catch (Exception ex) { Log.Write("פענוח רשימת שיעורים נכשל: " + ex.Message); list = "[]"; }

            PostToUi("{\"type\":\"searchShiurim\",\"fromRow\":" + fromRow +
                     ",\"text\":" + JsonSerializer.Serialize(text ?? "") +
                     ",\"shiurim\":" + WithHebrewDates(list) + "}");
        }

        private async Task DoBooksFiltersAsync(string text, int order, int lang, string filters)
        {
            try
            {
                string json = await khl.GetBooksFiltersAsync(text, order, lang, filters);
                PostToUi("{\"type\":\"booksFilters\",\"options\":" + json + "}");
            }
            catch (Exception ex) { Log.Write("טעינת אפשרויות סינון נכשלה: " + ex.Message); }
        }

        // רשימת ספרים/עלונים. הורדתם היא PDF בקישור סטטי, ולכן אין כאן שום שלב אישור.
        private async Task DoBooksAsync(string text, int fromRow, int order = 7, int lang = -1, string filters = null)
        {
            PostBusy(true, "טוען ספרים...");
            try
            {
                string json = await khl.GetBooksAsync(text, fromRow, 24, order, lang, filters);
                PostToUi("{\"type\":\"booksList\",\"fromRow\":" + fromRow +
                         ",\"text\":" + JsonSerializer.Serialize(text ?? "") +
                         ",\"books\":" + WithHebrewDates(json) + "}");
            }
            catch (Exception ex)
            {
                Log.Write("טעינת ספרים נכשלה: " + ex.Message);
                PostToast("שגיאה בטעינת הספרים: " + ex.Message);
            }
            finally { PostBusy(false, null); }
        }

        // פרטי שיעור בודד - משמש לפריטי חיפוש מסוג "שיעור" (SearchItemType=10), שאין להם דף רב.
        private async Task DoShiurDetailsAsync(long fileId)
        {
            PostBusy(true, "טוען שיעור...");
            try
            {
                string json = await khl.GetShiurDetailsAsync(fileId);
                Log.Write("פרטי שיעור(" + fileId + "): " + (json != null ? json.Substring(0, Math.Min(400, json.Length)) : "(ריק)"));
                PostToUi("{\"type\":\"shiurDetails\",\"fileId\":" + fileId + ",\"shiur\":" + WithHebrewDates(json) + "}");
            }
            catch (Exception ex) { Log.Write("טעינת פרטי שיעור נכשלה: " + ex.Message); PostToast("שגיאה בטעינת השיעור: " + ex.Message); }
            finally { PostBusy(false, null); }
        }

        // ----- התחברות -----
        // התחברות עצלה: נקראת ע"י מנהל ההורדות בפעם הראשונה שמורידים. מונעת התחברות כפולה במקביל.
        private async Task<bool> EnsureLoggedInAsync(CancellationToken ct)
        {
            if (khl.IsLoggedIn) return true;
            await loginGate.WaitAsync(ct);
            try
            {
                if (khl.IsLoggedIn) return true;
                // אולי נטען token לפרופיל בינתיים - ננסה שוב לסרוק
                if (await khl.TryLoadStoredTokenAsync()) { MarshalToUi(PostLoginState); return true; }
                // אין token - צריך שהמשתמש יתחבר דרך האתר
                MarshalToUi(() => PostToUi("{\"type\":\"needLogin\"}"));
                return false;
            }
            finally { loginGate.Release(); }
        }

        private async Task DoManualLoginAsync(string u, string p, bool remember)
        {
            PostBusy(true, "מתחבר...");
            try
            {
                Log.Write("התחברות ידנית: משתמש=" + u);
                await khl.LoginAsync(u, p);
                username = u;
                if (remember) password = p;      // סיסמה נשמרת רק אם המשתמש ביקש במפורש
                loginTime = DateTime.Now;
                authToken = khl.Token ?? "";
                SaveSettings();
                Log.Write("התחברות ידנית הצליחה" + (sessionMinutes > 0 ? " (משך חיבור " + sessionMinutes + " דק')" : ""));
                PostToast("התחברת בהצלחה");
                PostLoginState();
                PostToUi("{\"type\":\"loginOk\"}");
            }
            catch (Exception ex) { Log.Write("התחברות ידנית נכשלה: " + ex.Message); PostToast(ex.Message); PostToUi("{\"type\":\"loginFailed\"}"); }
            finally { PostBusy(false, null); }
        }

        private void PostLoginState()
        {
            bool hasCreds = !string.IsNullOrEmpty(username);
            PostToUi(JsonSerializer.Serialize(new
            {
                type = "loginState",
                loggedIn = khl.IsLoggedIn,
                hasCreds,
                username,
                secondsLeft = SessionSecondsLeft()
            }));
        }

        // ----- משך חיבור המשתמש -----
        // sessionMinutes=0 -> ללא הגבלה (מחזיר -1). אחרת: כמה שניות נותרו עד ניתוק אוטומטי.
        private int SessionSecondsLeft()
        {
            if (!khl.IsLoggedIn || sessionMinutes <= 0 || loginTime == DateTime.MinValue) return -1;
            double left = sessionMinutes * 60.0 - (DateTime.Now - loginTime).TotalSeconds;
            return left <= 0 ? 0 : (int)left;
        }

        private void StartSessionTimer()
        {
            if (sessionTimer != null) return;
            sessionTimer = new System.Windows.Forms.Timer { Interval = 15000 };
            sessionTimer.Tick += (s, e) =>
            {
                if (khl != null && khl.IsLoggedIn && sessionMinutes > 0 && SessionSecondsLeft() <= 0)
                    LogoutUser("תם זמן החיבור - יש להתחבר מחדש");
            };
            sessionTimer.Start();
        }

        // ניתוק: מנקה את הטוקן מהזיכרון ומההגדרות, כדי שהמשתמש הבא יידרש להזין פרטים מחדש.
        private void LogoutUser(string reason)
        {
            try
            {
                khl?.SetToken(null);
                authToken = "";
                loginTime = DateTime.MinValue;
                SaveSettings();
                Log.Write("ניתוק משתמש: " + (reason ?? "יזום"));
                PostLoginState();
                if (!string.IsNullOrEmpty(reason)) PostToast(reason);
            }
            catch (Exception ex) { Log.Write("ניתוק נכשל: " + ex.Message); }
        }

        // ----- עדכוני הורדה (מתקבלים מ-thread רקע -> ממרשלים ל-UI) -----
        private void OnDownloadUpdate(DownloadManager.Item item)
        {
            MarshalToUi(() =>
            {
                var msg = JsonSerializer.Serialize(new
                {
                    type = "downloadUpdate",
                    id = item.QueueId,
                    fileId = item.FileId,
                    title = item.Title,
                    rav = item.Rav,
                    quality = item.Quality.ToString(),
                    status = item.Status,
                    percent = item.Percent,
                    bytesDone = item.BytesDone,
                    bytesTotal = item.BytesTotal,
                    bytesPerSec = item.BytesPerSec,
                    canOpen = CanOpenLocally(item),
                    error = item.Error
                });
                PostToUi(msg);
            });
        }

        private void PushDownloadsSnapshot()
        {
            var list = downloads.Items.Select(i => new
            {
                id = i.QueueId, fileId = i.FileId, title = i.Title, rav = i.Rav,
                quality = i.Quality.ToString(), status = i.Status, percent = i.Percent,
                bytesDone = i.BytesDone, bytesTotal = i.BytesTotal, bytesPerSec = i.BytesPerSec,
                canOpen = CanOpenLocally(i), error = i.Error
            });
            PostToUi(JsonSerializer.Serialize(new { type = "downloadsSnapshot", items = list }));
        }

        // ----- יעדי הורדה: המחשב או התקן נשלף (אונקי/נגן/כרטיס זיכרון) -----
        // הסריקה (ובעיקר חלק ה-MTP) חוסמת, ולכן היא רצה ברקע והתוצאה נשלחת לממשק דרך ה-UI thread.
        private void PushTargets()
        {
            string pcFolder = ResolveDownloadFolder();
            _ = Task.Run(() =>
            {
                var list = MediaTargets.List(pcFolder);
                MarshalToUi(() => PushTargetsCore(list));
            });
        }

        private void PushTargetsCore(List<MediaTargets.Target> list)
        {
            // מצב קיוסק: המחשב אינו יעד לגיטימי - מציגים רק התקנים חיצוניים. אם היעד הנוכחי
            // הוא המחשב ויש התקן מחובר, עוברים אליו אוטומטית; אם אין התקן כלל, נשארים על
            // המחשב כיעד "מדומה" וההורדה עצמה תיחסם עד שיחובר התקן.
            if (kioskDeviceOnly)
            {
                var devices = list.Where(t => t.Kind != "pc").ToList();
                if (devices.Count > 0 && !devices.Any(t => string.Equals(t.Id, currentTargetId, StringComparison.OrdinalIgnoreCase)))
                {
                    currentTargetId = devices[0].Id;
                    ApplyDownloadTarget();
                    Log.Write("מצב קיוסק: היעד עבר אוטומטית להתקן " + devices[0].Name);
                }
                PostToUi(JsonSerializer.Serialize(new
                {
                    type = "targets",
                    current = currentTargetId,
                    deviceOnly = true,
                    items = devices.Select(t => new { id = t.Id, name = t.Name, kind = t.Kind, path = t.Path,
                                                      freeText = t.FreeText, freeBytes = t.FreeBytes, totalBytes = t.TotalBytes })
                }));
                return;
            }

            if (!list.Any(t => string.Equals(t.Id, currentTargetId, StringComparison.OrdinalIgnoreCase)))
            {
                // ההתקן שנבחר נותק - חוזרים למחשב ומחילים זאת גם על מנהל ההורדות
                Log.Write("ההתקן " + currentTargetId + " נותק - היעד חוזר למחשב");
                currentTargetId = MediaTargets.PcId;
                ApplyDownloadTarget();
                PostToast("ההתקן נותק - ההורדות יישמרו במחשב");
            }
            PostToUi(JsonSerializer.Serialize(new
            {
                type = "targets",
                current = currentTargetId,
                items = list.Select(t => new { id = t.Id, name = t.Name, kind = t.Kind, path = t.Path,
                                              freeText = t.FreeText, freeBytes = t.FreeBytes, totalBytes = t.TotalBytes })
            }));
        }

        // יעד חיצוני = כל דבר שאינו המחשב עצמו (כונן נשלף או התקן MTP).
        private static bool IsDeviceTarget(string id) =>
            !string.IsNullOrEmpty(id) && !string.Equals(id, MediaTargets.PcId, StringComparison.OrdinalIgnoreCase);

        private void SetTarget(string id)
        {
            var t = MediaTargets.Resolve(id, ResolveDownloadFolder());
            currentTargetId = t.Id;
            ApplyDownloadTarget();
            Log.Write("יעד ההורדות: " + t.Name + " -> " + t.Path);
            PostToast("היעד: " + t.Name);
            PushTargets();
        }

        // מחיל מחדש את היעד הנבחר על מנהל ההורדות (אחרי שינוי הגדרות או ניתוק התקן).
        // ביעד MTP אי אפשר להוריד ישירות מהדפדפן, ולכן ההורדה יורדת לתיקיית ביניים במחשב
        // ומועתקת להתקן בסיום - זו המשמעות של Deliver.
        private void ApplyDownloadTarget()
        {
            var t = MediaTargets.Resolve(currentTargetId, ResolveDownloadFolder());
            currentTargetId = t.Id;

            if (MtpDevices.IsMtpId(t.Id))
            {
                var (device, storage) = MtpDevices.ParseId(t.Id);
                downloads.UpdateConfig(StagingFolder(), maxParallelDownloads);
                downloads.Deliver = (item, localPath) => Task.Run(() =>
                {
                    // שם תיקיית הרב נלקח מהנתיב המקומי, כך שהוא כבר מנוקה מתווים אסורים
                    string ravFolder = Path.GetFileName(Path.GetDirectoryName(localPath) ?? "");
                    Log.Write("מעתיק להתקן " + device + " (" + storage + "): " + Path.GetFileName(localPath));
                    MtpDevices.CopyFile(device, storage,
                        new[] { MediaTargets.DeviceFolderName, ravFolder }, localPath);
                    Log.Write("ההעתקה להתקן הסתיימה: " + Path.GetFileName(localPath));
                });
            }
            else if (t.Kind == "removable")
            {
                // התקן נשלף (דיסק-און-קי, כרטיס זיכרון, נגן במצב אחסון): ההורדה יורדת
                // לתיקיית ביניים במחשב ורק אז מועתקת להתקן. הורדה ישירה להתקן כותבת אלפי
                // מקטעים קטנים - הדפוס שבו זיכרון פלאש זול הכי איטי - ומשאירה קובץ פגום
                // אם ההתקן נשלף באמצע. העתקה היא כתיבה סדרתית אחת, ומה שירד כבר נשמר.
                string dest = t.Path;
                downloads.UpdateConfig(StagingFolder(), maxParallelDownloads);
                downloads.Deliver = (item, localPath) => Task.Run(() =>
                {
                    // שם תיקיית הרב נלקח מהנתיב המקומי, כך שהוא כבר מנוקה מתווים אסורים
                    string ravFolder = Path.GetFileName(Path.GetDirectoryName(localPath) ?? "");
                    string dir = string.IsNullOrEmpty(ravFolder) ? dest : Path.Combine(dest, ravFolder);
                    Directory.CreateDirectory(dir);
                    string target = Path.Combine(dir, Path.GetFileName(localPath));
                    Log.Write("מעתיק להתקן: " + target);
                    File.Copy(localPath, target, true);
                });
            }
            else
            {
                // "המחשב": אין טעם בתיקיית ביניים - היעד ממילא על אותו דיסק
                downloads.Deliver = null;
                downloads.UpdateConfig(t.Path, maxParallelDownloads);
            }
        }

        // ניקוי שאריות מתיקיית הביניים. קובץ נשאר שם רק אם התוכנה נסגרה או קרסה בין
        // סיום ההורדה לסיום ההעתקה, ובלי הניקוי הוא נצבר בדיסק לנצח.
        private static void CleanStaging()
        {
            try
            {
                var dir = new DirectoryInfo(StagingFolder());
                if (!dir.Exists) return;
                int n = 0;
                foreach (var f in dir.GetFiles("*", SearchOption.AllDirectories))
                {
                    if (DateTime.UtcNow - f.LastWriteTimeUtc < TimeSpan.FromHours(12)) continue;
                    try { f.Delete(); n++; } catch { }
                }
                foreach (var d in dir.GetDirectories())
                    try { if (d.GetFiles("*", SearchOption.AllDirectories).Length == 0) d.Delete(true); } catch { }
                if (n > 0) Log.Write("ניקוי תיקיית הביניים: נמחקו " + n + " קבצים שנשארו מהרצה קודמת");
            }
            catch (Exception ex) { Log.Write("ניקוי תיקיית הביניים נכשל: " + ex.Message); }
        }

        // ================= מטמון תמונות הרבנים =================
        // התמונות הן קבצים סטטיים קטנים (200x200, ~40KB) שאינם עוברים ב-API, ולכן אפשר
        // למשוך אותן ב-HttpClient רגיל - Cloudflare אינו מאתגר אותן. השרת מגיש אותן עם
        // max-age של 4 שעות בלבד, כך שבלי מטמון משלנו הדפדפן חוזר לשרת כמה פעמים ביום.
        private const string RavCacheHost = "khl-cache";
        // User-Agent של דפדפן: HttpClient אינו שולח כזה כברירת מחדל, ו-Cloudflare
        // מגיב לפעמים בחסימה לבקשה בלי UA. הכתובת עצמה אינה מאותגרת (קובץ סטטי).
        private static readonly HttpClient imgHttp = CreateImgHttp();
        private static HttpClient CreateImgHttp()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            c.DefaultRequestHeaders.TryAddWithoutValidation("user-agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/151.0.0.0 Safari/537.36");
            return c;
        }
        private static readonly ConcurrentDictionary<string, byte> cachingNow = new ConcurrentDictionary<string, byte>();

        private static string RavCacheFolder() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KolHalashonKiosk", "cache", "ravs");

        // שם הקובץ מגיע מהשרת ומוצג בדף, ולכן הוא נבדק כאן ולא נסמכים על הדף:
        // רק אותיות/ספרות/מקף/נקודה, אחרת אפשר לכתוב לכל מקום בדיסק ("..\..\").
        private static bool IsSafeImageName(string f) =>
            !string.IsNullOrWhiteSpace(f) && f.Length <= 64 &&
            System.Text.RegularExpressions.Regex.IsMatch(f, @"^[A-Za-z0-9_\-]+\.(jpg|jpeg|png|gif)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        private async Task CacheRavImageAsync(string file)
        {
            if (!IsSafeImageName(file)) return;
            string path = Path.Combine(RavCacheFolder(), file);
            if (File.Exists(path)) return;
            if (!cachingNow.TryAdd(file, 0)) return;      // אותה תמונה מופיעה בעשרות שורות
            try
            {
                var bytes = await imgHttp.GetByteArrayAsync(KhlBrowser.SiteOrigin + "imgs/Ravs/" + file);
                if (bytes.Length < 200) return;           // תשובת שגיאה ולא תמונה
                Directory.CreateDirectory(RavCacheFolder());
                // כתיבה לקובץ זמני והחלפה, כדי שהדף לא יקרא קובץ חצי-כתוב
                string tmp = path + ".part";
                await File.WriteAllBytesAsync(tmp, bytes);
                File.Move(tmp, path, true);
            }
            catch (Exception ex) { Log.Write("מטמון תמונה " + file + " נכשל: " + ex.Message); }
            finally { cachingNow.TryRemove(file, out _); }
        }

        private static (int files, long bytes) RavCacheStats()
        {
            try
            {
                var dir = new DirectoryInfo(RavCacheFolder());
                if (!dir.Exists) return (0, 0);
                var files = dir.GetFiles("*", SearchOption.TopDirectoryOnly);
                return (files.Length, files.Sum(f => f.Length));
            }
            catch { return (0, 0); }
        }

        private static int ClearRavCache()
        {
            int n = 0;
            try
            {
                var dir = new DirectoryInfo(RavCacheFolder());
                if (!dir.Exists) return 0;
                foreach (var f in dir.GetFiles()) { try { f.Delete(); n++; } catch { } }
            }
            catch (Exception ex) { Log.Write("ניקוי מטמון התמונות נכשל: " + ex.Message); }
            Log.Write("מטמון התמונות נוקה: " + n + " קבצים");
            return n;
        }

        private static string StagingFolder() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KolHalashonKiosk", "staging");

        // רענון אוטומטי בעת חיבור/ניתוק התקן (הודעת WM_DEVICECHANGE של Windows)
        private const int WM_DEVICECHANGE = 0x0219;
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WM_DEVICECHANGE && web?.CoreWebView2 != null)
            {
                // ההודעה מגיעה לפני שהכונן מוכן לקריאה - המתנה קצרה ואז רענון
                var t = new System.Windows.Forms.Timer { Interval = 1200 };
                t.Tick += (s, e) => { t.Stop(); t.Dispose(); try { PushTargets(); } catch { } };
                t.Start();
            }
        }

        // האם הקובץ שירד עדיין יושב על המחשב. בהורדה להתקן MTP הוא הועתק להתקן
        // ונמחק מתיקיית הביניים, ולכן אין מה לפתוח - והכפתור לא יוצג בממשק.
        private static bool CanOpenLocally(DownloadManager.Item i)
        {
            if (i.Status != "done" || string.IsNullOrEmpty(i.FilePath)) return false;
            try { return File.Exists(i.FilePath); } catch { return false; }
        }

        // פתיחת קובץ שירד בתוכנה שמשויכת לסיומת שלו במחשב (הנגן שכבר מותקן).
        // UseShellExecute הוא מה שמפעיל את השיוך; בלעדיו Windows מנסה להריץ את הקובץ עצמו.
        private void OpenDownloadedFile(long queueId)
        {
            var item = downloads.Items.FirstOrDefault(i => i.QueueId == queueId);
            if (item == null) { PostToast("הפריט אינו ברשימת ההורדות"); return; }
            if (!CanOpenLocally(item))
            {
                PostToast(string.IsNullOrEmpty(item.FilePath) || item.Status != "done"
                    ? "הקובץ עדיין לא הושלם"
                    : "הקובץ אינו על המחשב - הוא הועתק להתקן");
                return;
            }
            try { Process.Start(new ProcessStartInfo(item.FilePath) { UseShellExecute = true }); }
            catch (Exception ex) { PostToast("לא ניתן לפתוח את הקובץ: " + ex.Message); }
        }

        // בירור כתובת המניפסט לנגן הווידאו שבממשק. הבירור עצמו הוא קריאת API
        // (שעוברת ב-Cloudflare דרך הדפדפן הנסתר) ולכן אינו יכול להתבצע בדף.
        private async Task SendVideoUrlAsync(long fileId, bool hd)
        {
            try
            {
                string url = await khl.GetVideoPlaylistUrlAsync(fileId, hd, CancellationToken.None);
                PostToUi(JsonSerializer.Serialize(new { type = "videoUrl", fileId, url }));
            }
            catch (Exception ex)
            {
                Log.Write("בירור זרם וידאו נכשל (" + fileId + "): " + ex.Message);
                PostToUi(JsonSerializer.Serialize(new { type = "videoUrl", fileId, error = ex.Message }));
            }
        }

        // פתיחת התיקייה שאליה ירד הקובץ, עם הקובץ עצמו מסומן בתוכה.
        // /select דורש נתיב מלא בלי מרכאות סביב המתג עצמו - זה התחביר שסייר Windows מצפה לו.
        private void RevealDownloadedFile(long queueId)
        {
            var item = downloads.Items.FirstOrDefault(i => i.QueueId == queueId);
            if (item == null || !CanOpenLocally(item)) { PostToast("הקובץ אינו על המחשב"); return; }
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + item.FilePath + "\"")
                    { UseShellExecute = true });
            }
            catch (Exception ex) { PostToast("לא ניתן לפתוח את התיקייה: " + ex.Message); }
        }

        // בחירת תיקיית ההורדות מתוך חלון "לאן להוריד?" שבממשק. הבחירה נשמרת בהגדרות,
        // וכך היא נשארת גם בהפעלה הבאה - בדיוק כמו בחירה מתוך מסך הניהול.
        private void PickDownloadFolder()
        {
            if (kioskDeviceOnly) { PostToast("במצב קיוסק ההורדה מתבצעת להתקן בלבד"); return; }
            string picked;
            try
            {
                using var dlg = new FolderBrowserDialog { Description = "בחרו תיקיית הורדות" };
                string cur = ResolveDownloadFolder();
                if (Directory.Exists(cur)) dlg.SelectedPath = cur;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                picked = dlg.SelectedPath;
            }
            catch (Exception ex) { PostToast("בחירת התיקייה נכשלה: " + ex.Message); return; }

            // ההורדות נכנסות לתיקיית "קול הלשון" בתוך מה שנבחר, ובתוכה תיקייה לכל רב -
            // בדיוק כמו בהתקן נשלף. כך בחירה ב-C:\Backup נותנת C:\Backup\קול הלשון,
            // והתיקייה שנבחרה לא מתמלאת בקבצים.
            if (!string.Equals(Path.GetFileName(picked.TrimEnd(Path.DirectorySeparatorChar)),
                               MediaTargets.DeviceFolderName, StringComparison.Ordinal))
                picked = Path.Combine(picked, MediaTargets.DeviceFolderName);

            try { Directory.CreateDirectory(picked); }
            catch (Exception ex) { PostToast("לא ניתן לכתוב לתיקייה שנבחרה: " + ex.Message); return; }

            downloadFolder = picked;
            SaveSettings();
            SetTarget(MediaTargets.PcId);   // בחירת תיקייה היא גם בחירה להוריד למחשב
            PushSettingsToUi();
            PushTargets();
            PostToast("תיקיית ההורדות: " + picked);
        }

        private void OpenDownloadFolder()
        {
            try
            {
                string f = ResolveDownloadFolder();
                Directory.CreateDirectory(f);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + f + "\"") { UseShellExecute = true });
            }
            catch (Exception ex) { PostToast("לא ניתן לפתוח את התיקייה: " + ex.Message); }
        }

        // ----- עזרי שליחה לממשק -----
        private void PostToUi(string json)
        {
            try { web.CoreWebView2.PostWebMessageAsJson(json); } catch { }
        }
        private void PostToast(string msg) =>
            PostToUi(JsonSerializer.Serialize(new { type = "toast", message = msg }));
        private void PostBusy(bool on, string msg) =>
            PostToUi(JsonSerializer.Serialize(new { type = "busy", on, message = msg }));

        // כל פעולת UI נדחית לסבב הודעות הבא (BeginInvoke) ולא רצה בתוך המחסנית הנוכחית.
        // קריטי: מטפלי אירועים של WebView2 (WebMessageReceived / WebResourceRequested / DownloadStarting)
        // רצים בתוך קריאת COM מתהליך הדפדפן, והוא ממתין שנחזור. אם בתוכם פותחים חלון, יוצרים WebView2
        // נוסף, מציגים דיאלוג מודאלי או סוגרים חלון - נוצרת נעילה הדדית: ה-UI קופא לחלוטין
        // ("התוכנית אינה מגיבה") עד ש-Windows הורג את התהליך. זו הייתה סיבת הקריסה בכניסה להגדרות.
        private void MarshalToUi(Action a)
        {
            try
            {
                if (IsHandleCreated && !IsDisposed) BeginInvoke(a);
                else a();
            }
            catch { }
        }

        // שם קריא יותר לאותה דחייה, לשימוש מתוך מטפלי אירועים של WebView2.
        private void UiDefer(Action a) => MarshalToUi(a);

        private void PushSettingsToUi()
        {
            PostToUi(JsonSerializer.Serialize(new
            {
                type = "settings",
                title,
                defaultQuality,
                audioBase = khl.AudioUrlBase,
                downloadFolder = ResolveDownloadFolder(),
                kioskFullscreen,
                requireExitPassword,
                kioskDeviceOnly,
                showLogin,
                allowVideo,
                allowHd
            }));
        }

        // ----- הגדרות: טעינה/שמירה (סיסמת החשבון מוצפנת ב-DPAPI) -----
        private void LoadSettings()
        {
            // זריעת ברירת מחדל בהפעלה ראשונה (מהמשאב המוטמע)
            if (!File.Exists(configPath))
            {
                try { File.WriteAllText(configPath, LoadEmbedded("default-config.json"), new UTF8Encoding(false)); }
                catch (Exception ex) { Log.Write("זריעת config.json נכשלה: " + ex.Message); }
            }
            try
            {
                if (!File.Exists(configPath)) return;
                using var d = JsonDocument.Parse(File.ReadAllText(configPath));
                var r = d.RootElement;
                if (r.TryGetProperty("title", out var v) && v.ValueKind == JsonValueKind.String) title = v.GetString();
                if (r.TryGetProperty("username", out var u) && u.ValueKind == JsonValueKind.String) username = u.GetString() ?? "";
                if (r.TryGetProperty("password", out var p) && p.ValueKind == JsonValueKind.String) password = Unprotect(p.GetString() ?? "");
                if (r.TryGetProperty("authToken", out var at) && at.ValueKind == JsonValueKind.String) authToken = Unprotect(at.GetString() ?? "");
                if (r.TryGetProperty("downloadFolder", out var df) && df.ValueKind == JsonValueKind.String) downloadFolder = df.GetString() ?? "";
                if (r.TryGetProperty("defaultQuality", out var dq) && dq.ValueKind == JsonValueKind.String) defaultQuality = dq.GetString() ?? "audio";
                if (r.TryGetProperty("adminPassword", out var ap) && ap.ValueKind == JsonValueKind.String) adminPassword = ap.GetString() ?? "000000";
                if (r.TryGetProperty("exitPassword", out var ep) && ep.ValueKind == JsonValueKind.String) exitPassword = ep.GetString() ?? "000000";
                if (r.TryGetProperty("kioskFullscreen", out var kf)) kioskFullscreen = kf.ValueKind == JsonValueKind.True;
                if (r.TryGetProperty("idleSeconds", out var isec) && isec.TryGetInt32(out var iv) && iv >= 0) idleSeconds = iv;
                if (r.TryGetProperty("baseUrl", out var bu) && bu.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(bu.GetString())) baseUrl = bu.GetString();
                if (r.TryGetProperty("siteKey", out var sk) && sk.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(sk.GetString())) siteKey = sk.GetString();
                if (r.TryGetProperty("maxParallelDownloads", out var mp) && mp.TryGetInt32(out var mpv) && mpv >= 1) maxParallelDownloads = mpv;
                if (r.TryGetProperty("sessionMinutes", out var sm) && sm.TryGetInt32(out var smv) && smv >= 0) sessionMinutes = smv;
                if (r.TryGetProperty("requireExitPassword", out var rep)) requireExitPassword = rep.ValueKind == JsonValueKind.True;
                if (r.TryGetProperty("kioskDeviceOnly", out var kdo)) kioskDeviceOnly = kdo.ValueKind == JsonValueKind.True;
                if (r.TryGetProperty("showLogin", out var sl)) showLogin = sl.ValueKind == JsonValueKind.True;
                if (r.TryGetProperty("allowVideo", out var av)) allowVideo = av.ValueKind != JsonValueKind.False;
                if (r.TryGetProperty("allowHd", out var ah)) allowHd = ah.ValueKind != JsonValueKind.False;
                if (r.TryGetProperty("loginTime", out var lt) && lt.ValueKind == JsonValueKind.String &&
                    DateTime.TryParse(lt.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var ltv)) loginTime = ltv;

                // מיגרציה: config שנשמר בגרסה קודמת מצביע על srv.kolhalashon.com שכבר לא בשימוש -
                // בלי זה ההגדרה השמורה הייתה גוברת על ברירת המחדל החדשה והחיפוש היה ממשיך להיכשל.
                string migrated = KhlBrowser.NormalizeBase(baseUrl);
                if (!string.Equals(migrated, baseUrl, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Write("עדכון כתובת API מ-" + baseUrl + " ל-" + migrated);
                    baseUrl = migrated;
                    SaveSettings();
                }
            }
            catch (Exception ex) { Log.Write("טעינת הגדרות נכשלה: " + ex.Message); }
        }

        private void SaveSettings()
        {
            try
            {
                var obj = new
                {
                    title,
                    username,
                    password = Protect(password),
                    authToken = Protect(authToken),
                    downloadFolder,
                    defaultQuality,
                    adminPassword,
                    exitPassword,
                    kioskFullscreen,
                    idleSeconds,
                    baseUrl,
                    siteKey,
                    maxParallelDownloads,
                    sessionMinutes,
                    requireExitPassword,
                    kioskDeviceOnly,
                    showLogin,
                    allowVideo,
                    allowHd,
                    loginTime = loginTime == DateTime.MinValue ? "" : loginTime.ToString("o")
                };
                File.WriteAllText(configPath,
                    JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
                    new UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Write("שמירת הגדרות נכשלה: " + ex.Message); }
        }

        // הצפנת סיסמת החשבון במנוחה (DPAPI - קשור למשתמש/מכונה). קידומת enc: מסמנת ערך מוצפן.
        private static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            try
            {
                byte[] enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
                return "enc:" + Convert.ToBase64String(enc);
            }
            catch { return plain; }
        }
        private static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return "";
            if (!stored.StartsWith("enc:")) return stored;   // תאימות לאחור: ערך גלוי
            try
            {
                byte[] dec = ProtectedData.Unprotect(Convert.FromBase64String(stored.Substring(4)), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(dec);
            }
            catch { return ""; }
        }

        private static bool VerifyPassword(string entered, string expected) =>
            !string.IsNullOrEmpty(expected) && entered == expected;

        // hls.js שמור כמשאב נפרד ונזרק לדף בזמן הטעינה, כדי ש-kiosk.html יישאר קריא
        // לעריכה במקום להחזיק 300KB של קוד ממוזער. בלעדיו אין נגן וידאו: Chromium
        // אינו יודע לנגן HLS מקורית, וזרמי הווידאו של קול הלשון הם HLS בלבד.
        private string BuildKioskHtml()
        {
            string html = LoadEmbedded("kiosk.html");
            try { return html.Replace("/*__HLS_JS__*/", LoadEmbedded("hls.min.js")); }
            catch (Exception ex) { Log.Write("הטמעת hls.js נכשלה: " + ex.Message); return html; }
        }

        private string LoadEmbedded(string name)
        {
            var asm = Assembly.GetExecutingAssembly();
            string resName = null;
            foreach (var n in asm.GetManifestResourceNames())
                if (n.EndsWith(name, StringComparison.OrdinalIgnoreCase)) { resName = n; break; }
            using var stream = asm.GetManifestResourceStream(resName);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        // ----- חלון התחברות דרך אתר קול הלשון: המשתמש מתחבר כרגיל, והתוכנה לוכדת את ה-token -----
        // זה המנגנון הנכון: האתר לא מתחבר דרך Accounts/UserLogin עם שם משתמש/סיסמה, אלא בזרימה משלו.
        // לכן פותחים את האתר האמיתי (עובר את Cloudflare - דפדפן אמיתי), המשתמש מתחבר, וסורקים את
        // ה-token מ-localStorage. ה-token נשמר בפרופיל, כך שבהפעלות הבאות ההתחברות אוטומטית.
        private async void OpenSiteLogin()
        {
            if (siteLoginForm != null && !siteLoginForm.IsDisposed) { siteLoginForm.Activate(); return; }
            siteLoginForm = new Form
            {
                Text = "התחברות לאתר קול הלשון - התחברו כרגיל וההתחברות תיקלט אוטומטית",
                Width = 1100,
                Height = 840,
                StartPosition = FormStartPosition.CenterScreen,
                BackColor = Color.FromArgb(15, 23, 42)
            };
            siteLoginWeb = new WebView2 { Dock = DockStyle.Fill };
            siteLoginForm.Controls.Add(siteLoginWeb);
            siteLoginForm.FormClosed += (s, e) =>
            {
                try { siteLoginPoll?.Stop(); } catch { }
                siteLoginPoll = null; siteLoginWeb = null; siteLoginForm = null;
            };
            siteLoginForm.Show();
            await siteLoginWeb.EnsureCoreWebView2Async(env);   // אותה סביבה => אותו פרופיל/עוגיות/אחסון כמו apiWeb
            siteLoginWeb.CoreWebView2.Settings.AreDevToolsEnabled = false;

            // יירוט כותרת ה-Authorization: אחרי התחברות, האתר שולח את ה-token של המשתמש בבקשות ל-API.
            // זה המנגנון הכי אמין - לא תלוי במקום שבו האתר שומר את הטוקן (localStorage/cookie/זיכרון).
            siteLoginWeb.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            siteLoginWeb.CoreWebView2.WebResourceRequested += OnLoginResourceRequested;

            // אבחון: עטיפת fetch/XHR בדף ההתחברות, כדי לגלות את נקודת הקצה האמיתית של ההתחברות,
            // את מבנה הבקשה והתשובה, ואת מבנה טופס ההתחברות (לצורך התחברות משדות בממשק).
            siteLoginWeb.CoreWebView2.WebMessageReceived += OnLoginDiagMessage;
            await siteLoginWeb.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(LoginDiagJs);

            siteLoginWeb.CoreWebView2.Navigate(KhlBrowser.SiteOrigin);

            // סקירה תקופתית של האחסון עד שנמצא token
            siteLoginPoll = new System.Windows.Forms.Timer { Interval = 1500 };
            siteLoginPoll.Tick += async (s, e) => await PollSiteLoginAsync();
            siteLoginPoll.Start();
            Log.Write("נפתח חלון התחברות דרך האתר");
        }

        // ----- אבחון התחברות: סקריפט שנטען בכל דף בחלון ההתחברות ועוטף fetch/XHR -----
        // מדווח למארח כל בקשה/תשובה שקשורה להתחברות, וכן את מבנה טופס ההתחברות ברגע שמופיע שדה סיסמה.
        private const string LoginDiagJs = @"
(function(){
  if (window.__khlDiag) return; window.__khlDiag = 1;
  function send(o){ try { o.t='loginDiag'; window.chrome.webview.postMessage(JSON.stringify(o)); } catch(e){} }
  function interesting(u){ u = (u==null?'':''+u); return /kolhalashon/i.test(u) && /(login|account|auth|token|user|otp|sms|code|sign)/i.test(u); }
  function cut(s,n){ s = (s==null?'':''+s); n = n||1500; return s.length>n ? s.slice(0,n)+'…[cut]' : s; }
  var origFetch = window.fetch;
  window.fetch = function(input, init){
    var url = (typeof input==='string') ? input : (input && input.url) || '';
    var m = (init && init.method) || (input && input.method) || 'GET';
    var body = init && init.body;
    var p = origFetch.apply(this, arguments);
    if (interesting(url)) {
      send({k:'req', how:'fetch', m:m, url:url, body:(typeof body==='string')?cut(body):(body?'['+((body.constructor&&body.constructor.name)||'obj')+']':'')});
      try { p.then(function(r){ try { r.clone().text().then(function(t){ send({k:'res', how:'fetch', url:url, status:r.status, body:cut(t)}); }); } catch(e){} }, function(){}); } catch(e){}
    }
    return p;
  };
  var oOpen = XMLHttpRequest.prototype.open, oSend = XMLHttpRequest.prototype.send;
  XMLHttpRequest.prototype.open = function(m,u){ this.__khlM=m; this.__khlU=u; return oOpen.apply(this, arguments); };
  XMLHttpRequest.prototype.send = function(b){
    var self = this;
    if (interesting(self.__khlU)) {
      send({k:'req', how:'xhr', m:self.__khlM, url:self.__khlU, body:(typeof b==='string')?cut(b):''});
      try { self.addEventListener('load', function(){ send({k:'res', how:'xhr', url:self.__khlU, status:self.status, body:cut(self.responseText)}); }); } catch(e){}
    }
    return oSend.apply(this, arguments);
  };
  var dumped = false;
  setInterval(function(){
    if (dumped) return;
    var p = document.querySelector('input[type=password]');
    if (!p) return;
    dumped = true;
    var host = p.closest('form') || p.parentElement && p.parentElement.parentElement || p.parentElement;
    send({k:'form', url:location.href, html:cut(host ? host.outerHTML : p.outerHTML, 4000)});
  }, 1000);
})();
";

        // רישום הודעות האבחון ליומן, עם מיסוך סיסמאות וטוקנים (היומן לא אמור להכיל סודות).
        private void OnLoginDiagMessage(object sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            try
            {
                string raw = args.TryGetWebMessageAsString();
                if (string.IsNullOrEmpty(raw) || raw[0] != '{') return;
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                if (!root.TryGetProperty("t", out var tp) || tp.GetString() != "loginDiag") return;
                string k = root.TryGetProperty("k", out var kp) ? kp.GetString() : "";
                string url = root.TryGetProperty("url", out var up) ? up.GetString() : "";
                string body = root.TryGetProperty("body", out var bp) ? bp.GetString() : "";
                if (k == "form")
                {
                    Log.Write("LOGIN-FORM: " + url + "\r\n" + (root.TryGetProperty("html", out var hp) ? hp.GetString() : ""));
                    return;
                }
                if (k == "req")
                    Log.Write("LOGIN-REQ: " + (root.TryGetProperty("m", out var mp) ? mp.GetString() : "?") + " " + url +
                              "  body=" + MaskSecrets(body));
                else if (k == "res")
                    Log.Write("LOGIN-RES: " + (root.TryGetProperty("status", out var sp) ? sp.GetInt32() : 0) + " " + url +
                              "  body=" + MaskSecrets(body));
            }
            catch { }
        }

        // מסתיר ערכי סיסמה וטוקנים ארוכים מהיומן, ומשאיר את שמות השדות (זה מה שנדרש לאבחון).
        private static string MaskSecrets(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = System.Text.RegularExpressions.Regex.Replace(
                s, "(\"[A-Za-z_]*(?:pass|pwd|secret)[A-Za-z_]*\"\\s*:\\s*\")[^\"]*(\")",
                "$1***$2", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            s = System.Text.RegularExpressions.Regex.Replace(
                s, "((?:pass|password|pwd)=)[^&]*", "$1***",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            s = System.Text.RegularExpressions.Regex.Replace(
                s, "[A-Za-z0-9_\\-]{20,}\\.[A-Za-z0-9_\\-]{20,}\\.[A-Za-z0-9_\\-]{10,}",
                m => "<JWT len=" + m.Value.Length + ">");
            return s;
        }

        // לוכד את ה-token מכותרת Authorization של בקשות שהאתר שולח (Bearer <jwt>).
        // מתעלם מ-authorization-site-key (מפתח האתר, קצר) - רק כותרת authorization האמיתית עם token ארוך.
        private void OnLoginResourceRequested(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            try
            {
                // אבחון: רישום בקשות POST בזמן ההתחברות - לגילוי נקודת הקצה האמיתית של ההתחברות (למימוש התחברות בממשק)
                try
                {
                    if (string.Equals(e.Request.Method, "POST", StringComparison.OrdinalIgnoreCase) &&
                        e.Request.Uri.IndexOf("kolhalashon", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        (e.Request.Uri.IndexOf("ogin", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         e.Request.Uri.IndexOf("account", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         e.Request.Uri.IndexOf("auth", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         e.Request.Uri.IndexOf("token", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         e.Request.Uri.IndexOf("user", StringComparison.OrdinalIgnoreCase) >= 0))
                        Log.Write("LOGIN-POST: " + e.Request.Uri);
                }
                catch { }

                if (khl == null || khl.IsLoggedIn) return;
                string auth = null;
                foreach (var kv in e.Request.Headers)
                    if (string.Equals(kv.Key, "authorization", StringComparison.OrdinalIgnoreCase)) { auth = kv.Value; break; }
                if (string.IsNullOrEmpty(auth)) return;
                string tok = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth.Substring(7).Trim() : auth.Trim();
                if (tok.Length < 20) return;   // מפתח-אתר קצר וכד' - לא token אמיתי
                khl.SetToken(tok);
                authToken = tok; loginTime = DateTime.Now; SaveSettings();   // שמירה מוצפנת להתחברות אוטומטית בפעם הבאה
                Log.Write("נלכד token מכותרת Authorization (אורך " + tok.Length + ")");
                MarshalToUi(() =>
                {
                    try { siteLoginPoll?.Stop(); } catch { }
                    try { siteLoginForm?.Close(); } catch { }
                    PostToast("התחברת בהצלחה");
                    PostLoginState();
                    PostToUi("{\"type\":\"loginOk\"}");
                });
            }
            catch { }
        }

        private async Task PollSiteLoginAsync()
        {
            if (siteLoginWeb == null) return;
            try
            {
                string raw = await siteLoginWeb.CoreWebView2.ExecuteScriptAsync(KhlBrowser.TokenScanJs);
                string tok = DecodeJsString(raw);
                if (!string.IsNullOrEmpty(tok))
                {
                    khl.SetToken(tok);
                    Log.Write("נלכד token מהתחברות באתר (אורך " + tok.Length + ")");
                    siteLoginPoll?.Stop();
                    try { siteLoginForm?.Close(); } catch { }
                    PostToast("התחברת בהצלחה");
                    PostLoginState();
                    PostToUi("{\"type\":\"loginOk\"}");   // kiosk.html ימשיך הורדה שהמתינה
                }
            }
            catch { }
        }

        private async Task<string> RunScriptSafe(WebView2 v, string js)
        {
            try { return DecodeJsString(await v.CoreWebView2.ExecuteScriptAsync(js)); }
            catch { return ""; }
        }

        private static string DecodeJsString(string raw)
        {
            if (string.IsNullOrEmpty(raw) || raw == "null") return "";
            try { return JsonSerializer.Deserialize<string>(raw) ?? ""; }
            catch { return raw; }
        }

        // ----- חלון אימות Cloudflare -----
        // כשה-fetch מהדפדפן הנסתר מחזיר status=0, המשמעות היא שהתשובה נחסמה בדפדפן: Cloudflare
        // מחזיר דף אתגר/חסימה ללא כותרות CORS, ולכן ה-fetch נכשל בלי קוד HTTP. אתגר כזה נפתר רק
        // בחלון גלוי שבו המשתמש יכול לסמן "אני אנושי". החלון מנווט ישירות לכתובת ה-API החסומה,
        // כי עוגיית cf_clearance נקבעת לפי מארח (srv) - וכל החלונות חולקים את אותו פרופיל דפדפן.
        // חשוב: לקרוא רק דרך UiDefer - ראו ההסבר ב-MarshalToUi.
        private async void OpenVerifyWindow(bool userInitiated)
        {
            if (verifyForm != null && !verifyForm.IsDisposed) { verifyForm.Activate(); return; }
            if (khl == null) return;
            if (!userInitiated && (DateTime.Now - lastVerifyOpen).TotalSeconds < 90) return;   // לא לפתוח שוב ושוב
            lastVerifyOpen = DateTime.Now;
            Log.Write("פותח חלון אימות Cloudflare (יזום ע\"י משתמש=" + userInitiated + ")");
            try
            {
                verifyForm = new Form
                {
                    Text = "אימות אבטחה - קול הלשון",
                    Width = 980,
                    Height = 800,
                    StartPosition = FormStartPosition.CenterScreen,
                    BackColor = Color.FromArgb(15, 23, 42)
                };

                var top = new Panel { Dock = DockStyle.Top, Height = 92, BackColor = Color.FromArgb(15, 23, 42) };
                var lbl = new Label
                {
                    Left = 14, Top = 10, Width = 580, Height = 40,
                    ForeColor = Color.White, RightToLeft = RightToLeft.Yes,
                    Text = "אתר קול הלשון דורש אימות שאינכם רובוט.\nאם מופיעה למטה תיבת סימון - סמנו אותה. החלון ייסגר לבד בסיום."
                };
                verifyStatus = new Label
                {
                    Left = 14, Top = 56, Width = 580, Height = 22,
                    ForeColor = Color.FromArgb(148, 163, 184), RightToLeft = RightToLeft.Yes,
                    Text = "בודק חיבור..."
                };
                var btnClose = new Button { Text = "סגור", Left = 860, Top = 50, Width = 90, Height = 30, Anchor = AnchorStyles.Top | AnchorStyles.Right };
                var btnRetry = new Button { Text = "בדוק שוב", Left = 752, Top = 50, Width = 100, Height = 30, Anchor = AnchorStyles.Top | AnchorStyles.Right };
                var btnSite = new Button { Text = "פתח את האתר", Left = 614, Top = 50, Width = 130, Height = 30, Anchor = AnchorStyles.Top | AnchorStyles.Right };
                btnClose.Click += (s, e) => { try { verifyForm?.Close(); } catch { } };
                btnRetry.Click += async (s, e) =>
                {
                    try { verifyWeb?.CoreWebView2?.Navigate(khl.ProbeUrl); } catch { }
                    await VerifyTickAsync();
                };
                btnSite.Click += (s, e) => { try { verifyWeb?.CoreWebView2?.Navigate(KhlBrowser.SiteOrigin); } catch { } };
                top.Controls.AddRange(new Control[] { lbl, verifyStatus, btnSite, btnRetry, btnClose });

                verifyWeb = new WebView2 { Dock = DockStyle.Fill };
                verifyForm.Controls.Add(verifyWeb);   // Fill נוסף ראשון, Top אחריו - כך הפאנל נשאר למעלה
                verifyForm.Controls.Add(top);
                verifyForm.FormClosed += (s, e) =>
                {
                    try { verifyPoll?.Stop(); } catch { }
                    verifyPoll = null; verifyWeb = null; verifyForm = null; verifyStatus = null;
                };

                verifyForm.Show();
                await verifyWeb.EnsureCoreWebView2Async(env);   // אותה סביבה => אותו פרופיל ואותן עוגיות כמו הדפדפן הנסתר
                if (verifyWeb == null || verifyWeb.CoreWebView2 == null) return;
                verifyWeb.CoreWebView2.Settings.AreDevToolsEnabled = false;
                verifyWeb.CoreWebView2.Navigate(khl.ProbeUrl);

                verifyPoll = new System.Windows.Forms.Timer { Interval = 2500 };
                verifyPoll.Tick += async (s, e) => await VerifyTickAsync();
                verifyPoll.Start();
            }
            catch (Exception ex)
            {
                Log.Write("פתיחת חלון האימות נכשלה: " + ex);
                try { verifyForm?.Close(); } catch { }
                verifyForm = null; verifyWeb = null;
            }
        }

        // דגימה תקופתית: הבדיקה האמיתית היא קריאת ה-API מהדפדפן הנסתר - ברגע שהיא מחזירה 200, האימות עבר.
        private async Task VerifyTickAsync()
        {
            if (verifyBusy || khl == null) return;
            verifyBusy = true;
            try
            {
                int st = await khl.ProbeOnceAsync();
                if (st == 200)
                {
                    try { verifyPoll?.Stop(); } catch { }
                    Log.Write("אימות Cloudflare הושלם - החיבור פעיל");
                    try { verifyForm?.Close(); } catch { }
                    await khl.ReloadSiteAsync();
                    PostToast("החיבור לקול הלשון שוחזר - אפשר לחפש");
                    PostLoginState();
                }
                else if (verifyStatus != null && !verifyStatus.IsDisposed)
                {
                    verifyStatus.Text = st == 0
                        ? "עדיין חסום - השלימו את האימות בחלון שלמטה"
                        : "תשובת שרת: " + st;
                }
            }
            catch (Exception ex) { Log.Write("בדיקת אימות נכשלה: " + ex.Message); }
            finally { verifyBusy = false; }
        }

        // ----- חלון ניהול (נעול בסיסמה, נפתח מתוך admin.html עצמו) -----
        // חשוב: לקרוא רק דרך UiDefer (לא ישירות מתוך מטפל אירוע של WebView2) - ראו ההסבר ב-MarshalToUi.
        private async void OpenAdminWindow()
        {
            if (adminForm != null && !adminForm.IsDisposed) { adminForm.Activate(); return; }
            Log.Write("פותח חלון ניהול...");
            try
            {
            adminCanClose = false;
            adminForm = new Form
            {
                Text = "ניהול - קול הלשון",
                Width = 820,
                Height = 820,
                StartPosition = FormStartPosition.CenterScreen,
                BackColor = Color.FromArgb(15, 23, 42)
            };
            adminWeb = new WebView2 { Dock = DockStyle.Fill };
            adminForm.Controls.Add(adminWeb);
            adminForm.FormClosing += (s, e) =>
            {
                if (!adminCanClose)
                {
                    e.Cancel = true;
                    try { adminWeb?.CoreWebView2?.PostWebMessageAsJson("{\"type\":\"requestClose\"}"); } catch { }
                }
            };
            adminForm.FormClosed += (s, e) => { adminWeb = null; adminForm = null; };
            adminForm.Show();
            await adminWeb.EnsureCoreWebView2Async(env);
            if (adminWeb == null || adminWeb.CoreWebView2 == null) { Log.Write("חלון הניהול נסגר לפני סיום האתחול"); return; }
            adminWeb.CoreWebView2.Settings.AreDevToolsEnabled = false;
            adminWeb.CoreWebView2.WebMessageReceived += OnAdminMessage;
            adminWeb.CoreWebView2.NavigationCompleted += (s, e) => { try { adminForm?.Activate(); adminWeb?.Focus(); } catch { } };
            adminWeb.CoreWebView2.NavigateToString(LoadEmbedded("admin.html"));
            Log.Write("חלון הניהול נפתח");
            }
            catch (Exception ex)
            {
                Log.Write("פתיחת חלון הניהול נכשלה: " + ex);
                try { adminCanClose = true; adminForm?.Close(); } catch { }
                adminForm = null; adminWeb = null;
                PostToast("לא ניתן לפתוח את חלון הניהול: " + ex.Message);
            }
        }

        private void OnAdminMessage(object sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            JsonDocument doc = null;
            try
            {
                doc = JsonDocument.Parse(args.WebMessageAsJson);
                var root = doc.RootElement;
                string type = root.GetProperty("type").GetString();

                if (type == "adminGetData")
                {
                    // שולח את ההגדרות הנוכחיות (בלי לחשוף את סיסמת החשבון בטקסט - רק אם קיימת)
                    var data = new
                    {
                        type = "adminData",
                        title,
                        username,
                        hasPassword = !string.IsNullOrEmpty(password),
                        downloadFolder,
                        defaultQuality,
                        adminPassword,
                        exitPassword,
                        kioskFullscreen,
                        idleSeconds,
                        baseUrl,
                        siteKey,
                        maxParallelDownloads,
                        sessionMinutes,
                        cacheFiles = RavCacheStats().files,
                        cacheBytes = RavCacheStats().bytes,
                        requireExitPassword,
                        kioskDeviceOnly,
                        showLogin,
                        allowVideo,
                        allowHd
                    };
                    adminWeb.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(data));
                }
                else if (type == "adminVerify")
                {
                    string p = root.TryGetProperty("password", out var pw) ? pw.GetString() : "";
                    bool ok = VerifyPassword(p, adminPassword);
                    adminWeb.CoreWebView2.PostWebMessageAsJson(
                        "{\"type\":\"adminVerifyResult\",\"ok\":" + (ok ? "true" : "false") + "}");
                }
                else if (type == "adminSave")
                {
                    string prevUsername = username;
                    if (root.TryGetProperty("title", out var t)) title = t.GetString() ?? title;
                    if (root.TryGetProperty("username", out var u)) username = u.GetString() ?? "";
                    // סיסמה מתעדכנת רק אם נשלחה חדשה (שדה לא ריק)
                    if (root.TryGetProperty("password", out var p) && p.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(p.GetString()))
                        password = p.GetString();
                    if (root.TryGetProperty("downloadFolder", out var df)) downloadFolder = df.GetString() ?? "";
                    if (root.TryGetProperty("defaultQuality", out var dq)) defaultQuality = dq.GetString() ?? "audio";
                    if (root.TryGetProperty("adminPassword", out var ap) && !string.IsNullOrEmpty(ap.GetString())) adminPassword = ap.GetString();
                    if (root.TryGetProperty("exitPassword", out var ep) && !string.IsNullOrEmpty(ep.GetString())) exitPassword = ep.GetString();
                    if (root.TryGetProperty("kioskFullscreen", out var kf)) kioskFullscreen = kf.ValueKind == JsonValueKind.True;
                    if (root.TryGetProperty("idleSeconds", out var isec) && isec.TryGetInt32(out var iv) && iv >= 0) idleSeconds = iv;
                    if (root.TryGetProperty("siteKey", out var sk) && !string.IsNullOrWhiteSpace(sk.GetString())) siteKey = sk.GetString();
                    if (root.TryGetProperty("baseUrl", out var bu) && !string.IsNullOrWhiteSpace(bu.GetString())) baseUrl = bu.GetString();
                    if (root.TryGetProperty("maxParallelDownloads", out var mp) && mp.TryGetInt32(out var mpv) && mpv >= 1) maxParallelDownloads = mpv;
                    if (root.TryGetProperty("sessionMinutes", out var sm) && sm.TryGetInt32(out var smv) && smv >= 0) sessionMinutes = smv;
                    if (root.TryGetProperty("requireExitPassword", out var rep)) requireExitPassword = rep.ValueKind == JsonValueKind.True;
                    if (root.TryGetProperty("kioskDeviceOnly", out var kdo)) kioskDeviceOnly = kdo.ValueKind == JsonValueKind.True;
                    if (root.TryGetProperty("showLogin", out var slg)) showLogin = slg.ValueKind == JsonValueKind.True;
                    if (root.TryGetProperty("allowVideo", out var av)) allowVideo = av.ValueKind == JsonValueKind.True;
                    if (root.TryGetProperty("allowHd", out var ah)) allowHd = ah.ValueKind == JsonValueKind.True;
                    if (!allowVideo && !allowHd && defaultQuality != "audio") defaultQuality = "audio";

                    SaveSettings();

                    // החלת השינויים חיה. שמירת הגדרות אינה מנתקת את המשתמש - רק שינוי שם המשתמש
                    // מנתק, כדי שההתחברות הבאה תתבצע עם החשבון החדש.
                    khl.UpdateConfig(baseUrl, siteKey);
                    if (!string.Equals(prevUsername, username, StringComparison.Ordinal)) LogoutUser(null);
                    ApplyDownloadTarget();   // שומר על היעד הנבחר (התקן נשלף) גם אחרי שינוי הגדרות
                    PushSettingsToUi();
                    PostLoginState();
                    PushTargets();

                    adminWeb.CoreWebView2.PostWebMessageAsJson("{\"type\":\"adminSaved\"}");
                }
                else if (type == "adminClearCache")
                {
                    int n = ClearRavCache();
                    var st = RavCacheStats();
                    adminWeb.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new
                    {
                        type = "cacheInfo", cleared = n, cacheFiles = st.files, cacheBytes = st.bytes
                    }));
                }
                else if (type == "adminPickFolder")
                {
                    // דיאלוג מודאלי חייב לרוץ מחוץ למטפל האירוע של WebView2 (אחרת נעילה הדדית)
                    UiDefer(PickFolder);
                }
                else if (type == "closeAdmin" || type == "allowClose")
                {
                    // סגירת החלון (ושחרור ה-WebView2 שלו) מתוך מטפל האירוע של אותו WebView2 - נדחית גם היא
                    UiDefer(() => { adminCanClose = true; try { adminForm?.Close(); } catch { } });
                }
            }
            catch (Exception ex) { Log.Write("OnAdminMessage שגיאה: " + ex.Message); }
            finally { doc?.Dispose(); }
        }

        private void PickFolder()
        {
            try
            {
                using var dlg = new FolderBrowserDialog { Description = "בחרו תיקיית הורדות" };
                if (!string.IsNullOrWhiteSpace(downloadFolder) && Directory.Exists(downloadFolder))
                    dlg.SelectedPath = downloadFolder;
                if (dlg.ShowDialog(adminForm) == DialogResult.OK)
                    adminWeb.CoreWebView2.PostWebMessageAsJson(
                        JsonSerializer.Serialize(new { type = "folderPicked", path = dlg.SelectedPath }));
            }
            catch (Exception ex) { Log.Write("PickFolder נכשל: " + ex.Message); }
        }
    }
}
