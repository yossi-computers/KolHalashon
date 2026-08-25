using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KolHalashonKiosk
{
    // זיהוי יעדי הורדה: המחשב עצמו + התקני מדיה נשלפים (אונקי, נגן, כרטיס זיכרון, דיסק חיצוני).
    //
    // הזיהוי מבוסס על אותיות כונן (DriveInfo). כל התקן שמתחבר כ"אחסון המוני" (USB Mass Storage)
    // מקבל אות כונן ולכן מזוהה - כך עובדים כל האונקיז, רוב נגני ה-MP3 וכרטיסי SD.
    // פלאפונים מודרניים מתחברים כברירת מחדל בפרוטוקול MTP ("העברת קבצים") ואינם מקבלים אות כונן,
    // ולכן אינם מופיעים כאן; יש להעבירם למצב אחסון המוני, או להוסיף תמיכת MTP בהמשך.
    public static class MediaTargets
    {
        public const string PcId = "pc";

        public class Target
        {
            public string Id;         // "pc" או שורש הכונן ("E:\")
            public string Name;       // שם לתצוגה
            public string Kind;       // "pc" | "removable"
            public string Path;       // תיקיית היעד בפועל
            public long FreeBytes = -1;
            public long TotalBytes = -1;   // -1 = לא ידוע (התקן MTP, שאין לו אות כונן)
            public string FreeText;
        }

        // שם התיקייה שנוצרת בהתקן החיצוני
        public const string DeviceFolderName = "קול הלשון";

        // סריקה מלאה (כוללת MTP) - עלולה להימשך ולכן יש להריץ אותה ב-thread רקע בלבד.
        public static List<Target> List(string pcFolder)
        {
            var list = ListDrivesOnly(pcFolder);

            // התקני MTP (פלאפונים וכד') - אין להם אות כונן, ראו MtpDevices
            try
            {
                var mtp = MtpDevices.List();
                Log.Write("סריקת התקנים: " + (list.Count - 1) + " נשלפים, " + mtp.Count + " MTP");
                foreach (var m in mtp)
                    list.Add(new Target
                    {
                        Id = m.Id,
                        Name = m.Display,
                        Kind = "mtp",
                        Path = m.Display + " \\ " + DeviceFolderName,
                        FreeBytes = -1
                    });
            }
            catch (Exception ex) { Log.Write("סריקת MTP נכשלה: " + ex.Message); }

            // התקן מדומה לבדיקות/תמיכה: KHL_TEST_DEVICE=<נתיב תיקייה> מתנהג כמו התקן נשלף.
            try
            {
                string sim = Environment.GetEnvironmentVariable("KHL_TEST_DEVICE");
                if (!string.IsNullOrWhiteSpace(sim))
                {
                    sim = sim.Trim();
                    list.Add(new Target
                    {
                        Id = sim,
                        Name = "התקן לבדיקה (" + sim + ")",
                        Kind = "removable",
                        Path = System.IO.Path.Combine(sim, DeviceFolderName),
                        FreeBytes = -1
                    });
                }
            }
            catch { }

            return list;
        }

        // כוננים בלבד (מהיר, בטוח לקריאה מכל thread)
        public static List<Target> ListDrivesOnly(string pcFolder)
        {
            var pc = new Target { Id = PcId, Name = "המחשב", Kind = "pc", Path = pcFolder };
            FillSpace(pc, pcFolder);
            var list = new List<Target> { pc };

            try
            {
                foreach (var d in DriveInfo.GetDrives())
                {
                    if (!IsCandidate(d)) continue;
                    string root;
                    string label;
                    long free, total;
                    try
                    {
                        root = d.RootDirectory.FullName;
                        label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "התקן נשלף" : d.VolumeLabel.Trim();
                        free = d.TotalFreeSpace;
                        total = d.TotalSize;
                    }
                    catch { continue; }   // כונן שנשלף בדיוק עכשיו

                    list.Add(new Target
                    {
                        Id = root,
                        Name = label + " (" + root.TrimEnd('\\') + ")",
                        Kind = "removable",
                        Path = System.IO.Path.Combine(root, DeviceFolderName),
                        FreeBytes = free,
                        TotalBytes = total,
                        FreeText = FormatSize(free)
                    });
                }
            }
            catch (Exception ex) { Log.Write("סריקת התקנים נכשלה: " + ex.Message); }

            return list;
        }

        // נפח הכונן שבו יושב נתיב נתון. משמש את "המחשב", שאינו כונן בפני עצמו אלא
        // תיקייה - ולכן הנפח נלקח מהכונן שמכיל אותה.
        private static void FillSpace(Target t, string path)
        {
            try
            {
                string root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(path));
                if (string.IsNullOrEmpty(root)) return;
                var d = new DriveInfo(root);
                if (!d.IsReady) return;
                t.FreeBytes = d.TotalFreeSpace;
                t.TotalBytes = d.TotalSize;
                t.FreeText = FormatSize(t.FreeBytes);
            }
            catch { }
        }

        // כונן נשלף ומוכן לשימוש. כונני רשת ותקליטורים אינם רלוונטיים להורדה, וכונן המערכת מוחרג
        // כדי שלא יופיע פעמיים (הוא כבר "המחשב").
        private static bool IsCandidate(DriveInfo d)
        {
            try
            {
                if (!d.IsReady) return false;
                if (d.DriveType != DriveType.Removable) return false;
                string sys = System.IO.Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System));
                if (string.Equals(d.RootDirectory.FullName, sys, StringComparison.OrdinalIgnoreCase)) return false;
                return true;
            }
            catch { return false; }
        }

        public static string FormatSize(long bytes)
        {
            if (bytes < 0) return "";
            string[] u = { "B", "KB", "MB", "GB", "TB" };
            double v = bytes; int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return (v >= 100 || i <= 1 ? Math.Round(v).ToString() : Math.Round(v, 1).ToString()) + " " + u[i];
        }

        // מאתר את היעד הנבחר; אם ההתקן נותק - חוזרים למחשב.
        // חשוב: מזהה MTP נפתר מתוך המזהה עצמו ובלי לסרוק התקנים, כי סריקת MTP היא פעולת COM
        // חוסמת (ראו MtpDevices) ואין להריץ אותה על thread ה-UI.
        public static Target Resolve(string id, string pcFolder)
        {
            if (MtpDevices.IsMtpId(id))
            {
                var (device, storage) = MtpDevices.ParseId(id);
                string disp = string.IsNullOrEmpty(storage) ? device : device + " · " + storage;
                return new Target
                {
                    Id = id, Name = disp, Kind = "mtp",
                    Path = disp + " \\ " + DeviceFolderName, FreeBytes = -1
                };
            }

            var all = ListDrivesOnly(pcFolder);
            return all.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase))
                   ?? all[0];
        }
    }
}
