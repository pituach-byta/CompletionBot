using Microsoft.AspNetCore.Mvc;
using OfficeOpenXml; 
using CompletionBot.Server.Services;
using CompletionBot.Server.Models;
using Dapper;
using System.IO.Compression; // הוספנו את זה בשביל ה-ZIP
using System.Data.SqlClient;

namespace CompletionBot.Server.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AdminController : ControllerBase
    {
        private readonly DbService _dbService;
        private readonly IWebHostEnvironment _env;

        public AdminController(DbService dbService, IWebHostEnvironment env)
        {
            _dbService = dbService;
            _env = env;
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
        }

        public class LoginRequest { public string Password { get; set; } = ""; }

        private class DbDebtRow
        {
            public string StudentID { get; set; } = "";
            public string LessonName { get; set; } = "";
            public int LessonNumber { get; set; }
            public string? MaterialLink { get; set; }
            public int Hours { get; set; }
            public bool IsSubmitted { get; set; }
            public bool IsPaid { get; set; }
            public string? FirstName { get; set; }
            public string? LastName { get; set; }
        }

        [HttpPost("login")]
        public IActionResult Login([FromBody] LoginRequest request)
        {
            if (request.Password == "Admin1234") return Ok(new { token = "admin-ok" });
            return Unauthorized("סיסמה שגויה");
        }

        // --- קבלת מידע, הורדת קובץ והעלאת אקסל ---
        [HttpGet("current-file-info")]
        public IActionResult GetCurrentFileInfo()
        {
            var path = Path.Combine(_env.ContentRootPath, "Data", "Current_Debts.xlsx");
            if (!System.IO.File.Exists(path)) return Ok(new { exists = false });
            var fileInfo = new FileInfo(path);
            return Ok(new { exists = true, lastModified = fileInfo.LastWriteTime, fileName = "Current_Debts.xlsx" });
        }

        [HttpGet("download-current")]
        public IActionResult DownloadCurrentFile()
        {
            var path = Path.Combine(_env.ContentRootPath, "Data", "Current_Debts.xlsx");
            if (!System.IO.File.Exists(path)) return NotFound("לא נמצא קובץ");
            return File(System.IO.File.ReadAllBytes(path), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "Current_Debts.xlsx");
        }

        // --- השוואת קובץ אקסל מול מסד הנתונים ללא שמירה ---
        [HttpPost("compare-excel")]
        public async Task<IActionResult> CompareExcel(IFormFile file)
        {
            if (file == null || file.Length == 0) return BadRequest("לא נבחר קובץ");
            if (Path.GetExtension(file.FileName).ToLower() != ".xlsx") return BadRequest("נא להעלות קובץ .xlsx בלבד");
            try
            {
                using var stream = new MemoryStream();
                await file.CopyToAsync(stream);
                stream.Position = 0;

                using var package = new ExcelPackage(stream);
                var worksheet = package.Workbook.Worksheets[0];
                var rowCount = worksheet.Dimension?.Rows ?? 0;
                var colCount = worksheet.Dimension?.Columns ?? 0;
                if (rowCount < 2) return BadRequest("הקובץ נראה ריק");

                var colMap = new Dictionary<string, int>();
                for (int col = 1; col <= colCount; col++)
                {
                    var header = worksheet.Cells[1, col].Value?.ToString()?.Trim();
                    if (!string.IsNullOrEmpty(header)) colMap[header] = col;
                }

                var requiredHeaders = new[] { "ת. זהות", "שם שיעור", "שעות", "מספר שיעור" };
                foreach (var h in requiredHeaders)
                    if (!colMap.ContainsKey(h)) return BadRequest($"חסרה עמודה חובה: {h}");

                string GetVal(int row, string colName) =>
                    colMap.TryGetValue(colName, out int idx) ? worksheet.Cells[row, idx].Value?.ToString()?.Trim() ?? "" : "";

                bool IsUrl(string path) => Uri.TryCreate(path, UriKind.Absolute, out var uriResult)
                    && (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps);

                // בנייה מקובץ האקסל
                var excelStudents = new Dictionary<string, (string FirstName, string LastName)>();
                var excelDebts = new Dictionary<string, (string LessonName, int LessonNumber, string MaterialLink, int Hours)>();
                for (int row = 2; row <= rowCount; row++)
                {
                    var sid = GetVal(row, "ת. זהות");
                    if (string.IsNullOrEmpty(sid)) continue;
                    if (!excelStudents.ContainsKey(sid))
                        excelStudents[sid] = (GetVal(row, "שם פרטי משתלמת"), GetVal(row, "שם משפחה משתלמת"));
                    string rawNum = GetVal(row, "מספר שיעור").Replace(".0", "").Trim();
                    int.TryParse(rawNum, out int lessonNum);
                    int.TryParse(GetVal(row, "שעות"), out int hours);
                    var lessonName = GetVal(row, "שם שיעור");
                    var link = GetVal(row, "קישור לעבודה");
                    var key = $"{sid}|{lessonName}|{lessonNum}";
                    if (!excelDebts.ContainsKey(key))
                        excelDebts[key] = (lessonName, lessonNum, link, hours);
                }

                // שאילתות מהמסד
                using var conn = _dbService.CreateConnection();
                var dbStudents = (await conn.QueryAsync<Student>(
                    "SELECT StudentID, FirstName, LastName FROM Students WHERE Status = 'Active'"
                )).ToDictionary(s => s.StudentID);

                var dbDebtsList = await conn.QueryAsync<DbDebtRow>(@"
                    SELECT sd.StudentID, sd.LessonName, sd.LessonNumber,
                           sd.MaterialLink, sd.Hours, sd.IsSubmitted, sd.IsPaid,
                           s.FirstName, s.LastName
                    FROM StudentDebts sd
                    JOIN Students s ON sd.StudentID = s.StudentID
                    WHERE sd.IsActive = 1");
                var dbDebts = dbDebtsList.ToDictionary(
                    d => $"{d.StudentID}|{d.LessonName ?? ""}|{d.LessonNumber}");

                // כל המפתחות של קורסים במסד (כולל IsActive=0) — לזיהוי קורסים שחוזרים לפעילות
                var allDbDebtKeys = new HashSet<string>(await conn.QueryAsync<string>(@"
                    SELECT StudentID + '|' + ISNULL(LessonName,'') + '|' + CAST(LessonNumber AS NVARCHAR)
                    FROM StudentDebts"));

                var studentsWithHistory = new HashSet<string>(await conn.QueryAsync<string>(
                    "SELECT DISTINCT StudentID FROM StudentDebts WHERE IsPaid = 1 OR IsSubmitted = 1"));

                // --- השוואה ---
                var newStudents = excelStudents.Keys
                    .Where(id => !dbStudents.ContainsKey(id))
                    .Select(id => new { studentId = id, firstName = excelStudents[id].FirstName, lastName = excelStudents[id].LastName })
                    .OrderBy(x => x.lastName).ToList();

                var deletedSafe = dbStudents.Keys
                    .Where(id => !excelStudents.ContainsKey(id) && !studentsWithHistory.Contains(id))
                    .Select(id => new { studentId = id, firstName = dbStudents[id].FirstName ?? "", lastName = dbStudents[id].LastName ?? "" })
                    .OrderBy(x => x.lastName).ToList();

                var deletedProtected = dbStudents.Keys
                    .Where(id => !excelStudents.ContainsKey(id) && studentsWithHistory.Contains(id))
                    .Select(id => new { studentId = id, firstName = dbStudents[id].FirstName ?? "", lastName = dbStudents[id].LastName ?? "", reason = "יש הגשות/תשלומים - לא תימחק" })
                    .OrderBy(x => x.lastName).ToList();

                var newCourses = excelDebts.Keys
                    .Where(k => !dbDebts.ContainsKey(k))
                    .Select(k =>
                    {
                        var parts = k.Split('|');
                        var sid2 = parts[0];
                        var d = excelDebts[k];
                        var sName = excelStudents.TryGetValue(sid2, out var sn) ? $"{sn.FirstName} {sn.LastName}".Trim() : sid2;
                        bool autoSubmit = !string.IsNullOrEmpty(d.MaterialLink) && !IsUrl(d.MaterialLink);
                        bool isReactivation = allDbDebtKeys.Contains(k);
                        return new { studentId = sid2, studentName = sName, lessonName = d.LessonName, lessonNumber = d.LessonNumber, isAutoSubmitted = autoSubmit, isReactivation };
                    })
                    .OrderBy(x => x.studentName).ToList();

                var removedCourses = dbDebts.Keys
                    .Where(k => !excelDebts.ContainsKey(k))
                    .Select(k =>
                    {
                        var d = dbDebts[k];
                        return new
                        {
                            studentId = d.StudentID,
                            studentName = $"{d.FirstName} {d.LastName}".Trim(),
                            lessonName = d.LessonName,
                            lessonNumber = d.LessonNumber,
                            isSubmitted = d.IsSubmitted,
                            isPaid = d.IsPaid,
                            hasActivity = d.IsSubmitted || d.IsPaid
                        };
                    })
                    .OrderByDescending(x => x.hasActivity).ThenBy(x => x.studentName).ToList();

                var changedLinks = dbDebts.Keys
                    .Where(k => excelDebts.ContainsKey(k))
                    .Select(k => new { d = dbDebts[k], ex = excelDebts[k] })
                    .Where(x => (x.d.MaterialLink ?? "") != x.ex.MaterialLink)
                    .Select(x => new
                    {
                        studentId = x.d.StudentID,
                        studentName = $"{x.d.FirstName} {x.d.LastName}".Trim(),
                        lessonName = x.d.LessonName,
                        lessonNumber = x.d.LessonNumber,
                        oldLink = x.d.MaterialLink ?? "",
                        newLink = x.ex.MaterialLink,
                        isSubmitted = x.d.IsSubmitted,
                        willUpdateLink = !x.d.IsSubmitted
                    })
                    .OrderByDescending(x => x.isSubmitted).ThenBy(x => x.studentName).ToList();

                var changedHours = dbDebts.Keys
                    .Where(k => excelDebts.ContainsKey(k))
                    .Select(k => new { d = dbDebts[k], ex = excelDebts[k] })
                    .Where(x => x.d.Hours != x.ex.Hours)
                    .Select(x => new
                    {
                        studentId = x.d.StudentID,
                        studentName = $"{x.d.FirstName} {x.d.LastName}".Trim(),
                        lessonName = x.d.LessonName,
                        oldHours = x.d.Hours,
                        newHours = x.ex.Hours,
                        isSubmitted = x.d.IsSubmitted,
                        willUpdate = !x.d.IsSubmitted
                    })
                    .OrderByDescending(x => x.isSubmitted).ThenBy(x => x.studentName).ToList();

                return Ok(new
                {
                    summary = new
                    {
                        newStudents = newStudents.Count,
                        deletedStudentsSafe = deletedSafe.Count,
                        deletedStudentsProtected = deletedProtected.Count,
                        newCourses = newCourses.Count,
                        removedCourses = removedCourses.Count,
                        changedLinks = changedLinks.Count,
                        changedHours = changedHours.Count,
                        totalChanges = newStudents.Count + deletedSafe.Count + deletedProtected.Count
                            + newCourses.Count + removedCourses.Count + changedLinks.Count + changedHours.Count
                    },
                    details = new
                    {
                        newStudents,
                        deletedStudentsSafe = deletedSafe,
                        deletedStudentsProtected = deletedProtected,
                        newCourses,
                        removedCourses,
                        changedLinks,
                        changedHours
                    }
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, "שגיאה בהשוואה: " + ex.Message);
            }
        }

 [HttpPost("upload-excel")]
public async Task<IActionResult> UploadExcel(IFormFile file)
{
    if (file == null || file.Length == 0) return BadRequest("לא נבחר קובץ או שהקובץ ריק");
    
    var extension = Path.GetExtension(file.FileName).ToLower();
    if (extension != ".xlsx") return BadRequest("נא להעלות קובץ אקסל בפורמט .xlsx בלבד");

    try
    {
        var dataFolder = Path.Combine(_env.ContentRootPath, "Data");
        if (!Directory.Exists(dataFolder)) Directory.CreateDirectory(dataFolder);
        var filePath = Path.Combine(dataFolder, "Current_Debts.xlsx");
        
        using (var stream = new FileStream(filePath, FileMode.Create)) { await file.CopyToAsync(stream); }

        using (var package = new ExcelPackage(new FileInfo(filePath)))
        {
            var worksheet = package.Workbook.Worksheets[0];
            var rowCount = worksheet.Dimension?.Rows ?? 0;
            var colCount = worksheet.Dimension?.Columns ?? 0;

            if (rowCount < 2) return BadRequest("הקובץ נראה ריק");

            var colMap = new Dictionary<string, int>();
            for (int col = 1; col <= colCount; col++)
            {
                var header = worksheet.Cells[1, col].Value?.ToString()?.Trim();
                if (!string.IsNullOrEmpty(header)) colMap[header] = col;
            }

            var requiredHeaders = new[] { "ת. זהות", "שם שיעור", "שעות", "מספר שיעור" };
            foreach (var header in requiredHeaders)
            {
                if (!colMap.ContainsKey(header)) return BadRequest($"חסרה עמודה חובה: {header}");
            }

            string GetVal(int row, string colName) => 
                colMap.TryGetValue(colName, out int colIndex) ? worksheet.Cells[row, colIndex].Value?.ToString()?.Trim() ?? "" : "";
            
            bool IsUrl(string path) => Uri.TryCreate(path, UriKind.Absolute, out var uriResult) 
                               && (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps);

            var studentIdsInExcel = new List<string>();
            for (int r = 2; r <= rowCount; r++) {
                var id = GetVal(r, "ת. זהות");
                if (!string.IsNullOrEmpty(id)) studentIdsInExcel.Add(id);
            }

            using (var connection = _dbService.CreateConnection())
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    try 
                    {
                        // --- פתרון לשגיאת ה-2100: יצירת טבלה זמנית ---
                        await connection.ExecuteAsync("CREATE TABLE #ExcelIds (ID NVARCHAR(50))", transaction: transaction);
                        
                        // העלאה מהירה של ה-IDs לטבלה הזמנית
                        using (var bulkCopy = new Microsoft.Data.SqlClient.SqlBulkCopy((Microsoft.Data.SqlClient.SqlConnection)connection, Microsoft.Data.SqlClient.SqlBulkCopyOptions.Default, (Microsoft.Data.SqlClient.SqlTransaction)transaction))
                        {
                            bulkCopy.DestinationTableName = "#ExcelIds";
                            var dt = new System.Data.DataTable();
                            dt.Columns.Add("ID");
                            foreach (var id in studentIdsInExcel.Distinct()) dt.Rows.Add(id);
                            await bulkCopy.WriteToServerAsync(dt);
                        }

                        // 3. מחיקת תלמידות שלא באקסל - רק אם אין להן הגשות או תשלומים (הגנה על נתונים חשובים)
                        var deleteMissingStudents = @"
                            DELETE FROM Submissions 
                            WHERE StudentID IN (
                                SELECT StudentID FROM Students 
                                WHERE StudentID NOT IN (SELECT ID FROM #ExcelIds) 
                                AND Status = 'Active'
                                AND StudentID NOT IN (
                                    SELECT DISTINCT StudentID FROM StudentDebts 
                                    WHERE IsPaid = 1 OR IsSubmitted = 1
                                )
                            );
                            DELETE FROM StudentDebts 
                            WHERE StudentID IN (
                                SELECT StudentID FROM Students 
                                WHERE StudentID NOT IN (SELECT ID FROM #ExcelIds) 
                                AND Status = 'Active'
                                AND StudentID NOT IN (
                                    SELECT DISTINCT StudentID FROM StudentDebts 
                                    WHERE IsPaid = 1 OR IsSubmitted = 1
                                )
                            );
                            DELETE FROM Students 
                            WHERE StudentID NOT IN (SELECT ID FROM #ExcelIds) 
                            AND Status = 'Active'
                            AND StudentID NOT IN (
                                SELECT DISTINCT StudentID FROM StudentDebts 
                                WHERE IsPaid = 1 OR IsSubmitted = 1
                            )";
                        
                        await connection.ExecuteAsync(deleteMissingStudents, transaction: transaction);

                        // 4. איפוס IsActive (לוגיקה מקורית ללא שינוי)
                        await connection.ExecuteAsync("UPDATE StudentDebts SET IsActive = 0 WHERE StudentID IN (SELECT ID FROM #ExcelIds)", transaction: transaction);

                        // --- לוגיקת חישוב השעות והלופ (נשארת בדיוק כפי שהייתה) ---
                        var hoursTracker = new Dictionary<string, int>();

                        for (int row = 2; row <= rowCount; row++)
                        {
                            var studentId = GetVal(row, "ת. זהות");
                            if (string.IsNullOrEmpty(studentId)) continue;

                            if (!hoursTracker.ContainsKey(studentId)) hoursTracker[studentId] = 0;
                            int.TryParse(GetVal(row, "שעות"), out int rowHours);
                            // IsExempt צריך להיות תמיד 0 בעת טעינה - הפטור בגלל מכסה מחושב ב-FilterDebtsLogic באמצעות IsAllowedSubmission
                            bool isExempt = false;
                            hoursTracker[studentId] += rowHours;

                            // Upsert Students
                            var upsertStudent = @"
                                IF NOT EXISTS (SELECT 1 FROM Students WHERE StudentID = @StudentID)
                                    INSERT INTO Students (StudentID, FirstName, LastName, YearGroup, StudentGroup, Status) 
                                    VALUES (@StudentID, @FirstName, @LastName, @YearGroup, @StudentGroup, 'Active')
                                ELSE
                                    UPDATE Students SET FirstName = @FirstName, LastName = @LastName WHERE StudentID = @StudentID AND Status = 'Active'";
                            
                            await connection.ExecuteAsync(upsertStudent, new { 
                                StudentID = studentId, 
                                FirstName = GetVal(row, "שם פרטי משתלמת"), 
                                LastName = GetVal(row, "שם משפחה משתלמת"),
                                YearGroup = GetVal(row, "שנה"),
                                StudentGroup = GetVal(row, "קבוצה")
                            }, transaction: transaction);

                            // Upsert Debts
                            string rawLessonNum = GetVal(row, "מספר שיעור").Replace(".0", "").Trim();
                            int.TryParse(rawLessonNum, out int lessonNumber);
                            var materialLink = GetVal(row, "קישור לעבודה");
                            bool isOnlyInstructions = !string.IsNullOrEmpty(materialLink) && !IsUrl(materialLink);

                            var upsertDebt = @"
                                IF NOT EXISTS (SELECT 1 FROM StudentDebts WHERE StudentID = @StudentID AND LessonName = @LessonName AND LessonNumber = @LessonNumber)
                                    INSERT INTO StudentDebts (StudentID, LessonName, LessonType, LessonNumber, Hours, LecturerName, StudyGoal, DomainType, MaterialLink, IsActive, IsPaid, IsSubmitted, IsExempt) 
                                    VALUES (@StudentID, @LessonName, @LessonType, @LessonNumber, @Hours, @LecturerName, @StudyGoal, @DomainType, @MaterialLink, 1, 0, @InitSub, @IsExempt)
                                ELSE
                                    UPDATE StudentDebts SET
                                        IsActive = 1,
                                        IsExempt      = CASE WHEN IsSubmitted = 1 THEN IsExempt      ELSE @IsExempt      END,
                                        Hours         = CASE WHEN IsSubmitted = 1 THEN Hours         ELSE @Hours         END,
                                        MaterialLink  = CASE WHEN IsSubmitted = 1 THEN MaterialLink  ELSE @MaterialLink  END
                                    WHERE StudentID = @StudentID AND LessonName = @LessonName AND LessonNumber = @LessonNumber";

                            await connection.ExecuteAsync(upsertDebt, new { 
                                StudentID = studentId, 
                                LessonName = GetVal(row, "שם שיעור"),
                                LessonType = GetVal(row, "סוג שיעור"),
                                LessonNumber = lessonNumber,
                                Hours = rowHours,
                                LecturerName = $"{GetVal(row, "שם פרטי מרצה")} {GetVal(row, "שם משפחה מרצה")}".Trim(),
                                StudyGoal = GetVal(row, "מטרת לימודים"),
                                DomainType = GetVal(row, "תחום"),
                                MaterialLink = materialLink,
                                IsExempt = isExempt,
                                InitSub = isOnlyInstructions ? 1 : 0
                            }, transaction: transaction);
                        }

                        transaction.Commit();
                        return Ok(new { message = "הסנכרון הושלם בהצלחה!" });
                    }
                    catch (Exception ex) 
{ 
    transaction.Rollback();
    Console.WriteLine(ex.Message); // שימוש במשתנה
    throw; 
}
                }
            }
        }
    }
    catch (Exception ex) 
    { 
        return StatusCode(500, "שגיאה בתהליך: " + ex.Message); 
    }
}
        // --- ייצוא דוח הגשות (מותאם לפונקציה החדשה) ---
        [HttpGet("export-submissions")]
       public async Task<IActionResult> ExportSubmissions()
        {
            try {
                using var connection = _dbService.CreateConnection();
                
                var sql = @"
                    SELECT 
                        d.DebtID, -- חובה לשלוף את זה בשביל הקישור החדש!
                        s.FirstName, 
                        s.LastName, 
                        s.StudentID, 
                        d.LessonNumber, 
                        d.LessonName, 
                        d.IsPaid, 
                        COALESCE(sub.FilePath, d.SubmissionPath) as SubmissionPath,
                        COALESCE(sub.UploadDate, d.LastUpdated) as SubmissionDate
                    FROM StudentDebts d 
                    JOIN Students s ON d.StudentID = s.StudentID 
                    LEFT JOIN Submissions sub ON d.DebtID = sub.DebtID 
                    WHERE d.IsSubmitted = 1 
                    ORDER BY SubmissionDate DESC";

                var submissions = await connection.QueryAsync(sql);

                using (var package = new ExcelPackage())
                {
                    var sheet = package.Workbook.Worksheets.Add("הגשות");
                    
                    var headers = new[] { "שם פרטי", "שם משפחה", "ת\"ז", "מספר קורס", "שם הקורס", "האם שולם", "קישור לעבודה", "תאריך הגשה" };
                    for (int i = 0; i < headers.Length; i++) {
                        sheet.Cells[1, i + 1].Value = headers[i];
                        sheet.Cells[1, i + 1].Style.Font.Bold = true;
                    }

                    int r = 2;
                    foreach (var sub in submissions)
                    {
                        sheet.Cells[r, 1].Value = Convert.ToString(sub.FirstName);
                        sheet.Cells[r, 2].Value = Convert.ToString(sub.LastName);
                        sheet.Cells[r, 3].Value = Convert.ToString(sub.StudentID);
                        sheet.Cells[r, 4].Value = Convert.ToString(sub.LessonNumber);
                        sheet.Cells[r, 5].Value = Convert.ToString(sub.LessonName);

                        bool isPaid = false;
                        try { if (sub.IsPaid != null) isPaid = Convert.ToBoolean(sub.IsPaid); } catch {}
                        sheet.Cells[r, 6].Value = isPaid ? "כן" : "לא";

                        // --- לוגיקת קישור חכמה לאקסל ---
                        string originalPath = Convert.ToString(sub.SubmissionPath);
                        if (!string.IsNullOrEmpty(originalPath))
                        {
                            // אנו יוצרים קישור שמפנה תמיד ל-Action החדש שלנו בשרת
                            // השרת יחליט אם לתת קובץ בודד או ZIP
                            var downloadUrl = $"{Request.Scheme}://{Request.Host}/api/admin/download-submission/{sub.DebtID}";

                            sheet.Cells[r, 7].Hyperlink = new Uri(downloadUrl);
                            
                            // טקסט לתצוגה
                            if (originalPath.Contains(",") || originalPath.Contains(" , "))
                                sheet.Cells[r, 7].Value = "הורדת קבצים (ZIP)";
                            else
                                sheet.Cells[r, 7].Value = "לחצי להורדה";

                            sheet.Cells[r, 7].Style.Font.UnderLine = true;
                            sheet.Cells[r, 7].Style.Font.Color.SetColor(System.Drawing.Color.Blue);
                        }
                        else
                        {
                            sheet.Cells[r, 7].Value = "אין קובץ";
                        }
                        // --------------------------------

                        if (sub.SubmissionDate != null)
                            sheet.Cells[r, 8].Value = Convert.ToDateTime(sub.SubmissionDate).ToString("dd/MM/yyyy HH:mm");
                        
                        r++;
                    }

                    sheet.Cells.AutoFitColumns();
                    sheet.View.RightToLeft = true; 
                    return File(package.GetAsByteArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "Report.xlsx");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("EXPORT ERROR: " + ex.ToString());
                return StatusCode(500, "שגיאה בייצוא הדו\"ח: " + ex.Message);
            }
        }
       [HttpGet("download-submission/{id}")]
public async Task<IActionResult> DownloadSubmission(int id)
{
    try
    {
        using var connection = _dbService.CreateConnection();
        
        // שליפת הנתיב לפי המבנה שראינו בטבלאות שלך
        var sql = @"
            SELECT TOP 1 COALESCE(sub.FilePath, d.SubmissionPath) 
            FROM StudentDebts d 
            LEFT JOIN Submissions sub ON d.DebtID = sub.DebtID 
            WHERE d.DebtID = @id";
            
        string? dbPath = await connection.QueryFirstOrDefaultAsync<string>(sql, new { id });

        if (string.IsNullOrEmpty(dbPath)) 
            return NotFound("לא נמצא נתיב לקובץ במסד הנתונים.");

        // התיקייה שראינו בצילום המסך שלך
        var uploadsPath = Path.Combine(_env.ContentRootPath, "BotUploads");
        
        // פיצול הנתיב במקרה שיש כמה קבצים (כפי שמופיע באקסל כ-ZIP)
        var fileNames = dbPath.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                              .Select(f => Path.GetFileName(f.Trim()))
                              .ToList();

        if (fileNames.Count == 1)
        {
            var fullPath = Path.Combine(uploadsPath, fileNames[0]);
            if (!System.IO.File.Exists(fullPath)) 
                return NotFound($"הקובץ {fileNames[0]} לא נמצא פיזית בתיקיית BotUploads.");
            
            var content = await System.IO.File.ReadAllBytesAsync(fullPath);
            return File(content, "application/octet-stream", fileNames[0]);
        }
        else
        {
            // טיפול בהורדה של מספר קבצים כקובץ ZIP אחד
            using var ms = new MemoryStream();
            using (var archive = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
            {
                foreach (var fileName in fileNames)
                {
                    var fullPath = Path.Combine(uploadsPath, fileName);
                    if (System.IO.File.Exists(fullPath))
                    {
                        archive.CreateEntryFromFile(fullPath, fileName);
                    }
                }
            }
            return File(ms.ToArray(), "application/zip", $"Submission_{id}.zip");
        }
    }
    catch (Exception ex)
    {
        return StatusCode(500, $"שגיאה בשליפת הקובץ: {ex.Message}");
    }
}

        // פונקציית עזר להורדה ישירה (לשימוש פנימי או גיבוי)
        [HttpGet("download/{fileName}")]
        public IActionResult DownloadFile(string fileName)
        {
            var uploadsPath = Path.Combine(_env.ContentRootPath, "BotUploads");
            var filePath = Path.Combine(uploadsPath, fileName);
            if (!System.IO.File.Exists(filePath)) return NotFound("הקובץ לא נמצא");
            return File(System.IO.File.ReadAllBytes(filePath), "application/octet-stream", fileName);
        }

        // ===== פונקציות ניהול חריגים =====

        /// <summary>
        /// קבלת כל החובות של תלמידה (כולל פטורים והוצאות)
        /// </summary>
        [HttpGet("student-debts/{studentId}")]
        public async Task<IActionResult> GetStudentDebts(string studentId)
        {
            try
            {
                var debts = await _dbService.GetAllDebtsByStudentIdAsync(studentId);
                return Ok(debts);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"שגיאה בשליפת החובות: {ex.Message}");
            }
        }

        /// <summary>
        /// הסרת קורס כולו מחובות התלמידה - מחיקה לגמרי מהמסד נתונים
        /// </summary>
        [HttpPost("remove-debt/{debtId}")]
        public async Task<IActionResult> RemoveDebt(int debtId)
        {
            try
            {
                var debt = await _dbService.GetDebtByIdAsync(debtId);
                if (debt == null)
                    return NotFound("החוב לא נמצא");

                await _dbService.RemoveDebtEntirelyAsync(debtId);
                return Ok(new { message = $"הקורס '{debt.LessonName}' של התלמידה {debt.StudentID} נמחק לגמרי מהמסד נתונים" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"שגיאה בהסרת החוב: {ex.Message}");
            }
        }

        /// <summary>
        /// פטור מתשלום בלבד על קורס מסוים
        /// </summary>
        [HttpPost("exempt-payment/{debtId}")]
        public async Task<IActionResult> ExemptFromPayment(int debtId)
        {
            try
            {
                var debt = await _dbService.GetDebtByIdAsync(debtId);
                if (debt == null)
                    return NotFound("החוב לא נמצא");

                await _dbService.ExemptDebtFromPaymentAsync(debtId);
                return Ok(new { message = $"התלמידה {debt.StudentID} פוטרה מתשלום על קורס '{debt.LessonName}'" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"שגיאה בפטור מתשלום: {ex.Message}");
            }
        }

        /// <summary>
        /// פטור מהגשה בלבד על קורס מסוים
        /// </summary>
        [HttpPost("exempt-submission/{debtId}")]
        public async Task<IActionResult> ExemptFromSubmission(int debtId)
        {
            try
            {
                var debt = await _dbService.GetDebtByIdAsync(debtId);
                if (debt == null)
                    return NotFound("החוב לא נמצא");

                await _dbService.ExemptDebtFromSubmissionAsync(debtId);
                return Ok(new { message = $"התלמידה {debt.StudentID} פוטרה מהגשה על קורס '{debt.LessonName}'" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"שגיאה בפטור מהגשה: {ex.Message}");
            }
        }

        [HttpGet("backup-db")]
        public async Task<IActionResult> BackupDb()
        {
            try
            {
                using var connection = _dbService.CreateConnection();
                var students    = (await connection.QueryAsync<dynamic>("SELECT * FROM Students")).ToList();
                var debts       = (await connection.QueryAsync<dynamic>("SELECT * FROM StudentDebts")).ToList();
                var submissions = (await connection.QueryAsync<dynamic>("SELECT * FROM Submissions")).ToList();

                var backup = new { exportedAt = DateTime.Now, students, debts, submissions };
                var json = System.Text.Json.JsonSerializer.Serialize(backup,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

                var fileName = $"DB_Backup_{DateTime.Now:yyyyMMdd_HHmmss}.json";
                return File(System.Text.Encoding.UTF8.GetBytes(json), "application/json", fileName);
            }
            catch (Exception ex)
            {
                return StatusCode(500, "שגיאה בגיבוי: " + ex.Message);
            }
        }

        private class BackupData
        {
            public List<BackupStudent> Students { get; set; } = new();
            public List<BackupDebt> Debts { get; set; } = new();
            public List<BackupSubmission> Submissions { get; set; } = new();
        }
        private class BackupStudent
        {
            public string StudentID { get; set; } = "";
            public string? FirstName { get; set; }
            public string? LastName { get; set; }
            public string? YearGroup { get; set; }
            public string? StudentGroup { get; set; }
            public string? Status { get; set; }
        }
        private class BackupDebt
        {
            public int DebtID { get; set; }
            public string StudentID { get; set; } = "";
            public string? LessonName { get; set; }
            public string? LessonType { get; set; }
            public int LessonNumber { get; set; }
            public int Hours { get; set; }
            public string? LecturerName { get; set; }
            public string? StudyGoal { get; set; }
            public string? DomainType { get; set; }
            public string? MaterialLink { get; set; }
            public bool IsPaid { get; set; }
            public string? TransactionId { get; set; }
            public bool IsSubmitted { get; set; }
            public bool IsExempt { get; set; }
            public bool IsActive { get; set; }
            public DateTime LastUpdated { get; set; }
            public DateTime? UploadDate { get; set; }
        }
        private class BackupSubmission
        {
            public int SubmissionID { get; set; }
            public int DebtID { get; set; }
            public string? StudentID { get; set; }
            public string? FilePath { get; set; }
            public DateTime? UploadDate { get; set; }
            public string? FileName { get; set; }
        }

        [HttpPost("restore-db")]
        public async Task<IActionResult> RestoreDb(IFormFile file)
        {
            if (file == null || file.Length == 0) return BadRequest("לא נבחר קובץ");
            if (Path.GetExtension(file.FileName).ToLower() != ".json") return BadRequest("נא להעלות קובץ JSON בלבד");

            try
            {
                using var stream = file.OpenReadStream();
                var backup = await System.Text.Json.JsonSerializer.DeserializeAsync<BackupData>(stream,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (backup == null) return BadRequest("קובץ גיבוי לא תקין");

                using var connection = _dbService.CreateConnection();
                connection.Open();
                using var transaction = connection.BeginTransaction();

                try
                {
                    // מחיקה בסדר הנכון (FK)
                    await connection.ExecuteAsync("DELETE FROM Submissions", transaction: transaction);
                    await connection.ExecuteAsync("DELETE FROM StudentDebts", transaction: transaction);
                    await connection.ExecuteAsync("DELETE FROM Students", transaction: transaction);

                    // שחזור Students
                    foreach (var s in backup.Students)
                        await connection.ExecuteAsync(
                            "INSERT INTO Students (StudentID, FirstName, LastName, YearGroup, StudentGroup, Status) VALUES (@StudentID, @FirstName, @LastName, @YearGroup, @StudentGroup, @Status)",
                            s, transaction: transaction);

                    // שחזור StudentDebts (עם IDENTITY_INSERT)
                    if (backup.Debts.Count > 0)
                    {
                        await connection.ExecuteAsync("SET IDENTITY_INSERT StudentDebts ON", transaction: transaction);
                        foreach (var d in backup.Debts)
                            await connection.ExecuteAsync(@"
                                INSERT INTO StudentDebts (DebtID, StudentID, LessonName, LessonType, LessonNumber, Hours, LecturerName, StudyGoal, DomainType, MaterialLink, IsPaid, TransactionId, IsSubmitted, IsExempt, IsActive, LastUpdated)
                                VALUES (@DebtID, @StudentID, @LessonName, @LessonType, @LessonNumber, @Hours, @LecturerName, @StudyGoal, @DomainType, @MaterialLink, @IsPaid, @TransactionId, @IsSubmitted, @IsExempt, @IsActive, @LastUpdated)",
                                d, transaction: transaction);
                        await connection.ExecuteAsync("SET IDENTITY_INSERT StudentDebts OFF", transaction: transaction);
                    }

                    // שחזור Submissions (עם IDENTITY_INSERT)
                    if (backup.Submissions.Count > 0)
                    {
                        await connection.ExecuteAsync("SET IDENTITY_INSERT Submissions ON", transaction: transaction);
                        foreach (var sub in backup.Submissions)
                            await connection.ExecuteAsync(@"
                                INSERT INTO Submissions (SubmissionID, DebtID, StudentID, FilePath, UploadDate, FileName)
                                VALUES (@SubmissionID, @DebtID, @StudentID, @FilePath, @UploadDate, @FileName)",
                                sub, transaction: transaction);
                        await connection.ExecuteAsync("SET IDENTITY_INSERT Submissions OFF", transaction: transaction);
                    }

                    transaction.Commit();
                    return Ok(new { message = $"שחזור הושלם! {backup.Students.Count} תלמידות, {backup.Debts.Count} חובות, {backup.Submissions.Count} הגשות" });
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
            }
            catch (Exception ex)
            {
                return StatusCode(500, "שגיאה בשחזור: " + ex.Message);
            }
        }

        /// <summary>
        /// פטור מלא מקורס - גם מתשלום וגם מהגשה (הקורס נשאר בבסיס הנתונים)
        /// </summary>
        [HttpPost("exempt-completely/{debtId}")]
        public async Task<IActionResult> ExemptCompletely(int debtId)
        {
            try
            {
                var debt = await _dbService.GetDebtByIdAsync(debtId);
                if (debt == null)
                    return NotFound("החוב לא נמצא");

                await _dbService.ExemptDebtCompletelyAsync(debtId);
                return Ok(new { message = $"התלמידה {debt.StudentID} פוטרה לחלוטין מקורס '{debt.LessonName}' (הקורס נשאר בבסיס נתונים)" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"שגיאה בפטור מלא: {ex.Message}");
            }
        }
    }
}