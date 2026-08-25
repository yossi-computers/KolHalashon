using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace KolHalashonKiosk
{
    // שכבת התקשורת מול ה-API של קול הלשון.
    // התגלה ע"י בחינת התעבורה של www2.kolhalashon.com. השרת חוסם בקשות שלא נראות כמו הדפדפן,
    // ולכן חובה לשלוח את אותן כותרות (origin/referer/user-agent + authorization-site-key).
    //
    // זרימת ההורדה (זהה למה שהקיוסק המקורי עושה ברקע):
    //   1) התחברות: POST Accounts/UserLogin  -> Token של המשתמש
    //   2) מפתח הורדה: GET files/checkAutorizationDownload/{fileId}/false  (עם Bearer Token)  -> key
    //   3) הקובץ עצמו: GET files/GetFileDownload/{fileId}/{quality}/{key}/null/false/false
    //
    // חיפוש ועיון פתוחים ואינם דורשים התחברות (מספיק authorization-site-key).
    public class KhlApi
    {
        public enum Quality { Audio = 1, Video = 2, HdVideo = 3, Pdf = 4 }   // Pdf אינו פרמטר של ה-API - הורדתו היא קישור סטטי

        private readonly HttpClient http;
        private string baseUrl;      // "https://srv.kolhalashon.com/api/"
        private string siteKey;      // הערך שמופיע כ- authorization-site-key: Bearer <siteKey>. מתחלף מדי פעם -> ניתן לעדכון בהגדרות.
        private string userToken;    // מתקבל בהתחברות; נדרש למפתח ההורדה.

        public bool IsLoggedIn => !string.IsNullOrEmpty(userToken);

        public KhlApi(string baseUrl, string siteKey, string existingToken = null)
        {
            this.baseUrl = NormalizeBase(baseUrl);
            this.siteKey = string.IsNullOrWhiteSpace(siteKey) ? "8ea2pe8" : siteKey.Trim();
            this.userToken = existingToken;

            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                CookieContainer = new CookieContainer(),
                UseCookies = true
            };
            http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
        }

        public void UpdateConfig(string newBaseUrl, string newSiteKey)
        {
            if (!string.IsNullOrWhiteSpace(newBaseUrl)) baseUrl = NormalizeBase(newBaseUrl);
            if (!string.IsNullOrWhiteSpace(newSiteKey)) siteKey = newSiteKey.Trim();
        }

        public string Token => userToken;

        private static string NormalizeBase(string b)
        {
            if (string.IsNullOrWhiteSpace(b)) return "https://srv.kolhalashon.com/api/";
            b = b.Trim();
            if (!b.EndsWith("/")) b += "/";
            return b;
        }

        // מוסיף את כל הכותרות שהשרת דורש. authorize=true מוסיף גם את ה-Bearer של המשתמש (להורדות).
        private HttpRequestMessage BuildRequest(HttpMethod method, string relativeUrl, bool authorize, HttpContent content = null)
        {
            var req = new HttpRequestMessage(method, baseUrl + relativeUrl);
            req.Headers.TryAddWithoutValidation("accept", "application/json, text/plain, */*");
            req.Headers.TryAddWithoutValidation("accept-language", "he-IL,he;q=0.9,en;q=0.8");
            req.Headers.TryAddWithoutValidation("authorization-site-key", "Bearer " + siteKey);
            req.Headers.TryAddWithoutValidation("origin", "https://www2.kolhalashon.com");
            req.Headers.TryAddWithoutValidation("referer", "https://www2.kolhalashon.com/");
            req.Headers.TryAddWithoutValidation("user-agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
            if (authorize && !string.IsNullOrEmpty(userToken))
                req.Headers.TryAddWithoutValidation("authorization", "Bearer " + userToken);
            if (content != null) req.Content = content;
            return req;
        }

        // ----- התחברות -----
        public async Task<bool> LoginAsync(string username, string password, CancellationToken ct = default)
        {
            var payload = JsonSerializer.Serialize(new { Username = username, Password = password });
            var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var req = BuildRequest(HttpMethod.Post, "Accounts/UserLogin/", false, content);
            using var resp = await http.SendAsync(req, ct);
            string body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                throw new KhlException("התחברות נכשלה (קוד " + (int)resp.StatusCode + "). בדקו שם משתמש וסיסמה.");

            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                string token = null;
                foreach (var name in new[] { "Token", "token", "AccessToken", "access_token" })
                    if (root.TryGetProperty(name, out var t) && t.ValueKind == JsonValueKind.String)
                    { token = t.GetString(); break; }
                if (string.IsNullOrEmpty(token))
                    throw new KhlException("התחברות הצליחה אך לא הוחזר טוקן. ייתכן שמבנה ה-API השתנה.");
                userToken = token;
                return true;
            }
            catch (JsonException)
            {
                throw new KhlException("תשובת התחברות לא תקינה מהשרת.");
            }
        }

        public void SetToken(string token) => userToken = token;

        // ----- חיפוש (רבנים / ספרים / שיעורים). מחזיר JSON גולמי שהממשק מפרש. -----
        public async Task<string> SearchAsync(string keyword, CancellationToken ct = default)
        {
            string kw = Uri.EscapeDataString(keyword ?? "");
            using var req = BuildRequest(HttpMethod.Get, $"Search/WebSite_GetSearchItems/{kw}/-1/1/4", false);
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                throw new KhlException("החיפוש נכשל (קוד " + (int)resp.StatusCode + ").");
            return await resp.Content.ReadAsStringAsync();
        }

        // ----- שיעורים של רב (עם דפדוף). מחזיר JSON גולמי (מערך שיעורים). -----
        public async Task<string> GetRavShiurimAsync(int ravId, int fromRow, int numRows, CancellationToken ct = default)
        {
            var body = new
            {
                QueryType = -1,
                LangID = -1,
                MasechetID = -1,
                DafNo = -1,
                MasechetIDY = -1,
                DafNoY = -1,
                MoedID = -1,
                ParashaID = -1,
                EnglishDisplay = false,
                MasechetIDYOz = -1,
                DafNoYOz = -1,
                FromRow = fromRow,
                NumOfRows = numRows,
                PrefferedLanguage = -1,
                SearchOrder = 7,
                FiltersArray = Array.Empty<object>(),
                GeneralID = ravId,
                FilterSwitch = new string('1', 111)
            };
            var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var req = BuildRequest(HttpMethod.Post, "Search/WebSite_GetRavShiurim/", false, content);
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                throw new KhlException("טעינת שיעורי הרב נכשלה (קוד " + (int)resp.StatusCode + ").");
            return await resp.Content.ReadAsStringAsync();
        }

        // ----- פרטי שיעור בודד -----
        public async Task<string> GetShiurDetailsAsync(long fileId, CancellationToken ct = default)
        {
            using var req = BuildRequest(HttpMethod.Get, $"TblShiurimLists/WebSite_GetShiurDetails/{fileId}", false);
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                throw new KhlException("טעינת פרטי השיעור נכשלה (קוד " + (int)resp.StatusCode + ").");
            return await resp.Content.ReadAsStringAsync();
        }

        // ----- מפתח הורדה (דורש התחברות) -----
        public async Task<string> GetDownloadKeyAsync(long fileId, CancellationToken ct = default)
        {
            using var req = BuildRequest(HttpMethod.Get, $"files/checkAutorizationDownload/{fileId}/false", true);
            using var resp = await http.SendAsync(req, ct);
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
                throw new KhlAuthException("נדרשת התחברות לחשבון קול הלשון כדי להוריד.");
            if ((int)resp.StatusCode == 204)
                throw new KhlDownloadBlockedException("קול הלשון חסמו את ההורדה של שיעור זה - ניתן רק להאזין לו באתר.");
            if (!resp.IsSuccessStatusCode)
                throw new KhlException("קבלת מפתח ההורדה נכשלה (קוד " + (int)resp.StatusCode + ").");
            string body = await resp.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(body))
                throw new KhlDownloadBlockedException("קול הלשון חסמו את ההורדה של שיעור זה - ניתן רק להאזין לו באתר.");
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String)
                return k.GetString();
            // ייתכן שם מפתח שונה - ננסה כמה
            foreach (var name in new[] { "Key", "downloadKey", "DownloadKey" })
                if (doc.RootElement.TryGetProperty(name, out var k2) && k2.ValueKind == JsonValueKind.String)
                    return k2.GetString();
            throw new KhlException("השרת לא החזיר מפתח הורדה.");
        }

        // ----- הורדת הקובץ עצמו לזרם, עם דיווח התקדמות -----
        // מחזיר את מספר הבתים שהורדו. progress: (bytesSoFar, totalBytesOrMinusOne).
        public async Task DownloadFileAsync(long fileId, Quality quality, string key, System.IO.Stream target,
            Action<long, long> progress, CancellationToken ct = default)
        {
            string url = $"files/GetFileDownload/{fileId}/{(int)quality}/{key}/null/false/false";
            using var req = BuildRequest(HttpMethod.Get, url, true);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
                throw new KhlAuthException("נדרשת התחברות לחשבון קול הלשון כדי להוריד.");
            if (resp.StatusCode == HttpStatusCode.NoContent || (int)resp.StatusCode == 204)
                throw new KhlException("השרת החזיר תוכן ריק - ייתכן שהאיכות המבוקשת אינה זמינה לשיעור זה.");
            if (!resp.IsSuccessStatusCode)
                throw new KhlException("ההורדה נכשלה (קוד " + (int)resp.StatusCode + ").");

            long total = resp.Content.Headers.ContentLength ?? -1;
            using var src = await resp.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
            {
                await target.WriteAsync(buffer, 0, read, ct);
                done += read;
                progress?.Invoke(done, total);
            }
        }
    }

    public class KhlException : Exception
    {
        public KhlException(string msg) : base(msg) { }
    }

    // שגיאת הרשאה - הממשק מגיב בהצגת מסך התחברות מחדש.
    public class KhlAuthException : KhlException
    {
        public KhlAuthException(string msg) : base(msg) { }
    }

    // קול הלשון חסמו את הורדת השיעור (DisableDownload=true בפרטי השיעור). השרת מחזיר 204
    // ריק על checkAutorizationDownload - אין טעם לנסות שוב או להתחבר מחדש.
    public class KhlDownloadBlockedException : KhlException
    {
        public KhlDownloadBlockedException(string msg) : base(msg) { }
    }
}
