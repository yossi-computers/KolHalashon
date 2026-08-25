# KolHalashon Kiosk — תוכנת הורדת שיעורים מקול הלשון

עמדת קיוסק (Windows Forms + WebView2) לחיפוש והורדה של שיעורים מאתר
[קול הלשון](https://www.kolhalashon.com), כולל הורדה ישירה לנגני MP3,
כרטיסי זיכרון והתקני MTP.

> **הורדה למשתמש קצה:** קובץ ההתקנה המוכן נמצא במאגר הציבורי
> [yossi-computers/KH-Download](https://github.com/yossi-computers/KH-Download/releases/latest).
> המאגר הזה מכיל את קוד המקור בלבד.

---

## מה התוכנה עושה

- **ממשק קיוסק בעברית** — מסך מלא, ניווט לפי רבנים / נושאים / חיפוש חופשי.
- **הורדת שמע, וידאו ו-PDF** — הזרמים של קול הלשון הם HLS בלבד, ולכן
  הווידאו נמשך דרך `hls.js` בנגן ומומר ל-MP4 אמיתי באמצעות `ffmpeg`.
- **הורדה ישירה להתקן** — נגן MP3, אונקי, כרטיס זיכרון או התקן MTP
  (`MtpDevices.cs`, `MediaTargets.cs`), עם תיוג ID3 דרך TagLibSharp.
- **חלון ניהול נעול בסיסמה** (`admin.html`) — הגדרת תיקיית יעד, איכויות
  מותרות, מסך מלא, זמן חוסר פעילות, סיסמאות ועוד.
- **התקנה עצמאית** — ה-EXE הוא single-file self-contained; אם WebView2
  Runtime חסר, הוא מותקן אוטומטית מ-bootstrapper מוטמע.

## מבנה הקוד

| קובץ | תפקיד |
|---|---|
| `Program.cs` | נקודת כניסה, `MainForm`, לוגיקת הקיוסק, גשר JS↔C#, לוג |
| `KhlApi.cs` | קריאות ל-API של קול הלשון (`www.kolhalashon.com/api/`) |
| `KhlBrowser.cs` | דפדוף הקטלוג: רבנים, נושאים, סדרות, תוצאות חיפוש |
| `DownloadManager.cs` | תור הורדות מקבילי, התקדמות, ניסיונות חוזרים |
| `MediaTargets.cs` | זיהוי יעדי שמירה (דיסק, כרטיס, נגן) |
| `MtpDevices.cs` | כתיבה להתקני MTP (טלפונים / נגנים ללא אות כונן) |
| `Ffmpeg.cs` | חילוץ והפעלה של ffmpeg להמרת TS→MP4 |
| `kiosk.html` | כל ממשק המשתמש (מוטמע ב-EXE) |
| `admin.html` | חלון ההגדרות (מוטמע ב-EXE) |
| `default-config.json` | הגדרות ברירת מחדל, מתועדות בעברית בתוך הקובץ |

## בנייה

דרוש **.NET 8 SDK** ו-Windows x64.

```
בנה.bat
```

או ידנית:

```
dotnet publish KolHalashonKiosk.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true -o publish
```

התוצאה: `publish\KolHalashonKiosk.exe` (קובץ יחיד, ~120MB).

### ffmpeg.exe — נדרש להורדת וידאו

`ffmpeg.exe` (כ-115MB) **אינו נמצא במאגר** מפני שהוא חורג ממגבלת 100MB
לקובץ ב-GitHub. הוא מוטמע ב-EXE בזמן ה-build ורק אם הוא קיים
(`Condition="Exists('ffmpeg.exe')"`), כך שהבנייה מצליחה גם בלעדיו —
אבל אז המרת הווידאו ל-MP4 לא תעבוד.

לפני הבנייה הורידו בניית **LGPL של win64** מ-<https://ffmpeg.org/download.html>
והניחו את `ffmpeg.exe` בשורש הפרויקט, לצד `ffmpeg-LICENSE.txt` שכבר נמצא כאן.

## הערות טכניות

- **`baseUrl` חייב להיות `https://www.kolhalashon.com/api/`.** מקור אחר,
  או שליחת כותרת `accept` בבקשה, גורמים ל-Cloudflare לחסום כל קריאה.
- **אין לפתוח חלון, דיאלוג או WebView2 שני מתוך מטפל אירוע של WebView2** —
  זה נועל את ה-UI thread. יש לדחות עם `BeginInvoke`.

## רישיונות של רכיבי צד שלישי

- `hls.js` — Apache-2.0, ראו `hls-LICENSE.txt`
- `ffmpeg` — בניית LGPL, ראו `ffmpeg-LICENSE.txt`
- `Microsoft.Web.WebView2`, `TagLibSharp` — ראו רישיונות החבילות ב-NuGet
