using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace KolHalashonKiosk
{
    // תמיכה בהתקנים בפרוטוקול MTP - פלאפונים, טאבלטים וחלק מהנגנים.
    //
    // התקן MTP אינו מקבל אות כונן ולכן אינו נראה ל-DriveInfo כלל; הוא מופיע רק במרחב השמות של
    // סייר Windows ("המחשב שלי"), ואליו ניגשים דרך Shell.Application ב-COM. אין דרך להוריד אליו
    // ישירות מהדפדפן, ולכן ההורדה יורדת קודם לתיקיית ביניים במחשב ורק אז מועתקת להתקן.
    //
    // כל קריאות ה-COM כאן חייבות לרוץ ב-thread מסוג STA (דרישה של Shell), ולכן כל פעולה מורצת
    // ב-thread ייעודי. הן נקראות מ-thread רקע של מנהל ההורדות, כך שהמתנה כאן אינה תוקעת את הממשק.
    public static class MtpDevices
    {
        private const int SSF_DRIVES = 17;         // "המחשב שלי" במרחב השמות של Shell
        public const string IdPrefix = "mtp:";

        public class MtpTarget
        {
            public string Id;          // mtp:<שם התקן>|<שם אחסון>
            public string Device;
            public string Storage;
            public string Display;
        }

        public static string MakeId(string device, string storage) => IdPrefix + device + "|" + storage;

        public static bool IsMtpId(string id) =>
            !string.IsNullOrEmpty(id) && id.StartsWith(IdPrefix, StringComparison.OrdinalIgnoreCase);

        public static (string device, string storage) ParseId(string id)
        {
            string rest = id.Substring(IdPrefix.Length);
            int i = rest.IndexOf('|');
            return i < 0 ? (rest, "") : (rest.Substring(0, i), rest.Substring(i + 1));
        }

        // ----- הרצת פעולת COM ב-thread מסוג STA -----
        private static T RunSta<T>(Func<T> work, int timeoutMs = 240000)
        {
            T result = default;
            Exception error = null;
            var t = new Thread(() =>
            {
                try { result = work(); }
                catch (Exception ex) { error = ex; }
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            if (!t.Join(timeoutMs)) throw new KhlException("הפעולה בהתקן לא הסתיימה בזמן.");
            if (error != null) throw error;
            return result;
        }

        private static dynamic CreateShell()
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type == null) throw new KhlException("Shell.Application אינו זמין במערכת.");
            return Activator.CreateInstance(type);
        }

        // ----- איתור התקנים -----
        // התקן נייד מזוהה בכך שהוא תיקייה שאינה חלק ממערכת הקבצים (IsFileSystem=false).
        // כל "אחסון" בתוכו (זיכרון פנימי / כרטיס SD) מוצג כיעד נפרד.
        public static List<MtpTarget> List()
        {
            try
            {
                return RunSta(() =>
                {
                    var found = new List<MtpTarget>();
                    dynamic shell = CreateShell();
                    dynamic computer = shell.NameSpace(SSF_DRIVES);
                    if (computer == null) return found;

                    foreach (dynamic item in computer.Items())
                    {
                        string devName;
                        bool isDevice;
                        try
                        {
                            isDevice = item.IsFolder && !item.IsFileSystem;
                            devName = item.Name as string;
                        }
                        catch { continue; }
                        if (!isDevice || string.IsNullOrWhiteSpace(devName)) continue;

                        dynamic devFolder = null;
                        try { devFolder = item.GetFolder; } catch { }
                        if (devFolder == null) continue;

                        bool anyStorage = false;
                        try
                        {
                            foreach (dynamic st in devFolder.Items())
                            {
                                try
                                {
                                    if (!st.IsFolder) continue;
                                    string stName = st.Name as string;
                                    if (string.IsNullOrWhiteSpace(stName)) continue;
                                    anyStorage = true;
                                    found.Add(new MtpTarget
                                    {
                                        Id = MakeId(devName, stName),
                                        Device = devName,
                                        Storage = stName,
                                        Display = devName + " · " + stName
                                    });
                                }
                                catch { }
                            }
                        }
                        catch { }

                        // התקן ללא רשימת אחסונים (או שנעול) - עדיין מוצג, ההעתקה תיעשה לשורש שלו
                        if (!anyStorage)
                            found.Add(new MtpTarget
                            {
                                Id = MakeId(devName, ""),
                                Device = devName,
                                Storage = "",
                                Display = devName
                            });
                    }
                    return found;
                }, 30000);
            }
            catch (Exception ex)
            {
                Log.Write("סריקת התקני MTP נכשלה: " + ex.Message);
                return new List<MtpTarget>();
            }
        }

        // ----- העתקת קובץ להתקן -----
        // subFolders: שרשרת התיקיות בתוך ההתקן (למשל "קול הלשון" / שם הרב) - נוצרות אם אינן קיימות.
        public static void CopyFile(string deviceName, string storageName, string[] subFolders, string localFile)
        {
            if (!File.Exists(localFile)) throw new KhlException("הקובץ להעתקה לא נמצא.");
            string fileName = Path.GetFileName(localFile);
            long size = new FileInfo(localFile).Length;

            RunSta<object>(() =>
            {
                dynamic shell = CreateShell();
                dynamic computer = shell.NameSpace(SSF_DRIVES);
                if (computer == null) throw new KhlException("לא ניתן לגשת לרשימת ההתקנים.");

                dynamic devFolder = FindChildFolder(computer, deviceName)
                    ?? throw new KhlException("ההתקן \"" + deviceName + "\" אינו מחובר.");

                dynamic root = devFolder;
                if (!string.IsNullOrEmpty(storageName))
                    root = FindChildFolder(devFolder, storageName)
                        ?? throw new KhlException("האחסון \"" + storageName + "\" לא נמצא בהתקן. ייתכן שהמסך נעול.");

                dynamic dest = root;
                foreach (var name in subFolders)
                {
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    dest = GetOrCreateFolder(dest, name.Trim());
                }

                dynamic srcFolder = shell.NameSpace(Path.GetDirectoryName(localFile));
                dynamic srcItem = srcFolder?.ParseName(fileName);
                if (srcItem == null) throw new KhlException("לא ניתן לקרוא את הקובץ שהורד.");

                // אם כבר קיים שם קובץ זהה - מוחקים כדי שההעתקה לא תיצור "(1)" או תיתקע בשאלה
                try
                {
                    dynamic old = dest.ParseName(fileName);
                    if (old != null) { old.InvokeVerb("delete"); Thread.Sleep(700); }
                }
                catch { }

                // 4=ללא חלון התקדמות, 16=כן לכולם, 512=ללא אישור יצירת תיקייה, 1024=ללא הודעות שגיאה
                dest.CopyHere(srcItem, 4 | 16 | 512 | 1024);

                WaitForFile(dest, fileName, size);
                return null;
            });
        }

        private static dynamic FindChildFolder(dynamic parent, string name)
        {
            try
            {
                foreach (dynamic it in parent.Items())
                {
                    try
                    {
                        if (string.Equals(it.Name as string, name, StringComparison.OrdinalIgnoreCase))
                            return it.GetFolder;
                    }
                    catch { }
                }
            }
            catch { }
            return null;
        }

        private static dynamic GetOrCreateFolder(dynamic parent, string name)
        {
            var existing = FindChildFolder(parent, name);
            if (existing != null) return existing;

            try { parent.NewFolder(name); }
            catch (Exception ex) { throw new KhlException("לא ניתן ליצור בהתקן את התיקייה \"" + name + "\": " + ex.Message); }

            // יצירת תיקייה בהתקן MTP אינה מיידית
            for (int i = 0; i < 20; i++)
            {
                Thread.Sleep(400);
                var f = FindChildFolder(parent, name);
                if (f != null) return f;
            }
            throw new KhlException("התיקייה \"" + name + "\" לא נוצרה בהתקן.");
        }

        // ההעתקה של Shell היא אסינכרונית ואינה מדווחת סיום - ממתינים להופעת הקובץ ולייצוב גודלו.
        private static void WaitForFile(dynamic dest, string fileName, long expectedSize)
        {
            string lastSize = null;
            int stable = 0;
            for (int i = 0; i < 600; i++)   // עד 5 דקות
            {
                Thread.Sleep(500);
                dynamic item = null;
                try { item = dest.ParseName(fileName); } catch { }
                if (item == null) { stable = 0; continue; }

                string sz = null;
                try { sz = dest.GetDetailsOf(item, 1) as string; } catch { }
                if (sz == lastSize) { if (++stable >= 3) return; }   // הגודל לא משתנה => ההעתקה הסתיימה
                else { stable = 0; lastSize = sz; }
            }
            throw new KhlException("ההעתקה להתקן לא הסתיימה בזמן.");
        }
    }
}
