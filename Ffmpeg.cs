using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace KolHalashonKiosk
{
    // המרת הווידאו לקובץ MP4 אמיתי.
    //
    // הווידאו של קול הלשון זמין רק כזרם HLS, ולכן הוא יורד כמקטעי MPEG-TS משורשרים.
    // TS הוא קונטיינר תקין, אבל נגנים ומכשירים רבים מצפים ל-MP4. כאן מתבצע remux:
    // אותם זרמי וידאו ושמע בדיוק מועברים לקונטיינר MP4 בלי קידוד מחדש (-c copy).
    // זו פעולה מהירה (שניות בודדות) וללא כל אובדן איכות.
    //
    // ffmpeg.exe מוטמע בתוך ה-EXE ונחלץ בשימוש הראשון לתיקיית הנתונים של התוכנה.
    // מדובר בבניית LGPL (ללא רכיבי GPL), המורצת כתהליך נפרד. רישיון הבינארי מצורף
    // לפרויקט כ-ffmpeg-LICENSE.txt.
    public static class Ffmpeg
    {
        private const string ResourceSuffix = "ffmpeg.exe";
        private static readonly object gate = new object();
        private static string cached;

        // מחלץ את ffmpeg.exe אם צריך ומחזיר את נתיבו, או null אם אינו זמין.
        public static string EnsureExtracted()
        {
            lock (gate)
            {
                if (cached != null && File.Exists(cached)) return cached;
                try
                {
                    var asm = Assembly.GetExecutingAssembly();
                    string res = null;
                    foreach (var n in asm.GetManifestResourceNames())
                        if (n.EndsWith(ResourceSuffix, StringComparison.OrdinalIgnoreCase)) { res = n; break; }
                    if (res == null) { Log.Write("ffmpeg אינו מוטמע בתוכנה"); return null; }

                    string dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KolHalashonKiosk");
                    Directory.CreateDirectory(dir);
                    string path = Path.Combine(dir, "ffmpeg.exe");

                    using var s = asm.GetManifestResourceStream(res);

                    // כבר חולץ בהפעלה קודמת (השוואת גודל מספיקה - הבינארי מתחלף רק עם גרסה חדשה)
                    if (File.Exists(path) && new FileInfo(path).Length == s.Length) { cached = path; return path; }

                    // כתיבה לקובץ זמני והחלפה, כדי ששתי הפעלות במקביל לא ידרסו זו את זו
                    string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    using (var f = File.Create(tmp)) s.CopyTo(f);
                    try { File.Move(tmp, path, true); }
                    catch
                    {
                        // הקובץ נעול (ffmpeg רץ כרגע מהפעלה אחרת) - הקיים תקין דיו
                        try { File.Delete(tmp); } catch { }
                        if (!File.Exists(path)) throw;
                    }

                    Log.Write("ffmpeg חולץ אל " + path);
                    cached = path;
                    return path;
                }
                catch (Exception ex)
                {
                    Log.Write("חילוץ ffmpeg נכשל: " + ex.Message);
                    return null;
                }
            }
        }

        // המרה ל-MP4. מחזיר true רק אם נוצר קובץ תקין.
        public static async Task<bool> RemuxToMp4Async(string input, string output, CancellationToken ct)
        {
            string exe = EnsureExtracted();
            if (exe == null) return false;

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            // ArgumentList מטפל בציטוט בעצמו - חשוב, כי הנתיבים כאן בעברית ועם רווחים.
            foreach (var a in new[] { "-y", "-hide_banner", "-loglevel", "error",
                                      "-i", input, "-c", "copy", "-movflags", "+faststart", output })
                psi.ArgumentList.Add(a);

            try
            {
                using var p = Process.Start(psi);
                if (p == null) return false;

                var errTask = p.StandardError.ReadToEndAsync();
                _ = p.StandardOutput.ReadToEndAsync();

                using (ct.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch { } }))
                    await p.WaitForExitAsync(ct);

                string err = await errTask;
                if (p.ExitCode != 0)
                {
                    Log.Write("ffmpeg נכשל (קוד " + p.ExitCode + "): " + Trim(err));
                    return false;
                }
                if (!File.Exists(output) || new FileInfo(output).Length == 0)
                {
                    Log.Write("ffmpeg הסתיים אך לא נוצר קובץ פלט.");
                    return false;
                }
                if (!string.IsNullOrWhiteSpace(err)) Log.Write("ffmpeg: " + Trim(err));
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Write("הרצת ffmpeg נכשלה: " + ex.Message);
                return false;
            }
        }

        private static string Trim(string s) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length > 400 ? s.Substring(0, 400) : s).Replace("\r", " ").Replace("\n", " ");
    }
}
