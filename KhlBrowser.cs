using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace KolHalashonKiosk
{
    // שכבת התקשורת מול קול הלשון דרך WebView2 אמיתי.
    //
    // למה לא HttpClient רגיל: האתר מוגן ע"י Cloudflare שמציב "אתגר" (challenge) לכל בקשה שלא מגיעה
    // מדפדפן אמיתי (cf-mitigated=challenge -> 403). דפדפן Chromium אמיתי פותר את האתגר אוטומטית
    // (ציון בוט נמוך + עוגיית cf_clearance). לכן כל הבקשות רצות כאן דרך fetch() בתוך WebView2 נסתר
    // שטעון על מקור האתר - בדיוק כמו שהאתר עצמו פונה ל-API.
    //
    // עדכון 18/08/2026: האתר עבר מיגרציה. www2.kolhalashon.com מפנה (301) ל-www.kolhalashon.com,
    // וה-API עבר מ-srv.kolhalashon.com ל-https://www.kolhalashon.com/api/ - כלומר אותו מקור כמו הדף.
    // זה קריטי: פנייה ל-srv מדף של www היא חוצת-מקור, Cloudflare מחזיר לה דף אתגר ללא כותרות CORS,
    // וה-fetch נכשל עם "TypeError: Failed to fetch" (status=0) בלי שום קוד HTTP. מקור זהה פותר זאת.
    // (הכותרת Authorization-Site-Key היא סתם "Bearer " + מחרוזת אקראית שהאתר מייצר - כל ערך מתקבל.)
    //
    // התקשורת מול ה-JS נעשית ב-RPC מעל postMessage (ExecuteScriptAsync לא ממתין ל-Promise).
    // ההורדות עצמן משתמשות במנגנון ההורדה המובנה של WebView2 (DownloadStarting) - יעיל וזורם לדיסק.
    public class KhlBrowser
    {
        public enum Quality { Audio = 1, Video = 2, HdVideo = 3, Pdf = 4 }   // Pdf אינו פרמטר של ה-API - הורדתו היא קישור סטטי

        private readonly WebView2 view;             // ה-WebView2 הנסתר (כבר עבר EnsureCoreWebView2Async)
        private CoreWebView2 core;
        private string baseUrl;
        private string siteKey;
        private string token;

        public const string SiteOrigin = "https://www.kolhalashon.com/";
        public const string DefaultApiBase = "https://www.kolhalashon.com/api/";
        // קריאת API קלה ופומבית המשמשת כ"דופק": אם היא מחזירה 200 - הדפדפן עבר את Cloudflare ומוכן לעבודה.
        private const string ProbePath = "TblShiurimLists/WebSite_GetShiurDetails/810186";

        private volatile bool ready;
        public bool IsReady => ready;
        // הכתובת שאליה מנווטים בחלון האימות הידני (אותו מארח שחוסם - כדי לקבל עוגיית cf_clearance עבורו)
        public string ProbeUrl => baseUrl + ProbePath;

        private int reqSeq = 0;
        private readonly ConcurrentDictionary<int, TaskCompletionSource<(int status, string body)>> pending
            = new ConcurrentDictionary<int, TaskCompletionSource<(int, string)>>();

        // הורדות פעילות לפי fileId -> בקשה (נצרך ע"י מטפל DownloadStarting)
        private class DlReq
        {
            public string PartPath;
            public string FinalPath;
            public Action<long, long> Progress;
            public TaskCompletionSource<bool> Tcs;
            public TaskCompletionSource<bool> Started;   // נדלק כשהדפדפן אכן פתח את ההורדה
            public CancellationToken Ct;
            public CoreWebView2DownloadOperation Op;
        }
        private readonly ConcurrentDictionary<long, DlReq> downloads = new ConcurrentDictionary<long, DlReq>();

        // דפדפן נסתר נפרד להורדות בלבד (ראו AttachDownloadView)
        private CoreWebView2 dlCore;
        private readonly SemaphoreSlim triggerGate = new SemaphoreSlim(1, 1);

        public bool IsLoggedIn => !string.IsNullOrEmpty(token);
        public string Token => token;
        public void SetToken(string t) => token = t;

        public KhlBrowser(WebView2 view, string baseUrl, string siteKey)
        {
            this.view = view;
            this.baseUrl = NormalizeBase(baseUrl);
            this.siteKey = string.IsNullOrWhiteSpace(siteKey) ? "8ea2pe8" : siteKey.Trim();
        }

        public void UpdateConfig(string newBaseUrl, string newSiteKey)
        {
            if (!string.IsNullOrWhiteSpace(newBaseUrl)) baseUrl = NormalizeBase(newBaseUrl);
            if (!string.IsNullOrWhiteSpace(newSiteKey)) siteKey = newSiteKey.Trim();
        }

        // מנרמל את כתובת ה-API, וגם מסיט אוטומטית מהמארח הישן (srv) שכבר לא בשימוש - כדי שהגדרה
        // שמורה מגרסה קודמת לא תשבית את התוכנה.
        public static string NormalizeBase(string b)
        {
            if (string.IsNullOrWhiteSpace(b)) return DefaultApiBase;
            b = b.Trim();
            if (b.IndexOf("srv.kolhalashon.com", StringComparison.OrdinalIgnoreCase) >= 0 ||
                b.IndexOf("www2.kolhalashon.com", StringComparison.OrdinalIgnoreCase) >= 0)
                return DefaultApiBase;
            if (!b.EndsWith("/")) b += "/";
            return b;
        }

        // ----- אתחול: הזרקת עוזר ה-fetch, מאזינים, ניווט ל-www2 והמתנה עד ש-Cloudflare נפתר -----
        // מחזיר true אם המערכת מוכנה (עברה את Cloudflare); false => על המארח לפתוח חלון אימות ידני.
        public async Task<bool> InitAsync()
        {
            core = view.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;

            core.WebMessageReceived += OnApiMessage;
            if (dlCore == null) core.DownloadStarting += OnDownloadStarting;   // גיבוי אם לא חובר דפדפן הורדות

            // עוזר קבוע שמוזרק לכל דף: מריץ fetch ומחזיר את התוצאה דרך postMessage.
            await core.AddScriptToExecuteOnDocumentCreatedAsync(HelperScript);

            var navDone = new TaskCompletionSource<bool>();
            void OnNav(object s, CoreWebView2NavigationCompletedEventArgs e)
            {
                core.NavigationCompleted -= OnNav;
                Log.Write("ניווט ל-" + SiteOrigin + ": success=" + e.IsSuccess + (e.IsSuccess ? "" : "  שגיאה=" + e.WebErrorStatus));
                navDone.TrySetResult(e.IsSuccess);
            }
            core.NavigationCompleted += OnNav;
            core.Navigate(SiteOrigin);
            try { await WithTimeout(navDone.Task, 30000); } catch { Log.Write("ניווט לאתר: תם הזמן"); }

            await LogPageInfoAsync();
            bool ok = await WaitUntilReadyAsync(12000);
            if (!ok) ok = await TryAutoClearanceAsync();   // אתגר Cloudflare - קודם ננסה לפתור לבד

            // התחברות אוטומטית מהפרופיל (אם המשתמש כבר התחבר דרך האתר בעבר)
            await TryLoadStoredTokenAsync();
            return ok;
        }

        // רישום מצב הדף הנוכחי בדפדפן הנסתר - מזהה מיד דף אתגר של Cloudflare ("Just a moment...")
        // או דף שגיאה, במקום להשאיר אותנו עם "status=0" בלי הסבר.
        public async Task LogPageInfoAsync()
        {
            try
            {
                string info = await RunScriptAsync(
                    "(location.href||'')+'  |  '+(document.title||'')+'  |  '+" +
                    "(((document.body&&document.body.innerText)||'').replace(/\\s+/g,' ').slice(0,160))");
                Log.Write("דף בדפדפן הנסתר: " + info);
            }
            catch (Exception ex) { Log.Write("קריאת פרטי הדף נכשלה: " + ex.Message); }
        }

        private static string Head(string body, int n = 140)
        {
            if (string.IsNullOrEmpty(body)) return "";
            body = body.Replace("\r", " ").Replace("\n", " ");
            return "body=" + (body.Length > n ? body.Substring(0, n) + "…" : body);
        }

        // ----- דפדפן נסתר ייעודי להורדות -----
        // למה לא להוריד מתוך דף האתר: האתר החדש הוא אפליקציית Angular שמיירטת לחיצות על קישורים
        // (וגם עלול לרשום service worker), ולכן טריק "צור <a download> ולחץ עליו" הפסיק לעבוד -
        // ההורדה פשוט לא התחילה. כאן מנווטים ניווט עליון (top-level) לכתובת הקובץ בדפדפן ריק משלנו:
        // השרת מחזיר Content-Disposition: attachment, Chromium הופך את הניווט להורדה, והדף נשאר ריק.
        // ניווט עליון גם מבטיח שכל העוגיות (כולל cf_clearance) נשלחות.
        public void AttachDownloadView(WebView2 v)
        {
            dlCore = v.CoreWebView2;
            dlCore.Settings.AreDevToolsEnabled = false;
            dlCore.Settings.AreDefaultContextMenusEnabled = false;
            dlCore.DownloadStarting += OnDownloadStarting;

            // אישור אוטומטי של "הורדת קבצים מרובים" - אחרת Chromium מציג חלון אישור בהורדה שנייה ואילך.
            dlCore.PermissionRequested += (s, e) =>
            {
                try
                {
                    if (e.PermissionKind == CoreWebView2PermissionKind.MultipleAutomaticDownloads)
                        e.State = CoreWebView2PermissionState.Allow;
                }
                catch { }
            };

            // אם השרת החזיר דף במקום קובץ, הניווט יסתיים בהצלחה ונרשום מה הוא החזיר - זה האבחון
            // היחיד שמסביר "ההורדה לא התחילה" (למשל דף אתגר של Cloudflare או הודעת שגיאה).
            dlCore.NavigationCompleted += async (s, e) =>
            {
                try
                {
                    string src = dlCore.Source ?? "";
                    if (src.IndexOf("GetFileDownload", StringComparison.OrdinalIgnoreCase) < 0) return;
                    string info = await RunScriptOnAsync(dlCore,
                        "(document.title||'')+' | '+(((document.body&&document.body.innerText)||'').replace(/\\s+/g,' ').slice(0,200))");
                    Log.Write("הורדה: השרת החזיר דף במקום קובץ -> " + info);
                }
                catch { }
            };

            dlCore.Navigate("about:blank");
        }

        private const string HelperScript = @"
window.__khl = window.__khl || {};
// שימו לב: אין להוסיף כאן כותרת accept! ראו ההסבר ב-ApiFetchOnceAsync.
// גוף JSON נשלח כ-Blob עם type, כדי ש-Chromium יקבע את Content-Type בעצמו במקום שנגדיר כותרת ידנית.
window.__khl.fetch = function(reqId, method, url, body, headers, ctype){
  try{
    var b = (body===null) ? undefined : (ctype ? new Blob([body], {type: ctype}) : body);
    fetch(url, {method:method, headers:headers, body:b, credentials:'include', cache:'no-store'})
      .then(function(r){ return r.text().then(function(t){
          window.chrome.webview.postMessage(JSON.stringify({t:'apiResult', reqId:reqId, status:r.status, body:t}));
        }); })
      .catch(function(e){
          window.chrome.webview.postMessage(JSON.stringify({t:'apiResult', reqId:reqId, status:0, body:String(e)}));
      });
  }catch(e){
    window.chrome.webview.postMessage(JSON.stringify({t:'apiResult', reqId:reqId, status:0, body:String(e)}));
  }
};
// אבחון: מריץ כמה וריאנטים של אותה בקשה ומדווח את קוד התשובה של כל אחד - כדי לזהות מה בדיוק
// גורם ל-Cloudflare לחסום (כותרות? credentials? XHR מול fetch?).
window.__khl.diag = function(reqId, url, siteKey, url2){
  var out=[];
  var rnd = Math.random().toString(36).slice(2,9);
  var tests = [
    ['ריק', url, {}],
    ['acceptStar', url, {headers:{'accept':'*/*'}}],
    ['acceptJson', url, {headers:{'accept':'application/json'}}],
    ['acceptFull', url, {headers:{'accept':'application/json, text/plain, */*'}}],
    ['skOnly', url, {headers:{'authorization-site-key':'Bearer '+rnd}}],
    ['authOnly', url, {headers:{'authorization':'Bearer '+rnd}}],
    ['skPlusAuth', url, {headers:{'authorization-site-key':'Bearer '+rnd,'authorization':'Bearer '+rnd}}],
    ['חיפוש-ריק', url2, {}],
    ['חיפוש-sk', url2, {headers:{'authorization-site-key':'Bearer '+rnd}}]
  ];
  var i=0;
  function done(){ try{ window.chrome.webview.postMessage(JSON.stringify({t:'apiResult', reqId:reqId, status:1, body:out.join('  |  ')})); }catch(e){} }
  function next(){
    if(i>=tests.length){
      var x=new XMLHttpRequest();
      try{
        x.open('GET', url, true);
        x.onload=function(){ out.push('xhr='+x.status); done(); };
        x.onerror=function(){ out.push('xhr=ERR'); done(); };
        x.send();
      }catch(e){ out.push('xhr=EX'); done(); }
      return;
    }
    var n=tests[i][0], u=tests[i][1], o=tests[i][2]; i++;
    fetch(u, o).then(function(r){ out.push(n+'='+r.status); next(); })
               .catch(function(e){ out.push(n+'=ERR('+String(e).slice(0,30)+')'); next(); });
  }
  next();
};
";

        // סריקת ה-token מאחסון הדפדפן (localStorage/sessionStorage) של אתר קול הלשון לאחר התחברות.
        // מזהה מחרוזת בפורמט JWT (שלושה חלקים מופרדים בנקודה) - כך זה עובד ללא תלות בשם המפתח המדויק.
        public const string TokenScanJs = @"(function(){
try{
 var pat=/^[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{5,}$/;
 function scan(store){
   for(var i=0;i<store.length;i++){
     var k=store.key(i); var v=store.getItem(k); if(!v) continue;
     var s=v; if(s.length>1 && s.charAt(0)=='\""' && s.charAt(s.length-1)=='\""') s=s.slice(1,-1);
     if(pat.test(s)) return s;
     try{var o=JSON.parse(v); for(var kk in o){var vv=o[kk]; if(typeof vv==='string' && pat.test(vv)) return vv;}}catch(e){}
   }
   return '';
 }
 var r=scan(localStorage); if(r) return r;
 return scan(sessionStorage);
}catch(e){ return ''; }
})()";

        // רישום אבחון: כל מפתחות האחסון + ראשי-ערכים (בלי לחשוף טוקן מלא) - לזיהוי מבנה ההתחברות של האתר.
        public const string StorageDumpJs = @"(function(){
try{
 var out=[];
 for(var i=0;i<localStorage.length;i++){var k=localStorage.key(i);var v=localStorage.getItem(k)||'';out.push('L:'+k+'='+v.slice(0,50));}
 for(var j=0;j<sessionStorage.length;j++){var k2=sessionStorage.key(j);var v2=sessionStorage.getItem(k2)||'';out.push('S:'+k2+'='+v2.slice(0,50));}
 return out.join(' | ');
}catch(e){ return String(e); }
})()";

        // ניסיון בדיקה בודד. מחזיר את קוד הסטטוס (0 = ה-fetch נכשל ברמת הרשת, -1 = חריגה).
        // status=0 פירושו כמעט תמיד שהתשובה נחסמה בדפדפן (אתגר Cloudflare מוחזר בלי כותרות CORS).
        public async Task<int> ProbeOnceAsync()
        {
            try
            {
                var (status, body) = await ApiFetchOnceAsync("GET", ProbePath, null, false, 8000);
                if (status != 200) Log.Write("בדיקת מוכנות: status=" + status + "  " + Head(body));
                return status;
            }
            catch (Exception ex) { Log.Write("בדיקת מוכנות נכשלה: " + ex.Message); return -1; }
        }

        // ניווט מחדש לדף האתר (לרענון ההקשר/העוגיות לאחר אימות)
        public Task<bool> ReloadSiteAsync() => NavigateAsync(SiteOrigin);

        private Task<bool> NavigateAsync(string url)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = Task.Delay(30000).ContinueWith(_ => tcs.TrySetResult(false));
            RunOnUi(() =>
            {
                try
                {
                    void OnNav(object s, CoreWebView2NavigationCompletedEventArgs e)
                    {
                        core.NavigationCompleted -= OnNav;
                        Log.Write("ניווט ל-" + url + ": success=" + e.IsSuccess);
                        tcs.TrySetResult(e.IsSuccess);
                    }
                    core.NavigationCompleted += OnNav;
                    core.Navigate(url);
                }
                catch (Exception ex) { Log.Write("ניווט נכשל: " + ex.Message); tcs.TrySetResult(false); }
            });
            return tcs.Task;
        }

        // ניסיון אוטומטי לעבור את אתגר Cloudflare, לפני שמטרידים את המשתמש בחלון אימות.
        // Cloudflare מציג את דף האתגר רק בניווט עליון (מסמך), לא בקריאת XHR: בניווט כזה סקריפט
        // האתגר רץ בדף, ובסיומו נקבעת עוגיית cf_clearance למארח - וממנה גם קריאות ה-API עוברות.
        public async Task<bool> TryAutoClearanceAsync()
        {
            try
            {
                Log.Write("ניסיון אוטומטי לעבור את אתגר Cloudflare (ניווט לכתובת ה-API)...");
                await NavigateAsync(ProbeUrl);
                for (int i = 0; i < 12; i++)
                {
                    await Task.Delay(1500);
                    string title = await RunScriptAsync("(document.title||'')");
                    if (!IsChallengeTitle(title)) break;
                }
                await LogPageInfoAsync();
                await ReloadSiteAsync();
                int st = await ProbeOnceAsync();
                Log.Write("לאחר ניסיון האימות האוטומטי: status=" + st);
                if (st != 200) Log.Write("אבחון וריאנטים: " + await DiagnoseFetchAsync());
                return st == 200;
            }
            catch (Exception ex) { Log.Write("ניסיון האימות האוטומטי נכשל: " + ex.Message); return false; }
        }

        // מריץ את מערך וריאנטי הבקשה ומחזיר סיכום קריא ליומן.
        public Task<string> DiagnoseFetchAsync()
        {
            int id = Interlocked.Increment(ref reqSeq);
            var tcs = new TaskCompletionSource<(int, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[id] = tcs;
            string searchUrl = baseUrl + "Search/WebSite_GetSearchItems/" + Uri.EscapeDataString("קניבסקי") + "/-1/1/4";
            string js = "window.__khl.diag(" + id + "," + J(ProbeUrl) + "," + J(siteKey) + "," + J(searchUrl) + ");";
            RunOnUi(() =>
            {
                try { _ = core.ExecuteScriptAsync(js); }
                catch (Exception ex) { if (pending.TryRemove(id, out var t)) t.TrySetException(ex); }
            });
            _ = TimeoutGuard(id, 45000);
            return tcs.Task.ContinueWith(t => t.Status == TaskStatus.RanToCompletion ? t.Result.Item2 : "(אבחון נכשל)");
        }

        private static bool IsChallengeTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return true;
            return title.IndexOf("just a moment", StringComparison.OrdinalIgnoreCase) >= 0
                || title.IndexOf("רק רגע", StringComparison.Ordinal) >= 0
                || title.IndexOf("attention required", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // בדיקה שהמערכת מוכנה: קריאת API קלה (פרטי שיעור ידוע) עד שמתקבל 200.
        public async Task<bool> WaitUntilReadyAsync(int timeoutMs)
        {
            int waited = 0;
            while (waited < timeoutMs)
            {
                try
                {
                    var (status, body) = await ApiFetchAsync("GET", ProbePath, null, false, 8000);
                    if (status == 200)
                    {
                        Log.Write("KhlBrowser מוכן (Cloudflare נפתר)");
                        try
                        {
                            var (ss, sb) = await ApiFetchAsync("GET",
                                "Search/WebSite_GetSearchItems/" + Uri.EscapeDataString("קניבסקי") + "/-1/1/4", null, false);
                            Log.Write("בדיקת חיפוש עצמית: status=" + ss + " len=" + (sb?.Length ?? 0));
                        }
                        catch (Exception ex) { Log.Write("בדיקת חיפוש עצמית נכשלה: " + ex.Message); }
                        return true;
                    }
                    Log.Write("KhlBrowser עדיין לא מוכן (status=" + status + ")  " + Head(body));
                }
                catch (Exception ex) { Log.Write("KhlBrowser probe: " + ex.Message); }
                await Task.Delay(1500);
                waited += 1500;
            }
            Log.Write("KhlBrowser: תם הזמן להמתנה למוכנות - כנראה נדרש אימות Cloudflare ידני");
            await LogPageInfoAsync();
            return false;
        }

        // ----- קריאת API עם ניסיונות חוזרים -----
        // קריאה שנשלחת בזמן שדף www2 עדיין מתייצב (מיד אחרי Cloudflare) עלולה ללכת לאיבוד ולהסתיים
        // ב-timeout. מכיוון שכל הקריאות אידמפוטנטיות (GET/חיפוש/התחברות) - מנסים שוב עד שמתקבלת תשובה.
        private async Task<(int status, string body)> ApiFetchAsync(string method, string relUrl, string jsonBody, bool authorize, int timeoutMs = 15000)
        {
            int attempts = 3;
            for (int i = 1; ; i++)
            {
                try
                {
                    var r = await ApiFetchOnceAsync(method, relUrl, jsonBody, authorize, timeoutMs);
                    // מעקב אחר מצב הקישוריות: 0 = הדפדפן חסום (Cloudflare/רשת), כל תשובת HTTP = הקשר תקין
                    ready = r.status != 0;
                    return r;
                }
                catch (KhlException) when (i < attempts)
                {
                    Log.Write("ApiFetch ניסיון " + i + " נכשל (" + relUrl + ") - מנסה שוב");
                    await Task.Delay(800);
                }
            }
        }

        // ----- קריאת API בודדת דרך הדפדפן -----
        private Task<(int status, string body)> ApiFetchOnceAsync(string method, string relUrl, string jsonBody, bool authorize, int timeoutMs)
        {
            int id = Interlocked.Increment(ref reqSeq);
            var tcs = new TaskCompletionSource<(int, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending[id] = tcs;

            // אין לשלוח כותרת accept! נמדד מול השרת (18/08/2026): כל בקשה מהדפדפן שמוסיפה accept
            // ידנית מקבלת 403 עם דף אתגר של Cloudflare, וכל בקשה בלי accept מקבלת 200 - גם עם
            // authorization-site-key וגם עם authorization. ההסבר: Cloudflare בודק את טביעת האצבע
            // של הכותרות (סדר/הרכב), וכותרת accept שנקבעה ידנית ב-fetch יוצאת בסדר שאינו אופייני
            // לדפדפן. Chromium ממילא שולח accept משלו, ולשרת ה-API לא אכפת מהערך.
            var headers = new Dictionary<string, string>
            {
                { "authorization-site-key", "Bearer " + siteKey }
            };
            if (authorize && !string.IsNullOrEmpty(token)) headers["authorization"] = "Bearer " + token;

            string url = baseUrl + relUrl;
            string js = "window.__khl.fetch(" + id + "," + J(method) + "," + J(url) + "," +
                        (jsonBody == null ? "null" : J(jsonBody)) + "," + JsonSerializer.Serialize(headers) + "," +
                        (jsonBody == null ? "null" : J("application/json")) + ");";

            RunOnUi(() =>
            {
                try { _ = core.ExecuteScriptAsync(js); }
                catch (Exception ex) { if (pending.TryRemove(id, out var t)) t.TrySetException(ex); }
            });

            _ = TimeoutGuard(id, timeoutMs);
            return tcs.Task;
        }

        private async Task TimeoutGuard(int id, int ms)
        {
            await Task.Delay(ms);
            if (pending.TryRemove(id, out var tcs))
                tcs.TrySetException(new KhlException("תם הזמן לתגובת השרת."));
        }

        private void OnApiMessage(object sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            try
            {
                string raw = args.TryGetWebMessageAsString();
                if (string.IsNullOrEmpty(raw) || raw[0] != '{') return;
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                if (!root.TryGetProperty("t", out var tp) || tp.GetString() != "apiResult") return;
                int reqId = root.GetProperty("reqId").GetInt32();
                int status = root.GetProperty("status").GetInt32();
                string body = root.TryGetProperty("body", out var b) ? b.GetString() : "";
                if (pending.TryRemove(reqId, out var tcs))
                    tcs.TrySetResult((status, body));
            }
            catch { }
        }

        // ----- פעולות API ספציפיות -----
        // המרת קוד המשתמש לפורמט שהשרת מצפה לו.
        // מועתק מהאתר עצמו (setImpureUserIdForServer): קוד בן 6 ספרות נשלח עם קידומת "9",
        // וקוד שמתחיל ב-* או # נשלח בלי התו המקדים. בסוף האתר מריץ parseInt - כלומר זהו שדה
        // מספרי (קוד משתמש), לא דוא"ל. בלי ההמרה הזו השרת תמיד מחזיר "שם המשתמש או הסיסמה שגויים".
        public static string NormalizeUserCode(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            string e = raw.Trim();
            if (System.Text.RegularExpressions.Regex.IsMatch(e, "[0-9]{6}")) e = "9" + e;
            else if (System.Text.RegularExpressions.Regex.IsMatch(e, "[*#][0-9]{5}")) e = "9" + e.Substring(1);
            else if (System.Text.RegularExpressions.Regex.IsMatch(e, "[*#][0-9]{4}")) e = e.Substring(1);

            // חיקוי parseInt: מספר מתחילת המחרוזת (וללא אפסים מובילים)
            var m = System.Text.RegularExpressions.Regex.Match(e, @"^\s*([0-9]+)");
            if (!m.Success) return e;
            return long.TryParse(m.Groups[1].Value, out var n) ? n.ToString() : m.Groups[1].Value;
        }

        public async Task<bool> LoginAsync(string username, string password)
        {
            string user = NormalizeUserCode(username);
            Log.Write("LoginAsync: קוד משתמש " + username + " נשלח כ-" + user);
            string payload = JsonSerializer.Serialize(new { Username = user, Password = password });
            var (status, body) = await ApiFetchAsync("POST", "Accounts/UserLogin/", payload, false);
            // אין לרשום את גוף התשובה! השרת מחזיר בתוכו את סיסמת המשתמש בטקסט גלוי
            // ("Password":"..."), ולוג אינו מקום לסיסמאות. בכשל נרשמת רק הודעת השגיאה של השרת.
            Log.Write("LoginAsync: status=" + status + " bodyLen=" + (body?.Length ?? 0) +
                      (status == 200 ? "" : " message=" + (ExtractMessage(body) ?? "(אין)")));
            if (status != 200)
            {
                // הצג את הודעת השרת אם קיימת (למשל "Username or password is incorrect")
                string serverMsg = ExtractMessage(body);
                if (status == 400 || status == 401)
                    throw new KhlException(string.IsNullOrEmpty(serverMsg)
                        ? "שם המשתמש או הסיסמה שגויים."
                        : "התחברות נכשלה: שם המשתמש או הסיסמה שגויים (" + serverMsg + ").");
                throw new KhlException("התחברות נכשלה (קוד " + status + ")" +
                    (string.IsNullOrEmpty(serverMsg) ? "." : ": " + serverMsg));
            }
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                foreach (var name in new[] { "Token", "token", "AccessToken", "access_token" })
                    if (root.TryGetProperty(name, out var t) && t.ValueKind == JsonValueKind.String)
                    { token = t.GetString(); Log.Write("LoginAsync: התקבל טוקן (אורך " + token.Length + ")"); return true; }
                Log.Write("LoginAsync: לא נמצא טוקן בתשובה. מפתחות: " + string.Join(",", EnumNames(root)));
                throw new KhlException("התחברות הצליחה אך לא הוחזר טוקן (ייתכן שמבנה ה-API השתנה).");
            }
            catch (JsonException) { throw new KhlException("תשובת התחברות לא תקינה מהשרת."); }
        }

        public async Task<string> SearchAsync(string keyword)
        {
            string kw = Uri.EscapeDataString(keyword ?? "");
            var (status, body) = await ApiFetchAsync("GET", $"Search/WebSite_GetSearchItems/{kw}/-1/1/4", null, false);
            if (status != 200)
            {
                Log.Write("חיפוש: status=" + status + "  " + Head(body));
                throw new KhlException(status == 0
                    ? "אין תשובה מהשרת - החיבור נחסם (Cloudflare). נדרש אימות."
                    : "החיפוש נכשל (קוד " + status + ").");
            }
            return body;
        }

        public async Task<string> GetRavShiurimAsync(int ravId, int fromRow, int numRows)
        {
            var body = new
            {
                QueryType = -1, LangID = -1, MasechetID = -1, DafNo = -1, MasechetIDY = -1, DafNoY = -1,
                MoedID = -1, ParashaID = -1, EnglishDisplay = false, MasechetIDYOz = -1, DafNoYOz = -1,
                FromRow = fromRow, NumOfRows = numRows, PrefferedLanguage = -1, SearchOrder = 7,
                FiltersArray = Array.Empty<object>(), GeneralID = ravId, FilterSwitch = new string('1', 111)
            };
            var (status, resp) = await ApiFetchAsync("POST", "Search/WebSite_GetRavShiurim/", JsonSerializer.Serialize(body), false);
            if (status != 200) throw new KhlException("טעינת שיעורי הרב נכשלה (קוד " + status + ").");
            return resp;
        }

        // תוצאות חיפוש אמיתיות (רשימת שיעורים) עבור פריט מתוך רשימת ההצעות: נושא, קטגוריה, סדרה וכו'.
        // מיפוי הפרמטרים הועתק מהאתר: QueryType=SearchItemType, MainSubjectID=SearchItemId,
        // GeneralID=SearchItemBigId, CustomQueryString=SearchItemStrId, CustomString=הטקסט.
        // התשובה: {"ShiurimList":[...], "SessionText":"..."}.
        public async Task<string> GetSearchResultsAsync(int queryType, long mainSubjectId, long generalId,
            string strId, string text, int fromRow, int numRows, int order = 7, int lang = -1)
        {
            var body = new
            {
                QueryType = queryType,
                MainSubjectID = mainSubjectId,
                GeneralID = generalId,
                CustomQueryString = strId ?? "",
                CustomString = text ?? "",
                CustomBool = true,
                SessionID = (string)null,
                // סינון שפה נעשה בשני השדות יחד - כך האתר שולח, ורק כך התוצאות באמת מסוננות.
                LangID = lang, MasechetID = -1, DafNo = -1, MasechetIDY = -1, DafNoY = -1,
                MoedID = -1, ParashaID = -1, EnglishDisplay = false, MasechetIDYOz = -1, DafNoYOz = -1,
                FromRow = fromRow, NumOfRows = numRows, PrefferedLanguage = lang, SearchOrder = order,
                FiltersArray = Array.Empty<object>(), FilterSwitch = new string('1', 111)
            };
            var (status, resp) = await ApiFetchAsync("POST", "Search/WebSite_GetSearchResultsWithSession",
                JsonSerializer.Serialize(body), false);
            if (status != 200)
            {
                Log.Write("תוצאות חיפוש: status=" + status + "  " + Head(resp));
                throw new KhlException("טעינת התוצאות נכשלה (קוד " + status + ").");
            }
            return resp;
        }

        // ----- מסכי עיון: רבנים, נושאים, סדרות וערוצים -----
        // כל אלה רק מביאים רשימה; הפתיחה של פריט חוזרת למסלולים הקיימים -
        // רב דרך GetRavShiurimAsync, וסדרה/ערוץ/נושא דרך GetSearchResultsAsync.

        // חיפוש רבנים - בדיוק כמו דף הרבנים באתר.
        //
        // חשוב: Ravs/GetRavsNames (שהיה כאן קודם) מחזיר 8,610 רשומות שם, כולל רבנים שאין להם
        // שיעורים כלל. דף הרבנים באתר משתמש ב-WebSite_SearchRav, שמחזיר 6,971 - המספר שמוצג
        // באתר - ומוסיף לכל רב את ShiurimCount. לכן עברנו אליו.
        //
        // סדר הפרמטרים בכתובת שונה מסדר הפרמטרים בקוד של האתר; זה הסדר הנכון:
        //   {טקסט}/{שפה}/{מיון}/{משורה}/{כמות}/{אנגלית}
        // טקסט ריק נשלח כ-"NULL" (כך האתר עושה), שפה -1 = כל השפות.
        public Task<string> SearchRavsAsync(string text, int lang, int order, int fromRow, int numRows)
        {
            string t = string.IsNullOrWhiteSpace(text) ? "NULL" : Uri.EscapeDataString(text.Trim());
            return GetJsonAsync($"Search/WebSite_SearchRav/{t}/{lang}/{order}/{fromRow}/{numRows}/false",
                                "חיפוש הרבנים נכשל");
        }

        // מחזיר שורה ראשונה עם הסך הכל (VarLong=1, VarInt=כמות), ואחריה שורה לכל שפה שקיימת
        // בתוצאות (VarLong=2, VarInt=מזהה שפה). כך האתר בונה את רשימת הסינון לפי שפה.
        public Task<string> SearchRavsCountAsync(string text, int lang)
        {
            string t = string.IsNullOrWhiteSpace(text) ? "NULL" : Uri.EscapeDataString(text.Trim());
            return GetJsonAsync($"Search/WebSite_SearchRavGetCount/{t}/{lang}", "ספירת הרבנים נכשלה");
        }

        // עץ הנושאים של תפריט האתר: SubjectId/ParentSubjectId/SubjectTitleHebrew/DisplayOrder/ShowOnWebSite.
        public Task<string> GetTopicsAsync() => GetJsonAsync("General/GetTblSubjectToTopicsMenu/1/1", "טעינת הנושאים נכשלה");

        // ערוצי התורה. הפרמטר השני הוא "אנגלית" - false לעברית.
        public Task<string> GetChannelsAsync() => GetJsonAsync("HomePage/GetChannels/1/false", "טעינת הערוצים נכשלה");

        private async Task<string> GetJsonAsync(string path, string errPrefix)
        {
            var (status, body) = await ApiFetchAsync("GET", path, null, false);
            if (status != 200) throw new KhlException(errPrefix + " (קוד " + status + ").");
            return body;
        }

        // כל סדרות השיעורים, בדפים. כל סדרה מזוהה ב-CatId, שנפתח כ-QueryType=9.
        public async Task<string> GetAllSeriesAsync(int fromRow, int numRows, int order = 1, int lang = -1)
        {
            var body = new
            {
                QueryType = -1, MainSubjectID = -1, GeneralID = -1,
                CustomQueryString = "", CustomString = "", CustomBool = true, SessionID = (string)null,
                LangID = lang, MasechetID = -1, DafNo = -1, MasechetIDY = -1, DafNoY = -1,
                MoedID = -1, ParashaID = -1, EnglishDisplay = false, MasechetIDYOz = -1, DafNoYOz = -1,
                FromRow = fromRow, NumOfRows = numRows, PrefferedLanguage = lang, SearchOrder = order,
                FiltersArray = Array.Empty<object>(), FilterSwitch = new string('1', 111)
            };
            var (status, resp) = await ApiFetchAsync("POST", "Search/WebSite_GetAllSerries", JsonSerializer.Serialize(body), false);
            if (status != 200) throw new KhlException("טעינת הסדרות נכשלה (קוד " + status + ").");
            return resp;
        }

        // ----- מסכות הסינון (FilterSwitch) -----
        // FilterSwitch אינו מחרוזת קבועה אלא מסכת סיביות שבוחרת אילו קבוצות סינון פעילות.
        // האתר בונה אותה כך: מערך של 100 אפסים, מדליקים את האינדקסים של הקבוצות, ואז
        // מוחקים את אינדקס 1 (totalCount) - מה שמקצר ל-99 תווים ומזיז את כל המיפוי.
        // בלי המסכה הנכונה שאילתת אפשרויות הסינון מחזירה רשימה ריקה.
        private static string FilterMask(params int[] groups)
        {
            var a = new char[100];
            for (int i = 0; i < 100; i++) a[i] = '0';
            foreach (var g in groups) if (g >= 0 && g < 100) a[g] = '1';
            return new string(a).Remove(1, 1);
        }

        // מזהי קבוצות הסינון, מתוך ה-enum של האתר
        private const int FBySubject = 0, FByUser = 2, FByLanguage = 3, FOfAudios = 4, FOfVideos = 5,
                          FByDuration = 6, FPdfOrMarker = 7, FAudiance = 9, FDate = 10,
                          FParasha = 14, FMasechet = 16, FMoed = 17, FDaf = 20, FAids = 33;

        // "ספרים" - נושא, רב, שפה ועזרים (בדיוק כמו בדף הספרים באתר). FAids נשאר במסכה אף
        // שהממשק אינו מציג את הקבוצה (השרת מתעלם ממנה - ראו ההסבר ב-kiosk.html), כדי שהמסכה
        // תישאר זהה לזו שהאתר שולח: שינוי שלה עלול להחזיר רשימת אפשרויות ריקה.
        private static readonly string BooksMask = FilterMask(FBySubject, FByUser, FByLanguage, FAids);

        // "default" - קבוצת הסינון של דפי השיעורים הרגילים
        private static readonly string DefaultMask =
            FilterMask(FBySubject, FOfAudios, FOfVideos, FMoed, FByUser, FByDuration, FByLanguage,
                       FPdfOrMarker, FAudiance, FAids, FDate, FParasha, FMasechet, FDaf);

        // אפשרויות הסינון לספרים: [{ResultType, ResultId, ResultString, ResultCount}, ...]
        // ResultType: 0=נושא, 2=רב, 3=שפה, 33=עזרים, 1=סך הכל.
        public async Task<string> GetBooksFiltersAsync(string text, int order, int lang, string filtersJson)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (status, resp) = await ApiFetchAsync("POST", "Search/WebSite_GetBooks_SubjectIDs",
                BooksBody(text, 0, 24, order, lang, filtersJson), false, 45000);
            Log.Write("אפשרויות סינון ספרים: " + sw.ElapsedMilliseconds + " מ\"ש, status=" + status);
            if (status != 200) throw new KhlException("טעינת אפשרויות הסינון נכשלה (קוד " + status + ").");
            return resp;
        }

        // גוף הבקשה של הספרים. חשוב: השרת מחזיר רשימה ריקה אם חסרים שדות ברירת המחדל,
        // ולכן נשלחים כאן כולם במפורש ולא רק מה ששונה מברירת המחדל.
        // סינון שפה יחידה מועבר בשדות LangID/PrefferedLanguage במקום ב-FiltersArray.
        // נמדד מול השרת (25/08/2026): אותה בקשה בדיוק לוקחת 12 שניות דרך FiltersArray
        // ורק 0.75 שניות דרך השדות - עם קבוצת תוצאות וסדר זהים לחלוטין.
        // בבחירה של יותר משפה אחת אין ברירה אלא לחזור ל-FiltersArray האיטי.
        private static (string filters, int lang) ExtractSingleLanguage(string filtersJson, int lang)
        {
            if (string.IsNullOrWhiteSpace(filtersJson) || filtersJson.Trim() == "[]")
                return (filtersJson, lang);
            try
            {
                using var doc = JsonDocument.Parse(filtersJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return (filtersJson, lang);

                var langs = new List<JsonElement>();
                var rest = new List<JsonElement>();
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    bool isLang = el.ValueKind == JsonValueKind.Object &&
                                  el.TryGetProperty("ResultType", out var rt) &&
                                  rt.ValueKind == JsonValueKind.Number && rt.GetInt32() == FByLanguage;
                    (isLang ? langs : rest).Add(el);
                }
                if (langs.Count != 1) return (filtersJson, lang);
                if (!langs[0].TryGetProperty("ResultId", out var rid) || rid.ValueKind != JsonValueKind.Number)
                    return (filtersJson, lang);

                var sb = new StringBuilder("[");
                for (int i = 0; i < rest.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(rest[i].GetRawText());
                }
                sb.Append(']');
                return (sb.ToString(), rid.GetInt32());
            }
            catch { return (filtersJson, lang); }
        }

        private static string BooksBody(string text, int fromRow, int numRows, int order, int lang, string filtersJson)
        {
            (filtersJson, lang) = ExtractSingleLanguage(filtersJson, lang);
            string filters = string.IsNullOrWhiteSpace(filtersJson) ? "[]" : filtersJson;
            return "{\"QueryType\":-1,\"LangID\":" + lang + ",\"MasechetID\":-1,\"DafNo\":-1,\"MasechetIDY\":-1,"
                 + "\"DafNoY\":-1,\"MoedID\":-1,\"ParashaID\":-1,\"EnglishDisplay\":false,\"MasechetIDYOz\":-1,"
                 + "\"DafNoYOz\":-1,\"FromRow\":" + fromRow + ",\"NumOfRows\":" + numRows
                 + ",\"PrefferedLanguage\":" + lang + ",\"SearchOrder\":" + order
                 + ",\"GeneralID\":-1,\"FreeSearchText\":" + JsonSerializer.Serialize(text ?? "")
                 + ",\"FiltersArray\":" + filters + ",\"FilterSwitch\":\"" + BooksMask + "\"}";
        }

        // ----- נושא מעץ הנושאים -----
        // דורש שני מזהים: MainSubjectID (נושא-אב כלשהו בשרשרת) ו-GeneralID (הנושא עצמו).
        // בענף "לימוד יומי" הסדר הפוך, ולכן אם התשובה ריקה מנסים גם את הסדר ההפוך.
        public async Task<string> GetShiurimOfSubjectAsync(long mainId, long subjectId,
            int fromRow, int numRows, int order, int lang, string dailyDate = null)
        {
            string first = await SubjectQueryAsync(mainId, subjectId, fromRow, numRows, order, lang, dailyDate);
            if (!LooksEmpty(first)) return first;
            // בלימוד יומי התאריך הוא שקובע, ואין טעם להפוך את מזהי הנושא
            if (!string.IsNullOrEmpty(dailyDate)) return first;
            string second = await SubjectQueryAsync(subjectId, mainId, fromRow, numRows, order, lang, null);
            return LooksEmpty(second) ? first : second;
        }

        // כותרת הלימוד היומי לתאריך שנבחר ("ספר איוב פרק מ, יום שלישי י\"ב אלול תשפ\"ו").
        // dailyName הוא שם הפונקציה אצלם, למשל DAILY_GetTodayNavi.
        public async Task<string> GetDailyTitleAsync(string dailyName, string isoDate, bool yerushalmiOz)
        {
            string path = $"Search/getDailyLimudTitle/false/{Uri.EscapeDataString(dailyName)}/{Uri.EscapeDataString(isoDate)}";
            if (dailyName == "DAILY_GetTodayDafYerushalmi") path += yerushalmiOz ? "/1" : "/0";
            var (status, body) = await ApiFetchAsync("GET", path, null, false);
            return status == 200 ? body : "[]";
        }

        private static bool LooksEmpty(string json) =>
            string.IsNullOrWhiteSpace(json) || json.Trim() == "[]" || json.Trim() == "null";

        private async Task<string> SubjectQueryAsync(long mainId, long generalId,
            int fromRow, int numRows, int order, int lang, string dailyDate = null)
        {
            var body = new
            {
                QueryType = -1, MainSubjectID = mainId, GeneralID = generalId,
                // בלימוד יומי השדה הזה הוא שבוחר את השיעורים של אותו יום
                DailyShiurDate = dailyDate,
                CustomQueryString = "", CustomString = "", CustomBool = true, SessionID = (string)null,
                LangID = lang, MasechetID = -1, DafNo = -1, MasechetIDY = -1, DafNoY = -1,
                MoedID = -1, ParashaID = -1, EnglishDisplay = false, MasechetIDYOz = -1, DafNoYOz = -1,
                FromRow = fromRow, NumOfRows = numRows, PrefferedLanguage = lang, SearchOrder = order,
                FiltersArray = Array.Empty<object>(), FilterSwitch = DefaultMask
            };
            var (status, resp) = await ApiFetchAsync("POST", "Search/WebSite_GetShiurimOfSubject",
                JsonSerializer.Serialize(body), false);
            if (status != 200) throw new KhlException("טעינת שיעורי הנושא נכשלה (קוד " + status + ").");
            return resp;
        }

        // ----- ענפים מיוחדים: פרשת השבוע ודף היומי -----
        // אלה אינם עוברים ב-GetShiurimOfSubject אלא ב-endpoint של דף הבית.
        // QueryType: 5=פרשה, 1=תלמוד בבלי (מסכת+דף), 8=ירושלמי.
        public async Task<string> GetHomePageShiurimAsync(int queryType, long parashaId, long masechetId,
            long dafNo, int fromRow, int numRows, int order, int lang)
        {
            var body = new
            {
                QueryType = queryType, MainSubjectID = -1, GeneralID = -1,
                CustomQueryString = "", CustomString = "", CustomBool = true, SessionID = (string)null,
                LangID = lang, MasechetID = masechetId, DafNo = dafNo,
                MasechetIDY = queryType == 8 ? masechetId : -1, DafNoY = queryType == 8 ? dafNo : -1,
                MoedID = -1, ParashaID = parashaId, EnglishDisplay = false,
                MasechetIDYOz = -1, DafNoYOz = -1,
                FromRow = fromRow, NumOfRows = numRows,
                PrefferedLanguage = lang < 0 ? 1 : lang, SearchOrder = order,
                FiltersArray = Array.Empty<object>(), FilterSwitch = DefaultMask
            };
            var (status, resp) = await ApiFetchAsync("POST", "Search/WebSite_HomePage_GetMoreShiurim",
                JsonSerializer.Serialize(body), false);
            if (status != 200) throw new KhlException("טעינת השיעורים נכשלה (קוד " + status + ").");
            return resp;
        }

        // רשימות לענפים המיוחדים
        public Task<string> GetParashotAsync()  => GetJsonAsync("Tanach/GetAllParashas", "טעינת הפרשות נכשלה");
        public Task<string> GetHumashimAsync()  => GetJsonAsync("Tanach/GetAllHumashes", "טעינת החומשים נכשלה");
        public Task<string> GetMasechtotAsync() => GetJsonAsync("Shas/GetAllTblMasechets", "טעינת המסכתות נכשלה");
        public Task<string> GetNachAsync()      => GetJsonAsync("Tanach/GetAllNachIsShow", "טעינת ספרי נ\"ך נכשלה");
        // שימו לב: הנתיב הוא TblMoeds בלי שם פעולה - כל וריאציה אחרת מחזירה 404.
        public Task<string> GetMoadimAsync()    => GetJsonAsync("TblMoeds", "טעינת המועדים נכשלה");

        // ספרים, עלונים וגליונות. אותו גוף בקשה בדיוק כמו חיפוש שיעורים; הטקסט החופשי
        // עובר בשדה FreeSearchText (ולא CustomString כמו בחיפוש הרגיל). ההורדה עצמה אינה
        // עוברת ב-API כלל - היא קישור סטטי, ראו DirectPdfUrl.
        public async Task<string> GetBooksAsync(string text, int fromRow, int numRows,
            int order = 7, int lang = -1, string filtersJson = null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (status, resp) = await ApiFetchAsync("POST", "Search/WebSite_GetBooks",
                BooksBody(text, fromRow, numRows, order, lang, filtersJson), false, 45000);
            Log.Write("ספרים: " + sw.ElapsedMilliseconds + " מ\"ש, status=" + status +
                      ", סינונים=" + (string.IsNullOrWhiteSpace(filtersJson) ? "0" : filtersJson.Length.ToString() + " תווים"));
            if (status != 200) throw new KhlException("טעינת הספרים נכשלה (קוד " + status + ").");
            return resp;
        }

        public async Task<string> GetShiurDetailsAsync(long fileId)
        {
            var (status, body) = await ApiFetchAsync("GET", $"TblShiurimLists/WebSite_GetShiurDetails/{fileId}", null, false);
            if (status != 200) throw new KhlException("טעינת פרטי השיעור נכשלה (קוד " + status + ").");
            return body;
        }

        private static string ExtractMessage(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                foreach (var name in new[] { "message", "Message", "error", "Error", "error_description" })
                    if (doc.RootElement.TryGetProperty(name, out var m) && m.ValueKind == JsonValueKind.String)
                        return m.GetString();
            }
            catch { }
            return null;
        }

        private static IEnumerable<string> EnumNames(JsonElement obj)
        {
            if (obj.ValueKind != JsonValueKind.Object) yield break;
            foreach (var p in obj.EnumerateObject()) yield return p.Name;
        }

        public async Task<string> GetDownloadKeyAsync(long fileId)
        {
            var (status, body) = await ApiFetchAsync("GET", $"files/checkAutorizationDownload/{fileId}/false", null, true);
            Log.Write("GetDownloadKey(" + fileId + "): status=" + status + " head=" +
                      (body != null ? body.Substring(0, Math.Min(120, body.Length)) : "(null)"));
            if (status == 401 || status == 403)
            {
                token = null;   // ה-token פג/נדחה - נקה כדי שהמערכת תבקש התחברות מחדש
                throw new KhlAuthException("נדרשת התחברות לחשבון קול הלשון כדי להוריד.");
            }
            // 204 = השרת מסרב להנפיק מפתח: השיעור מסומן DisableDownload בקול הלשון (בעיקר מוזיקה
            // וניגונים). זו אינה תקלה זמנית - ניסיון חוזר או התחברות מחדש לא יעזרו.
            if (status == 204 || (status == 200 && string.IsNullOrWhiteSpace(body)))
                throw new KhlDownloadBlockedException("קול הלשון חסמו את ההורדה של שיעור זה - ניתן רק להאזין לו באתר.");
            if (status != 200) throw new KhlException("קבלת מפתח ההורדה נכשלה (קוד " + status + ").");
            using var doc = JsonDocument.Parse(body);
            foreach (var name in new[] { "key", "Key", "downloadKey", "DownloadKey" })
                if (doc.RootElement.TryGetProperty(name, out var k) && k.ValueKind == JsonValueKind.String)
                    return k.GetString();
            throw new KhlException("השרת לא החזיר מפתח הורדה.");
        }

        // כתובת ההורדה הישירה של השמע - אותו endpoint שהנגן באתר קול הלשון משתמש בו.
        // אינו דורש התחברות, טוקן או מפתח הורדה, ומחזיר את קובץ ה-MP3 המלא (כולל
        // Accept-Ranges ו-content-disposition: attachment). עובד גם על שיעורים המסומנים
        // DisableDownload, שעבורם checkAutorizationDownload מחזיר 204 ריק.
        // השימוש בו בקיוסק הוא בתיאום עם קול הלשון (24/08/2026).
        public string DirectAudioUrl(long fileId) => AudioUrlBase + fileId;

        // אותה כתובת בלי המזהה, לשימוש נגן ההאזנה שבממשק: הוא מרכיב את הכתובת בעצמו
        // ומזרים ממנה (התשובה נושאת Accept-Ranges, ולכן אין צורך להוריד את הקובץ כולו).
        // נמסרת לממשק בהודעת settings ולא כתובה שם, כדי שתלך אחרי כתובת ה-API שבהגדרות.
        public string AudioUrlBase => baseUrl + "files/GetMp3FileToPlay/";

        // קול הלשון בונים את שמות הקבצים ממזהה השיעור: השם מרופד באפסים ל-8 ספרות, והתיקייה
        // היא 5 הספרות הראשונות שלו. (מהקוד של האתר: getShiurNameFromID / getShiurFolderName,
        // fileNameLengthFormat=8.) לדוגמה 2193085 -> 02193/02193085, ו-42691555 -> 42691/42691555.
        public static string ShiurFileName(long fileId) => fileId.ToString().PadLeft(8, '0');
        public static string ShiurFolderName(long fileId) => ShiurFileName(fileId).Substring(0, 5);

        // כתובת ההורדה הישירה של PDF (ספרים, עלונים ומקורות מצורפים) - קישור סטטי, בלי API כלל.
        public static string DirectPdfUrl(long fileId) =>
            SiteOrigin + "PDF/Shiurim/" + ShiurFolderName(fileId) + "/" + ShiurFileName(fileId) + ".pdf";

        // ================= הורדת וידאו (HLS) =================
        // קול הלשון לא מגישים את הווידאו כקובץ אחד: הוא זמין רק כזרם HLS משרת Wowza.
        // המסלול: playlist.m3u8 (רשימת איכויות) -> chunklist.m3u8 (רשימת מקטעים) -> עשרות
        // קבצי .ts. ההורדה כאן מושכת את המקטעים לפי הסדר ומשרשרת אותם לקובץ אחד. MPEG-TS
        // בנוי לשרשור (כל מקטע עומד בפני עצמו), ולכן התוצאה היא קובץ .ts תקין - בלי ffmpeg.
        //
        // מזהה ה-session בשמות המקטעים (w2130495691) מתחלף בכל בקשה ל-playlist, ולכן חובה
        // לקרוא playlist ואז chunklist בכל הורדה מחדש ולא לשמור כתובות מקטעים.
        //
        // שרת ה-streaming אינו מציב אתגר Cloudflare (בניגוד ל-/api/), ולכן דווקא כאן אפשר
        // HttpClient רגיל - וזה הכרחי, כי משיכת עשרות מגהבייט דרך JS הייתה בלתי מעשית.
        public const string StreamingBase = SiteOrigin + "streaming/KHL_Video/_definst_/amlst:NewArchive/";

        private static readonly HttpClient direct = CreateDirectClient();

        private static HttpClient CreateDirectClient()
        {
            var c = new HttpClient();
            c.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/151.0.0.0 Safari/537.36");
            c.Timeout = TimeSpan.FromMinutes(10);
            return c;
        }

        // הורדת קובץ סטטי ישירות ב-HttpClient, בלי מנגנון ההורדה של WebView2.
        //
        // למה לא דרך WebView2 כמו שאר ההורדות: לקובצי ה-PDF של קול הלשון אין כותרת
        // content-disposition: attachment (בניגוד ל-GetMp3FileToPlay, שיש לו). לכן WebView2
        // מזהה application/pdf ופותח את הקובץ במציג ה-PDF המובנה שלו במקום להוריד אותו -
        // האירוע DownloadStarting כלל אינו נורה, וההורדה "נתקעת" עד לפקיעת שומר הסף.
        // (נמדד 24/08/2026: שני ניסיונות פגו ב-30 שניות כל אחד.)
        public async Task DownloadFileDirectAsync(string url, string finalPath,
            Action<long, long> progress, CancellationToken ct)
        {
            string part = finalPath + ".part";
            try { if (File.Exists(part)) File.Delete(part); } catch { }

            using (var resp = await direct.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (!resp.IsSuccessStatusCode)
                    throw new KhlException("ההורדה נכשלה (קוד " + (int)resp.StatusCode + ").");

                // כתובת שאינה קיימת מוגשת ע"י האתר כדף ה-SPA בקוד 200 - לא כשגיאה.
                string ctype = resp.Content.Headers.ContentType?.MediaType ?? "";
                if (ctype.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
                    throw new KhlException("הקובץ אינו זמין להורדה בקול הלשון.");

                long total = resp.Content.Headers.ContentLength ?? -1;
                long done = 0;
                using var src = await resp.Content.ReadAsStreamAsync(ct);
                using var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
                var buf = new byte[81920];
                int n;
                while ((n = await src.ReadAsync(buf, 0, buf.Length, ct)) > 0)
                {
                    await fs.WriteAsync(buf, 0, n, ct);
                    done += n;
                    progress?.Invoke(done, total);
                }
            }

            if (File.Exists(finalPath)) File.Delete(finalPath);
            File.Move(part, finalPath);
        }

        // מיקום הווידאו בשרת המדיה: מחזיר {"location":"42428/42428927"}.
        public async Task<string> GetVideoLocationAsync(long fileId)
        {
            var (status, body) = await ApiFetchAsync("GET", $"files/getLocationOfFileToVideo/{fileId}", null, false);
            if (status != 200 || string.IsNullOrWhiteSpace(body))
                throw new KhlException("לא נמצא מיקום הווידאו לשיעור זה (קוד " + status + ").");
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("location", out var l) && l.ValueKind == JsonValueKind.String)
                return l.GetString();
            throw new KhlException("השרת לא החזיר מיקום וידאו לשיעור זה.");
        }

        public async Task DownloadVideoToFileAsync(long fileId, Quality quality, string finalPath,
            Action<long, long> progress, CancellationToken ct)
        {
            string location = await GetVideoLocationAsync(fileId);

            // "HD" היא תיקיית המקור באיכות מלאה; לשיעורים ישנים יש רק "Compressed".
            string dir = null, chunkName = null;
            long bandwidth = 0;
            foreach (var folder in new[] { "HD", "Compressed" })
            {
                string b = StreamingBase + folder + "/" + location + "/";
                string playlist = await GetPlaylistAsync(b + "playlist.m3u8", ct);
                if (playlist == null) continue;
                (chunkName, bandwidth) = PickRendition(playlist, quality == Quality.HdVideo);
                if (chunkName == null) continue;
                dir = b;
                break;
            }
            if (dir == null) throw new KhlException("לא נמצא זרם וידאו לשיעור זה.");

            string chunklist = await GetPlaylistAsync(dir + chunkName, ct)
                ?? throw new KhlException("רשימת מקטעי הווידאו לא נטענה.");
            var segments = chunklist.Split((char)10)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0 && !x.StartsWith("#"))
                .ToList();
            if (segments.Count == 0) throw new KhlException("רשימת מקטעי הווידאו ריקה.");

            // הערכת גודל הקובץ מראש: משך הזרם (סכום ה-EXTINF) כפול קצב הסיביות שהמניפסט
            // מצהיר עליו. בלי זה אין מה לדווח בזמן המקטע הראשון, והממשק היה מציג פס "לא ידוע"
            // שנראה כמו הורדה שהושלמה. ההערכה מדויקת בכ-10%, ומתרחבת בהמשך אם התבררה כנמוכה.
            double seconds = 0;
            foreach (var line in chunklist.Split((char)10))
            {
                var t = line.Trim();
                if (!t.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase)) continue;
                var num = t.Substring(8).TrimEnd(',').Trim();
                if (double.TryParse(num, System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out var d)) seconds += d;
            }
            long estimated = bandwidth > 0 && seconds > 0 ? (long)(seconds * bandwidth / 8.0) : -1;
            Log.Write("DownloadVideo(" + fileId + "): " + segments.Count + " מקטעים, ~" +
                      (int)seconds + " שניות, הערכה " + (estimated > 0 ? (estimated / 1048576) + "MB" : "לא ידועה") +
                      " <- " + dir + chunkName);

            string part = finalPath + ".part";
            try { if (File.Exists(part)) File.Delete(part); } catch { }

            long done = 0;
            using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true))
            {
                for (int i = 0; i < segments.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    using var resp = await direct.GetAsync(dir + segments[i], HttpCompletionOption.ResponseHeadersRead, ct);
                    if (!resp.IsSuccessStatusCode)
                        throw new KhlException("הורדת מקטע וידאו נכשלה (קוד " + (int)resp.StatusCode + ").");
                    using var src = await resp.Content.ReadAsStreamAsync(ct);
                    var buf = new byte[81920];
                    int n;
                    while ((n = await src.ReadAsync(buf, 0, buf.Length, ct)) > 0)
                    {
                        await fs.WriteAsync(buf, 0, n, ct);
                        done += n;
                        // הערכת הגודל: מהמניפסט בהתחלה, ומרגע שיש מקטע שלם גם לפי הממוצע הנמדד.
                        // לוקחים את הגדולה מביניהן - הערכת חסר היא זו שגורמת לפס להיתקע על 99%.
                        long measured = i > 0 ? (long)((double)done / i * segments.Count) : 0;
                        long total = Math.Max(estimated, measured);
                        if (total > 0 && done > total) total = (long)(done * 1.02);
                        progress?.Invoke(done, total > 0 ? total : -1);
                    }
                }
            }

            if (File.Exists(finalPath)) File.Delete(finalPath);
            File.Move(part, finalPath);
            progress?.Invoke(done, done);
        }

        // מוריד m3u8 ומוודא שזו באמת רשימת השמעה: כתובת שגויה מחזירה את דף ה-SPA (HTML) בקוד 200.
        // כתובת המניפסט להזרמה בממשק. שרת ה-streaming מגיש Access-Control-Allow-Origin: *
        // ואינו מציב אתגר Cloudflare, ולכן הדף עצמו יכול למשוך ממנו את המניפסט ואת
        // המקטעים. מה שהוא לא יכול הוא לברר את מיקום הקובץ - זו קריאת API שעוברת
        // ב-Cloudflare - ולכן הבירור נעשה כאן, כולל בחירת התיקייה הקיימת מבין השתיים.
        public async Task<string> GetVideoPlaylistUrlAsync(long fileId, bool hd, CancellationToken ct)
        {
            string location = await GetVideoLocationAsync(fileId);
            foreach (var folder in hd ? new[] { "HD", "Compressed" } : new[] { "Compressed", "HD" })
            {
                string url = StreamingBase + folder + "/" + location + "/playlist.m3u8";
                if (await GetPlaylistAsync(url, ct) != null) return url;
            }
            throw new KhlException("לא נמצא זרם וידאו לשיעור זה.");
        }

        private static async Task<string> GetPlaylistAsync(string url, CancellationToken ct)
        {
            try
            {
                using var resp = await direct.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode) return null;
                string body = await resp.Content.ReadAsStringAsync(ct);
                return body != null && body.StartsWith("#EXTM3U", StringComparison.Ordinal) ? body : null;
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        // בוחר איכות מתוך playlist.m3u8: הגבוהה ביותר ל-HD, הנמוכה ביותר לווידאו רגיל.
        private static (string uri, long bandwidth) PickRendition(string playlist, bool highest)
        {
            var lines = playlist.Split((char)10).Select(x => x.Trim()).ToArray();
            string best = null;
            long bestBw = -1;
            for (int i = 0; i < lines.Length - 1; i++)
            {
                if (!lines[i].StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase)) continue;
                string uri = lines[i + 1];
                if (uri.Length == 0 || uri.StartsWith("#")) continue;

                long bw = 0;
                int b = lines[i].IndexOf("BANDWIDTH=", StringComparison.OrdinalIgnoreCase);
                if (b >= 0)
                {
                    string num = new string(lines[i].Substring(b + 10).TakeWhile(char.IsDigit).ToArray());
                    long.TryParse(num, out bw);
                }
                if (best == null || (highest ? bw > bestBw : bw < bestBw)) { best = uri; bestBw = bw; }
            }
            return (best, bestBw);
        }

        // ----- הורדת קובץ דרך מנגנון ההורדה של WebView2 -----
        // getKey מתקבל כפונקציה ולא כערך בכוונה: מפתח ההורדה נמשך בתוך אותו תור שבו מופעלת ההורדה.
        // נמדד (18/08/2026): כששולחים כמה בקשות checkAutorizationDownload בו-זמנית, השרת מגיב רק
        // לאחת מהן, ובקשת הקובץ של האחרות נתקעת בלי תשובה כלל - ההורדה פשוט לא מתחילה. סידור
        // "בקשת מפתח -> הפעלת הורדה" בתור אחד פותר זאת; ההורדות עצמן ממשיכות במקביל לאחר שהתחילו.
        public async Task DownloadToFileAsync(long fileId, Quality quality, Func<Task<string>> getKey, string finalPath,
            Action<long, long> progress, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            string part = finalPath + ".part";
            try { if (File.Exists(part)) File.Delete(part); } catch { }

            var req = new DlReq { PartPath = part, FinalPath = finalPath, Progress = progress, Tcs = tcs, Ct = ct };
            downloads[fileId] = req;

            // ביטול: מסמנים, המטפל ב-StateChanged יבצע Cancel על הפעולה
            using var reg = ct.Register(() => { RunOnUi(() => { if (req.Op != null) { try { req.Op.Cancel(); } catch { } } }); });

            await triggerGate.WaitAsync(ct);
            try
            {
                var target = dlCore ?? core;
                bool started = false;

                // שמע ו-PDF יורדים דרך הכתובות הישירות של האתר: בלי מפתח ובלי התחברות.
                // וידאו עדיין עובר במסלול הישן (מפתח הורדה + GetFileDownload).
                bool direct = quality == Quality.Audio || quality == Quality.Pdf;

                for (int attempt = 1; attempt <= 2 && !started; attempt++)
                {
                    string url;
                    if (quality == Quality.Pdf)
                        url = DirectPdfUrl(fileId);
                    else if (quality == Quality.Audio)
                        url = DirectAudioUrl(fileId);
                    else
                        url = baseUrl + $"files/GetFileDownload/{fileId}/{(int)quality}/{await getKey()}/null/false/false";
                    Log.Write("DownloadToFile(" + fileId + "): מפעיל הורדה בדפדפן (ניסיון " + attempt +
                              (direct ? ", ישירה" : "") + ") -> " + url);

                    req.Started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    RunOnUi(() =>
                    {
                        try { target.Navigate(url); }
                        catch (Exception ex) { if (downloads.TryRemove(fileId, out _)) tcs.TrySetException(ex); }
                    });

                    // שומר סף: אם ההורדה לא נפתחה - מדווחים שגיאה במקום להיתקע לנצח.
                    var done = await Task.WhenAny(req.Started.Task, Task.Delay(30000, ct));
                    ct.ThrowIfCancellationRequested();   // ביטול ע"י המשתמש - לא "ההורדה לא התחילה"
                    started = done == req.Started.Task;
                    if (!started)
                    {
                        Log.Write("DownloadToFile(" + fileId + "): ההורדה לא נפתחה תוך 30 שניות (ניסיון " + attempt + ")");
                        RunOnUi(() => { try { target.Navigate("about:blank"); } catch { } });   // שחרור ניווט תקוע
                    }
                }

                if (!started)
                {
                    downloads.TryRemove(fileId, out _);
                    throw new KhlException("ההורדה לא התחילה (השרת לא החזיר קובץ). נסו שוב.");
                }
            }
            finally { triggerGate.Release(); }

            await tcs.Task;
        }

        private void OnDownloadStarting(object sender, CoreWebView2DownloadStartingEventArgs e)
        {
            try
            {
                var op = e.DownloadOperation;
                long fileId = ParseFileId(op.Uri);
                Log.Write("DownloadStarting: uri=" + op.Uri + " fileId=" + fileId + " matched=" + downloads.ContainsKey(fileId));
                if (fileId < 0 || !downloads.TryGetValue(fileId, out var req))
                    return;   // לא הורדה שלנו - השאר להתנהגות ברירת המחדל

                req.Op = op;
                req.Started?.TrySetResult(true);   // משחרר את שומר הסף ב-DownloadToFileAsync
                e.Handled = true;                 // ללא חלונית הורדה מובנית
                e.ResultFilePath = req.PartPath;

                op.BytesReceivedChanged += (s2, a2) =>
                {
                    long total = op.TotalBytesToReceive.HasValue ? (long)op.TotalBytesToReceive.Value : -1;
                    req.Progress?.Invoke(op.BytesReceived, total);
                };
                op.StateChanged += (s3, a3) =>
                {
                    Log.Write("Download state(" + fileId + ")=" + op.State + " bytes=" + op.BytesReceived +
                              (op.State == CoreWebView2DownloadState.Interrupted ? " reason=" + op.InterruptReason : ""));
                    if (op.State == CoreWebView2DownloadState.Completed)
                    {
                        downloads.TryRemove(fileId, out _);
                        try
                        {
                            if (File.Exists(req.FinalPath)) File.Delete(req.FinalPath);
                            File.Move(req.PartPath, req.FinalPath);
                            req.Tcs.TrySetResult(true);
                        }
                        catch (Exception ex) { req.Tcs.TrySetException(ex); }
                    }
                    else if (op.State == CoreWebView2DownloadState.Interrupted)
                    {
                        downloads.TryRemove(fileId, out _);
                        try { if (File.Exists(req.PartPath)) File.Delete(req.PartPath); } catch { }
                        if (req.Ct.IsCancellationRequested)
                            req.Tcs.TrySetCanceled();
                        else
                            req.Tcs.TrySetException(new KhlException("ההורדה הופסקה (" + op.InterruptReason + ")."));
                    }
                };
            }
            catch (Exception ex) { Log.Write("OnDownloadStarting שגיאה: " + ex.Message); }
        }

        private static long ParseFileId(string uri)
        {
            // .../files/GetFileDownload/{fileId}/{quality}/...  |  .../files/GetMp3FileToPlay/{fileId}
            // |  .../PDF/Shiurim/{folder}/{00000000}.pdf
            try
            {
                foreach (var marker in new[] { "GetFileDownload/", "GetMp3FileToPlay/" })
                {
                    int i = uri.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                    if (i < 0) continue;
                    string rest = uri.Substring(i + marker.Length);
                    int end = rest.IndexOfAny(new[] { '/', '?', '#' });
                    string num = end > 0 ? rest.Substring(0, end) : rest;
                    if (long.TryParse(num, out var v)) return v;
                }

                // PDF: השם מרופד באפסים, ולכן long.Parse מחזיר את המזהה המקורי.
                int p = uri.IndexOf("/PDF/Shiurim/", StringComparison.OrdinalIgnoreCase);
                if (p >= 0)
                {
                    string name = uri.Substring(p);
                    int dot = name.LastIndexOf(".pdf", StringComparison.OrdinalIgnoreCase);
                    if (dot > 0)
                    {
                        int slash = name.LastIndexOf('/', dot);
                        if (slash >= 0 && long.TryParse(name.Substring(slash + 1, dot - slash - 1), out var pv)) return pv;
                    }
                }
                return -1;
            }
            catch { return -1; }
        }

        // מריץ סקריפט ב-WebView הנסתר ומחזיר את התוצאה (מחרוזת מפוענחת). רץ על thread ה-UI.
        private Task<string> RunScriptAsync(string js) => RunScriptOnAsync(core, js);

        private Task<string> RunScriptOnAsync(CoreWebView2 target, string js)
        {
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            RunOnUi(async () =>
            {
                try { var r = await target.ExecuteScriptAsync(js); tcs.TrySetResult(DecodeScriptString(r)); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
            return tcs.Task;
        }

        // ExecuteScriptAsync מחזיר ערך מקודד-JSON. אם הסקריפט החזיר מחרוזת - מפענחים אותה.
        private static string DecodeScriptString(string raw)
        {
            if (string.IsNullOrEmpty(raw) || raw == "null") return "";
            try { return JsonSerializer.Deserialize<string>(raw) ?? ""; }
            catch { return raw; }
        }

        // התחברות אוטומטית: מנסה לטעון token שכבר נשמר בפרופיל הדפדפן (מהתחברות קודמת דרך האתר).
        public async Task<bool> TryLoadStoredTokenAsync()
        {
            try
            {
                string tok = await RunScriptAsync(TokenScanJs);
                if (!string.IsNullOrEmpty(tok)) { token = tok; Log.Write("נטען טוקן שמור מהפרופיל (אורך " + tok.Length + ")"); return true; }
            }
            catch (Exception ex) { Log.Write("TryLoadStoredToken ex: " + ex.Message); }
            return false;
        }

        // נקרא ע"י חלון ההתחברות: סורק את הטוקן ומגדיר אותו. מחזיר true אם נמצא.
        public async Task<bool> CaptureTokenFromStorageAsync()
        {
            string tok = await RunScriptAsync(TokenScanJs);
            if (!string.IsNullOrEmpty(tok)) { token = tok; return true; }
            return false;
        }

        public async Task<string> DumpStorageAsync()
        {
            try { return await RunScriptAsync(StorageDumpJs); } catch (Exception ex) { return "dump ex: " + ex.Message; }
        }

        // ----- עזרי thread -----
        private void RunOnUi(Action a)
        {
            try
            {
                if (view.InvokeRequired) view.BeginInvoke(a);
                else a();
            }
            catch { }
        }

        private static string J(string s) => JsonSerializer.Serialize(s ?? "");

        private static async Task WithTimeout(Task task, int ms)
        {
            var t = await Task.WhenAny(task, Task.Delay(ms));
            if (t != task) throw new TimeoutException();
            await task;
        }
    }
}
