using Microsoft.AspNetCore.Mvc;
using OfficeOpenXml; 
using CompletionBot.Server.Services;
using Dapper;

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

        // --- 1. קבלת מידע על הקובץ הקיים ---
        [HttpGet("current-file-info")]
        public IActionResult GetCurrentFileInfo()
        {
            var path = Path.Combine(_env.ContentRootPath, "Data", "Current_Debts.xlsx");
            if (!System.IO.File.Exists(path))
            {
                return Ok(new { exists = false });
            }

            var fileInfo = new FileInfo(path);
            return Ok(new { 
                exists = true, 
                lastModified = fileInfo.LastWriteTime, 
                fileName = "Current_Debts.xlsx"
            });
        }

        // --- 2. הורדת הקובץ הקיים לצפייה ---
        [HttpGet("download-current")]
        public IActionResult DownloadCurrentFile()
        {
            var path = Path.Combine(_env.ContentRootPath, "Data", "Current_Debts.xlsx");
            if (!System.IO.File.Exists(path)) return NotFound("לא נמצא קובץ נתונים במערכת");

            var fileBytes = System.IO.File.ReadAllBytes(path);
            return File(fileBytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "Current_Debts.xlsx");
        }

        // --- 3. העלאה וסנכרון חכם ---
        [HttpPost("upload-excel")]
        public async Task<IActionResult> UploadExcel(IFormFile file)
        {
            if (file == null || file.Length == 0) return BadRequest("לא נבחר קובץ");

            try
            {
                // א. שמירת הקובץ הפיזי בשרת (לצורך צפייה עתידית)
                var dataFolder = Path.Combine(_env.ContentRootPath, "Data");
                if (!Directory.Exists(dataFolder)) Directory.CreateDirectory(dataFolder);
                
                var filePath = Path.Combine(dataFolder, "Current_Debts.xlsx");
                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await file.CopyToAsync(stream);
                }

                // ב. ביצוע הסנכרון מול המסד
                using (var package = new ExcelPackage(new FileInfo(filePath)))
                {
                    var worksheet = package.Workbook.Worksheets[0];
                    var rowCount = worksheet.Dimension.Rows;

                    using var connection = _dbService.CreateConnection();
                    connection.Open();
                    using var transaction = connection.BeginTransaction(); // עבודה בטרנזקציה לביטחון

                    try 
                    {
                        // שלב 1: סימון כל החובות כ"לא פעילים" זמנית
                        // מי שלא יופיע בקובץ החדש - יישאר לא פעיל ולא יופיע בבוט
                        await connection.ExecuteAsync("UPDATE StudentDebts SET IsActive = 0", transaction: transaction);

                        for (int row = 2; row <= rowCount; row++)
                        {
                            var studentId = worksheet.Cells[row, 2].Value?.ToString()?.Trim();
                            if (string.IsNullOrEmpty(studentId)) continue;

                            // קריאת נתונים
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

                            // עדכון פרטי תלמידה (או יצירה אם חדשה)
                            var upsertStudent = @"
                                IF NOT EXISTS (SELECT 1 FROM Students WHERE StudentID = @StudentID)
                                    INSERT INTO Students (StudentID, FirstName, LastName, YearGroup, StudentGroup) 
                                    VALUES (@StudentID, @FirstName, @LastName, @YearGroup, @StudentGroup)
                                ELSE
                                    UPDATE Students 
                                    SET FirstName = @FirstName, LastName = @LastName, YearGroup = @YearGroup, StudentGroup = @StudentGroup 
                                    WHERE StudentID = @StudentID";
                            
                            await connection.ExecuteAsync(upsertStudent, new { StudentID = studentId, FirstName = firstName, LastName = lastName, YearGroup = yearGroup, StudentGroup = studentGroup }, transaction: transaction);

                            // עדכון חוב (סנכרון)
                            // הלוגיקה: בודקים לפי ת"ז + שם קורס + מספר שיעור.
                            // אם קיים -> מעדכנים פרטים ומחזירים ל-Active=1.
                            // אם לא קיים -> יוצרים חדש עם Active=1.
                            var upsertDebt = @"
                                MERGE INTO StudentDebts AS Target
                                USING (VALUES (@StudentID, @LessonName, @LessonNumber)) AS Source (StudentID, LessonName, LessonNumber)
                                ON Target.StudentID = Source.StudentID AND Target.LessonName = Source.LessonName AND Target.LessonNumber = Source.LessonNumber
                                WHEN MATCHED THEN
                                    UPDATE SET 
                                        IsActive = 1, -- מחזירים לחיים
                                        LessonType = @LessonType,
                                        Hours = @Hours,
                                        LecturerName = @LecturerName,
                                        StudyGoal = @StudyGoal,
                                        DomainType = @DomainType,
                                        MaterialLink = @MaterialLink,
                                        LastUpdated = GETDATE()
                                WHEN NOT MATCHED THEN
                                    INSERT (StudentID, LessonName, LessonType, LessonNumber, Hours, LecturerName, StudyGoal, DomainType, MaterialLink, IsActive, IsPaid, IsSubmitted)
                                    VALUES (@StudentID, @LessonName, @LessonType, @LessonNumber, @Hours, @LecturerName, @StudyGoal, @DomainType, @MaterialLink, 1, 0, 0);";

                            await connection.ExecuteAsync(upsertDebt, new { 
                                StudentID = studentId, 
                                LessonName = lessonName, 
                                LessonType = lessonType, 
                                LessonNumber = lessonNumber, 
                                Hours = hours, 
                                LecturerName = lecturerName, 
                                StudyGoal = studyGoal, 
                                DomainType = domainType, 
                                MaterialLink = materialLink 
                            }, transaction: transaction);
                        }

                        transaction.Commit();
                        return Ok(new { message = "הסנכרון הושלם! הנתונים עודכנו בהתאם לקובץ החדש." });
                    }
                    catch (Exception)
                    {
                        transaction.Rollback();
                        throw;
                    }
                }
            }
            catch (Exception ex) { return StatusCode(500, "שגיאה: " + ex.Message); }
        }
        
        // (הפונקציה הקודמת של ייצוא דוח נשארת ללא שינוי)
        [HttpGet("export-submissions")]
        public async Task<IActionResult> ExportSubmissions()
        {
             using var connection = _dbService.CreateConnection();
             var sql = @"SELECT s.FirstName, s.LastName, s.StudentID, d.LessonName, d.LessonType, d.LecturerName, d.LastUpdated as SubmissionDate FROM StudentDebts d JOIN Students s ON d.StudentID = s.StudentID WHERE d.IsSubmitted = 1 ORDER BY d.LastUpdated DESC";
             var submissions = await connection.QueryAsync(sql);
             using (var package = new ExcelPackage()) {
                var sheet = package.Workbook.Worksheets.Add("הגשות");
                sheet.Cells[1, 1].Value = "שם פרטי"; sheet.Cells[1, 2].Value = "שם משפחה"; sheet.Cells[1, 3].Value = "ת\"ז"; sheet.Cells[1, 4].Value = "קורס"; sheet.Cells[1, 5].Value = "תאריך";
                int r = 2;
                foreach(var sub in submissions) {
                    sheet.Cells[r, 1].Value = sub.FirstName; sheet.Cells[r, 2].Value = sub.LastName; sheet.Cells[r, 3].Value = sub.StudentID; sheet.Cells[r, 4].Value = sub.LessonName; sheet.Cells[r, 5].Value = sub.SubmissionDate.ToString();
                    r++;
                }
                return File(package.GetAsByteArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "Report.xlsx");
             }
        }
    }
}