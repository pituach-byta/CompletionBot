using Microsoft.AspNetCore.Mvc;
using OfficeOpenXml; 
using CompletionBot.Server.Services;
using Dapper;
using System.IO.Compression; // הוספנו את זה בשביל ה-ZIP

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
            if (file == null || file.Length == 0) return BadRequest("לא נבחר קובץ");
            try
            {
                var dataFolder = Path.Combine(_env.ContentRootPath, "Data");
                if (!Directory.Exists(dataFolder)) Directory.CreateDirectory(dataFolder);
                var filePath = Path.Combine(dataFolder, "Current_Debts.xlsx");
                using (var stream = new FileStream(filePath, FileMode.Create)) { await file.CopyToAsync(stream); }

                using (var package = new ExcelPackage(new FileInfo(filePath)))
                {
                    var worksheet = package.Workbook.Worksheets[0];
                    var rowCount = worksheet.Dimension.Rows;

                    using (var connection = _dbService.CreateConnection())
                    {
                        connection.Open();
                        using (var transaction = connection.BeginTransaction())
                        {
                            try 
                            {
                                await connection.ExecuteAsync("UPDATE StudentDebts SET IsActive = 0", transaction: transaction);
                                string currentStudentId = "";
                                int currentStudentHours = 0;

                                for (int row = 2; row <= rowCount; row++)
                                {
                                    var studentId = worksheet.Cells[row, 2].Value?.ToString()?.Trim();
                                    if (string.IsNullOrEmpty(studentId)) continue;

                                    if (studentId != currentStudentId) { currentStudentId = studentId; currentStudentHours = 0; }

                                    var yearGroup = worksheet.Cells[row, 1].Value?.ToString();
                                    var lastName = worksheet.Cells[row, 3].Value?.ToString();
                                    var firstName = worksheet.Cells[row, 4].Value?.ToString();
                                    var studentGroup = worksheet.Cells[row, 5].Value?.ToString();
                                    int.TryParse(worksheet.Cells[row, 6].Value?.ToString(), out int lessonNumber);
                                    var lessonType = worksheet.Cells[row, 7].Value?.ToString();
                                    var lessonName = worksheet.Cells[row, 8].Value?.ToString();
                                    int.TryParse(worksheet.Cells[row, 9].Value?.ToString(), out int hours);
                                    var lecturerName = $"{worksheet.Cells[row, 11].Value} {worksheet.Cells[row, 10].Value}".Trim();
                                    var studyGoal = worksheet.Cells[row, 12].Value?.ToString();
                                    var domainType = worksheet.Cells[row, 13].Value?.ToString();
                                    var materialLink = worksheet.Cells[row, 15].Value?.ToString();

                                    bool isExempt = (currentStudentHours >= 300);
                                    if (!isExempt) currentStudentHours += hours;

                                    var upsertStudent = @"IF NOT EXISTS (SELECT 1 FROM Students WHERE StudentID = @StudentID)
                                            INSERT INTO Students (StudentID, FirstName, LastName, YearGroup, StudentGroup) VALUES (@StudentID, @FirstName, @LastName, @YearGroup, @StudentGroup)
                                        ELSE UPDATE Students SET FirstName = @FirstName, LastName = @LastName, YearGroup = @YearGroup, StudentGroup = @StudentGroup WHERE StudentID = @StudentID";
                                    await connection.ExecuteAsync(upsertStudent, new { StudentID = studentId, FirstName = firstName, LastName = lastName, YearGroup = yearGroup, StudentGroup = studentGroup }, transaction: transaction);

                                    var upsertDebt = @"MERGE INTO StudentDebts AS Target
                                        USING (VALUES (@StudentID, @LessonName, @LessonNumber)) AS Source (StudentID, LessonName, LessonNumber)
                                        ON Target.StudentID = Source.StudentID AND Target.LessonName = Source.LessonName AND Target.LessonNumber = Source.LessonNumber
                                        WHEN MATCHED THEN UPDATE SET IsActive = 1, IsExempt = @IsExempt, LessonType = @LessonType, Hours = @Hours, LecturerName = @LecturerName, StudyGoal = @StudyGoal, DomainType = @DomainType, MaterialLink = @MaterialLink, LastUpdated = GETDATE()
                                        WHEN NOT MATCHED THEN INSERT (StudentID, LessonName, LessonType, LessonNumber, Hours, LecturerName, StudyGoal, DomainType, MaterialLink, IsActive, IsPaid, IsSubmitted, IsExempt) VALUES (@StudentID, @LessonName, @LessonType, @LessonNumber, @Hours, @LecturerName, @StudyGoal, @DomainType, @MaterialLink, 1, 0, 0, @IsExempt);";
                                    await connection.ExecuteAsync(upsertDebt, new { StudentID = studentId, LessonName = lessonName, LessonType = lessonType, LessonNumber = lessonNumber, Hours = hours, LecturerName = lecturerName, StudyGoal = studyGoal, DomainType = domainType, MaterialLink = materialLink, IsExempt = isExempt }, transaction: transaction);
                                }
                                transaction.Commit();
                                return Ok(new { message = "הסנכרון הושלם!" });
                            }
                            catch (Exception) { transaction.Rollback(); throw; }
                        }
                    }
                }
            }
            catch (Exception ex) { return StatusCode(500, "שגיאה: " + ex.Message); }
        }
        
        // --- פונקציה חדשה וחכמה להורדת הגשות (ZIP או רגיל) ---
        [HttpGet("download-submission/{debtId}")]
        public async Task<IActionResult> DownloadSubmission(int debtId)
        {
            try 
            {
                using var connection = _dbService.CreateConnection();
                // שליפת נתיבי הקבצים לפי מזהה חוב
                var sql = @"SELECT COALESCE(sub.FilePath, d.SubmissionPath) 
                            FROM StudentDebts d 
                            LEFT JOIN Submissions sub ON d.DebtID = sub.DebtID 
                            WHERE d.DebtID = @DebtID";
                
                var pathString = await connection.QuerySingleOrDefaultAsync<string>(sql, new { DebtID = debtId });

                if (string.IsNullOrEmpty(pathString)) return NotFound("לא נמצאו קבצים להגשה זו");

                // פירוק המחרוזת לקבצים בודדים (טיפול במפרידים שונים)
                var links = pathString.Split(new[] { " , ", ",", ";" }, StringSplitOptions.RemoveEmptyEntries)
                                      .Select(l => l.Trim())
                                      .ToList();

                var uploadsPath = Path.Combine(_env.ContentRootPath, "BotUploads");

                // רשימה שתחזיק את הנתיבים הפיזיים האמיתיים בדיסק
                var physicalFiles = new List<(string Name, string Path)>();

                foreach(var link in links)
                {
                    string fileName = "";
                    // אם זה קישור מלא (מהמערכת החדשה), נחלץ רק את שם הקובץ מהסוף
                    if (link.Contains("/api/admin/download/"))
                    {
                        fileName = Path.GetFileName(link);
                    }
                    else
                    {
                        // תמיכה במערכת הישנה
                        fileName = Path.GetFileName(link);
                    }
                    
                    var fullPath = Path.Combine(uploadsPath, fileName);
                    if (System.IO.File.Exists(fullPath))
                    {
                        physicalFiles.Add((fileName, fullPath));
                    }
                }

                if (physicalFiles.Count == 0) return NotFound("הקבצים הפיזיים לא נמצאו בשרת");

                // מקרה 1: קובץ בודד - הורדה ישירה
                if (physicalFiles.Count == 1)
                {
                    var file = physicalFiles[0];
                    return File(System.IO.File.ReadAllBytes(file.Path), "application/octet-stream", file.Name);
                }

                // מקרה 2: מספר קבצים - יצירת ZIP
                using (var memoryStream = new MemoryStream())
                {
                    using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, true))
                    {
                        foreach (var file in physicalFiles)
                        {
                            var entry = archive.CreateEntry(file.Name);
                            using (var entryStream = entry.Open())
                            using (var fileStream = new FileStream(file.Path, FileMode.Open, FileAccess.Read))
                            {
                                await fileStream.CopyToAsync(entryStream);
                            }
                        }
                    }
                    
                    memoryStream.Position = 0;
                    return File(memoryStream.ToArray(), "application/zip", $"Submission_{debtId}.zip");
                }
            }
            catch (Exception ex)
            {
                return StatusCode(500, "שגיאה בהורדת הקבצים: " + ex.Message);
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