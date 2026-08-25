using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KolHalashonKiosk
{
    // מנהל תור ההורדות: מספר הורדות במקביל, דיווח התקדמות לממשק, שמירה בשמות ידידותיים לנגן ותיוג ID3.
    public class DownloadManager
    {
        public class Item
        {
            public long QueueId;        // מזהה פנימי בתור (לא file id)
            public long FileId;
            public string Title;
            public string Rav;
            public string Topic;
            public KhlBrowser.Quality Quality;
            public string Status = "waiting";   // waiting | downloading | done | error | canceled
            public int Percent;
            public long BytesDone;
            public long BytesTotal = -1;
            public long BytesPerSec;            // קצב מוחלק, לחישוב זמן משוער בממשק
            internal long SpeedBytes;           // מצב פנימי לחישוב הקצב
            internal DateTime SpeedTime;
            public string FilePath;
            public string Error;
            public bool Removed;        // הוסר מהרשימה בידי המשתמש - ראו Remove
            public CancellationTokenSource Cts;
        }

        private readonly KhlBrowser api;
        private readonly Func<CancellationToken, Task<bool>> ensureLoggedIn;
        private string downloadFolder;
        private int maxParallel;

        private readonly ConcurrentQueue<Item> waiting = new ConcurrentQueue<Item>();
        private readonly ConcurrentDictionary<long, Item> all = new ConcurrentDictionary<long, Item>();
        private int running = 0;
        private long queueSeq = 0;
        private readonly object pumpGate = new object();

        // מופעל בכל עדכון של פריט (הממשק דוחף התקדמות ל-WebView). רץ תמיד על thread כלשהו - הצרכן צריך למרשל ל-UI.
        public event Action<Item> OnUpdate;

        // מסירה ליעד שאינו מערכת קבצים (התקן MTP): מקבל את הקובץ המקומי המוכן ומעביר אותו להתקן.
        // כשהוא מוגדר, ההורדה יורדת לתיקיית ביניים ורק אז מועברת. null = שמירה רגילה בדיסק.
        public Func<Item, string, System.Threading.Tasks.Task> Deliver;

        public DownloadManager(KhlBrowser api, Func<CancellationToken, Task<bool>> ensureLoggedIn, string downloadFolder, int maxParallel)
        {
            this.api = api;
            this.ensureLoggedIn = ensureLoggedIn;
            this.downloadFolder = downloadFolder;
            this.maxParallel = Math.Max(1, maxParallel);
        }

        public void UpdateConfig(string folder, int parallel)
        {
            downloadFolder = folder;
            maxParallel = Math.Max(1, parallel);
            Pump();
        }

        // קצב ההורדה, מחושב מהפרשי הבתים בין דיווחי ההתקדמות. הדיווחים אינם מגיעים
        // בקצב אחיד, ולכן הערך מוחלק (ממוצע נע מעריכי) - אחרת הזמן המשוער בממשק קופץ.
        // מתעלמים ממקטעים קצרים מחצי שנייה, שבהם הרעש גדול מהאות.
        private static void UpdateSpeed(Item item, long done)
        {
            var now = DateTime.UtcNow;
            if (item.SpeedTime == default) { item.SpeedTime = now; item.SpeedBytes = done; return; }
            double secs = (now - item.SpeedTime).TotalSeconds;
            if (secs < 0.5) return;
            long delta = done - item.SpeedBytes;
            item.SpeedTime = now;
            item.SpeedBytes = done;
            if (delta < 0) return;                       // התחלה מחדש של אותו קובץ
            long sample = (long)(delta / secs);
            item.BytesPerSec = item.BytesPerSec <= 0 ? sample
                             : (long)(item.BytesPerSec * 0.7 + sample * 0.3);
        }

        public IEnumerable<Item> Items => all.Values.OrderBy(i => i.QueueId);

        public Item Enqueue(long fileId, KhlBrowser.Quality quality, string title, string rav, string topic)
        {
            var item = new Item
            {
                QueueId = Interlocked.Increment(ref queueSeq),
                FileId = fileId,
                Title = title ?? ("שיעור " + fileId),
                Rav = rav ?? "",
                Topic = topic ?? "",
                Quality = quality,
                Cts = new CancellationTokenSource()
            };
            all[item.QueueId] = item;
            waiting.Enqueue(item);
            Notify(item);
            Pump();
            return item;
        }

        public void Cancel(long queueId)
        {
            if (all.TryGetValue(queueId, out var item))
            {
                try { item.Cts?.Cancel(); } catch { }
                if (item.Status == "waiting")
                {
                    item.Status = "canceled";
                    Notify(item);
                }
            }
        }

        // הסרה של פריט בודד מהרשימה. הורדה שעדיין רצה מבוטלת תחילה; קובץ שכבר ירד
        // נשאר במקומו על הדיסק - ההסרה נוגעת לרשימה בלבד.
        // הסימון Removed נחוץ כי משימת ההורדה שכבר רצה עוד תשלח עדכון סיום אחרי
        // הביטול, ובלעדיו הפריט היה נדחף בחזרה לרשימה שבממשק.
        public void Remove(long queueId)
        {
            if (!all.TryRemove(queueId, out var item)) return;
            item.Removed = true;
            try { item.Cts?.Cancel(); } catch { }
        }

        public void ClearFinished()
        {
            foreach (var kv in all)
                if (kv.Value.Status is "done" or "error" or "canceled")
                    all.TryRemove(kv.Key, out _);
        }

        private void Pump()
        {
            lock (pumpGate)
            {
                while (running < maxParallel && waiting.TryDequeue(out var item))
                {
                    if (item.Status == "canceled") continue;
                    Interlocked.Increment(ref running);
                    _ = Task.Run(() => ProcessAsync(item));
                }
            }
        }

        private async Task ProcessAsync(Item item)
        {
            try
            {
                var ct = item.Cts.Token;
                item.Status = "downloading";
                item.Percent = 0;
                Notify(item);

                // כל סוגי ההורדה עוברים כיום בכתובות הישירות של האתר ואינם דורשים התחברות:
                // שמע ב-GetMp3FileToPlay, PDF בקישור סטטי, ווידאו בזרם ה-HLS.

                string finalPath = BuildFilePath(item);
                item.FilePath = finalPath;
                Directory.CreateDirectory(Path.GetDirectoryName(finalPath));

                // אם כבר קיים קובץ תקין - דלג (מונע הורדה כפולה, וגם חוסך בקשת אישור מיותרת מהשרת)
                if (File.Exists(finalPath) && new FileInfo(finalPath).Length > 0)
                {
                    item.Percent = 100;
                    item.Status = "done";
                    Notify(item);
                    return;
                }

                void OnProgress(long done, long total)
                {
                    item.BytesDone = done;
                    item.BytesTotal = total;
                    UpdateSpeed(item, done);
                    int pct = total > 0 ? (int)(done * 100 / total) : -1;
                    if (pct != item.Percent) { item.Percent = pct; Notify(item); }
                }

                bool isVideo = item.Quality == KhlBrowser.Quality.Video || item.Quality == KhlBrowser.Quality.HdVideo;
                if (isVideo)
                {
                    // וידאו אינו קובץ אחד בשרת - הוא נאסף ממקטעי HLS (MPEG-TS) ורק אז מומר
                    // ל-MP4 אמיתי ב-remux מהיר, בלי קידוד מחדש. ראו DownloadVideoToFileAsync ו-Ffmpeg.
                    string tsPath = Path.ChangeExtension(finalPath, ".ts");
                    item.FilePath = tsPath;     // כדי שביטול ינקה את ה-part הנכון
                    await api.DownloadVideoToFileAsync(item.FileId, item.Quality, tsPath, OnProgress, ct);

                    item.Status = "converting";
                    item.Percent = -1;
                    Notify(item);

                    if (await Ffmpeg.RemuxToMp4Async(tsPath, finalPath, ct))
                    {
                        try { File.Delete(tsPath); } catch { }
                        item.FilePath = finalPath;
                    }
                    else
                    {
                        // בלי ffmpeg תקין עדיף להשאיר את ה-TS מאשר לא לספק כלום - הוא נגן בפני עצמו.
                        Log.Write("המרת הווידאו נכשלה - נשמר כ-TS: " + tsPath);
                        try { if (File.Exists(finalPath)) File.Delete(finalPath); } catch { }
                        item.FilePath = tsPath;
                    }
                    item.Status = "downloading";
                }
                else if (item.Quality == KhlBrowser.Quality.Pdf)
                {
                    // PDF לא יכול לרדת דרך WebView2 - הוא נפתח שם במציג המובנה. ראו ההסבר שם.
                    await api.DownloadFileDirectAsync(KhlBrowser.DirectPdfUrl(item.FileId), finalPath, OnProgress, ct);
                }
                else
                {
                    // ההורדה מתבצעת ע"י מנגנון ההורדה של WebView2 (עובר את Cloudflare), וזורמת לדיסק.
                    await api.DownloadToFileAsync(item.FileId, item.Quality, () => GetKeyWithRetryAsync(item.FileId, ct), finalPath, OnProgress, ct);
                }

                finalPath = item.FilePath;   // בווידאו ייתכן שנשמר כ-TS בגלל כשל בהמרה
                TryTag(item, finalPath);

                // יעד MTP: הקובץ ירד לתיקיית ביניים ועכשיו מועתק להתקן עצמו
                var deliver = Deliver;
                if (deliver != null)
                {
                    item.Status = "copying";
                    item.Percent = -1;
                    Notify(item);
                    await deliver(item, finalPath);
                    try { if (File.Exists(finalPath)) File.Delete(finalPath); } catch { }
                }

                item.Percent = 100;
                item.Status = "done";
                Notify(item);
            }
            catch (OperationCanceledException)
            {
                item.Status = "canceled";
                CleanupPart(item);
                Notify(item);
            }
            catch (KhlDownloadBlockedException ex)
            {
                // חסימה מצד קול הלשון - לא תקלה; לוג קצר בלי stack trace.
                item.Status = "error";
                item.Error = ex.Message;
                Log.Write("הורדה חסומה (" + item.FileId + "): " + ex.Message);
                CleanupPart(item);
                Notify(item);
            }
            catch (Exception ex)
            {
                item.Status = "error";
                item.Error = ex.Message;
                Log.Write("הורדה נכשלה (" + item.FileId + "): " + ex);
                CleanupPart(item);
                Notify(item);
            }
            finally
            {
                Interlocked.Decrement(ref running);
                Pump();
            }
        }

        // מפתח ההורדה עלול לפוג בין קבלתו לשימוש; אם נכשל בהרשאה - התחבר שוב פעם אחת ונסה שוב.
        private async Task<string> GetKeyWithRetryAsync(long fileId, CancellationToken ct)
        {
            try { return await api.GetDownloadKeyAsync(fileId); }
            catch (KhlAuthException)
            {
                bool ok = await ensureLoggedIn(ct);
                if (!ok) throw;
                return await api.GetDownloadKeyAsync(fileId);
            }
        }

        private void CleanupPart(Item item)
        {
            try
            {
                if (!string.IsNullOrEmpty(item.FilePath))
                {
                    string part = item.FilePath + ".part";
                    if (File.Exists(part)) File.Delete(part);
                }
            }
            catch { }
        }

        private string BuildFilePath(Item item)
        {
            string root = string.IsNullOrWhiteSpace(downloadFolder)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "קול הלשון")
                : downloadFolder;

            string ext = item.Quality switch
            {
                KhlBrowser.Quality.Audio => ".mp3",
                KhlBrowser.Quality.Pdf => ".pdf",
                _ => ".mp4",
            };
            string ravFolder = Sanitize(string.IsNullOrWhiteSpace(item.Rav) ? "שיעורים" : item.Rav);
            string name = Sanitize(item.Title);
            if (string.IsNullOrWhiteSpace(name)) name = "שיעור_" + item.FileId;

            // צירוף מזהה השיעור לשם - מונע דריסה בין שיעורים בעלי כותרת זהה, ומקל על איתור.
            string file = name + " [" + item.FileId + "]" + ext;
            return Path.Combine(root, ravFolder, file);
        }

        // מסיר תווים אסורים בשם קובץ/תיקייה ומקצר אורך מוגזם.
        private static string Sanitize(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder();
            foreach (char c in s.Trim())
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? ' ' : c);
            string res = sb.ToString().Trim();
            res = string.Join(" ", res.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            if (res.Length > 120) res = res.Substring(0, 120).Trim();
            return res;
        }

        // תיוג ID3 בעברית (כותרת/רב/נושא) - חשוב לנגנים ישנים שמציגים לפי תגיות. כשל בתיוג לא מפיל הורדה.
        private void TryTag(Item item, string path)
        {
            if (item.Quality != KhlBrowser.Quality.Audio) return;   // תיוג רק לאודיו
            try
            {
                using var tf = TagLib.File.Create(path);
                tf.Tag.Title = item.Title;
                if (!string.IsNullOrWhiteSpace(item.Rav))
                {
                    tf.Tag.Performers = new[] { item.Rav };
                    tf.Tag.AlbumArtists = new[] { item.Rav };
                }
                if (!string.IsNullOrWhiteSpace(item.Topic))
                    tf.Tag.Album = item.Topic;
                tf.Tag.Comment = "קול הלשון - " + item.FileId;
                tf.Save();
            }
            catch { /* תיוג נכשל - הקובץ עדיין תקין */ }
        }

        private void Notify(Item item)
        {
            if (item.Removed) return;
            try { OnUpdate?.Invoke(item); } catch { }
        }
    }
}
