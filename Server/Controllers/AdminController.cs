using Microsoft.AspNetCore.Mvc;
using OfficeOpenXml; 
using CompletionBot.Server.Services;
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

                        // 3. מחיקת תלמידות שלא באקסל (לוגיקה מקורית ללא שינוי, רק שימוש בטבלה הזמנית)
                        var deleteMissingStudents = @"
                            DELETE FROM Submissions WHERE StudentID IN (SELECT StudentID FROM Students WHERE StudentID NOT IN (SELECT ID FROM #ExcelIds) AND Status = 'Active');
                            DELETE FROM StudentDebts WHERE StudentID IN (SELECT StudentID FROM Students WHERE StudentID NOT IN (SELECT ID FROM #ExcelIds) AND Status = 'Active');
                            DELETE FROM Students WHERE StudentID NOT IN (SELECT ID FROM #ExcelIds) AND Status = 'Active'";
                        
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
                                    UPDATE StudentDebts SET IsActive = 1, IsExempt = @IsExempt, MaterialLink = @MaterialLink, Hours = @Hours
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
    }
}