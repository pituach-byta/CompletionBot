using System.Data;
using Microsoft.Data.SqlClient;
using Dapper;
using CompletionBot.Server.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace CompletionBot.Server.Services
{
    public class DbService
    {
        private readonly IConfiguration _configuration;
        private readonly string _connectionString;

        public DbService(IConfiguration configuration)
        {
            _configuration = configuration;
            _connectionString = _configuration.GetConnectionString("DefaultConnection") 
                            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
        }

        public IDbConnection CreateConnection()
        {
            return new SqlConnection(_connectionString);
        }

        public async Task<Student?> GetStudentByIdAsync(string studentId)
        {
            using var connection = CreateConnection();
            var sql = "SELECT * FROM Students WHERE StudentID = @StudentID";
            return await connection.QuerySingleOrDefaultAsync<Student>(sql, new { StudentID = studentId });
        }

        // --- הפונקציה שהייתה חסרה וגרמה לשגיאה ---
        public async Task<IEnumerable<StudentDebt>> GetDebtsByStudentIdAsync(string studentId)
        {
            using var connection = CreateConnection();
            var sql = @"
                SELECT * FROM StudentDebts 
                WHERE StudentID = @StudentID AND IsActive = 1";
            return await connection.QueryAsync<StudentDebt>(sql, new { StudentID = studentId });
        }

        public async Task<IEnumerable<dynamic>> GetStudentDebtsFullDetailsAsync(string studentId)
        {
            using var connection = CreateConnection();
            var sql = @"
                SELECT 
                    s.StudentID, s.FirstName, s.LastName, s.YearGroup, s.StudentGroup,
                    sd.DebtID, sd.LessonName, sd.LessonType, sd.LessonNumber, 
                    sd.StudyGoal, sd.LecturerName, sd.Hours, sd.MaterialLink, 
                    sd.IsPaid, sd.IsSubmitted, sd.IsExempt,
                    COALESCE(sub.UploadDate, sd.LastUpdated) AS ActualSubmissionDate,
                    sub.FileName, sub.FilePath
                FROM StudentDebts sd
                INNER JOIN Students s ON sd.StudentID = s.StudentID
                LEFT JOIN Submissions sub ON sd.DebtID = sub.DebtID
                WHERE s.StudentID = @StudentID AND sd.IsActive = 1"; 

            return await connection.QueryAsync<dynamic>(sql, new { StudentID = studentId });
        }

        public async Task MarkDebtAsPaidAsync(int debtId, string transactionId = "NedarimPlus")
        {
            using var connection = CreateConnection();
            var sql = @"UPDATE StudentDebts 
                        SET IsPaid = 1, TransactionId = @TransactionId, LastUpdated = GETDATE() 
                        WHERE DebtID = @DebtID";
            await connection.ExecuteAsync(sql, new { DebtID = debtId, TransactionId = transactionId });
        }
        
        public async Task MarkDebtAsSubmittedAsync(int debtId)
        {
            using var connection = CreateConnection();
            var sql = @"UPDATE StudentDebts 
                        SET IsSubmitted = 1, LastUpdated = GETDATE()
                        WHERE DebtID = @DebtID";
            await connection.ExecuteAsync(sql, new { DebtID = debtId });
        }

        public async Task<StudentDebt?> GetDebtByIdAsync(int debtId)
        {
            using var connection = CreateConnection();
            var sql = "SELECT * FROM StudentDebts WHERE DebtID = @DebtID";
            return await connection.QuerySingleOrDefaultAsync<StudentDebt>(sql, new { DebtID = debtId });
        }

        public async Task SaveSubmissionAsync(int debtId, string studentId, string filePath)
        {
            using var connection = CreateConnection();
            
            // 1️⃣ קבל את MaterialLink של הDebt הנוכחי (לזיהוי קורסים כפולים עם אותו קישור אמיתי בלבד)
            var debtSql = "SELECT MaterialLink FROM StudentDebts WHERE DebtID = @DebtID";
            var materialLink = await connection.QuerySingleOrDefaultAsync<string>(debtSql, new { DebtID = debtId });
            
            // 2️⃣ שמור/עדכן את ה-Submission עבור debtId הנוכחי
            var checkSql = "SELECT COUNT(*) FROM Submissions WHERE DebtID = @DebtID";
            int exists = await connection.ExecuteScalarAsync<int>(checkSql, new { DebtID = debtId });

            if (exists > 0)
            {
                var updateSql = @"UPDATE Submissions 
                                  SET FilePath = @FilePath, UploadDate = GETDATE(), FileName = 'Updated File' 
                                  WHERE DebtID = @DebtID";
                await connection.ExecuteAsync(updateSql, new { DebtID = debtId, FilePath = filePath });
            }
            else
            {
                var insertSql = @"INSERT INTO Submissions (DebtID, StudentID, FilePath, UploadDate, FileName) 
                                  VALUES (@DebtID, @StudentID, @FilePath, GETDATE(), 'New Submission')";
                await connection.ExecuteAsync(insertSql, new { DebtID = debtId, StudentID = studentId, FilePath = filePath });
            }

            // 3️⃣ סמן את debtId הנוכחי כ-submitted
            await MarkDebtAsSubmittedAsync(debtId);
            
            // לא סימון קורסים דומים עם אותו MaterialLink
            // כל קורס צריך להגש בנפרד
        }
        public async Task UpdateStudentStatusAsync(string studentId, string newStatus)
{
    using var connection = CreateConnection();
    var sql = "UPDATE Students SET Status = @Status WHERE StudentID = @StudentID";
    await connection.ExecuteAsync(sql, new { Status = newStatus, StudentID = studentId });
}

// פונקציה לבדיקה האם התלמידה סיימה את כל חובותיה
public async Task<bool> CheckIfAllDebtsCompletedAsync(string studentId)
{
    using var connection = CreateConnection();
    // בודק אם יש חוב פעיל שהוא לא שולם או לא הוגש או לא פטור
    var sql = @"SELECT COUNT(*) FROM StudentDebts 
                WHERE StudentID = @StudentID 
                AND IsActive = 1 
                AND (IsPaid = 0 OR IsSubmitted = 0) 
                AND IsExempt = 0";
    int remainingDebts = await connection.ExecuteScalarAsync<int>(sql, new { StudentID = studentId });
    return remainingDebts == 0;
}

        // === פונקציות חריגות לניהול בלבד ===

        /// <summary>
        /// פטור תלמידה מתשלום על קורס מסוים בלבד
        /// </summary>
        public async Task ExemptDebtFromPaymentAsync(int debtId)
        {
            using var connection = CreateConnection();
            var sql = @"UPDATE StudentDebts 
                        SET IsPaid = 1, TransactionId = 'AdminExemption', LastUpdated = GETDATE() 
                        WHERE DebtID = @DebtID";
            await connection.ExecuteAsync(sql, new { DebtID = debtId });
        }

        /// <summary>
        /// פטור תלמידה מהגשה של קורס מסוים בלבד
        /// </summary>
        public async Task ExemptDebtFromSubmissionAsync(int debtId)
        {
            using var connection = CreateConnection();
            var sql = @"UPDATE StudentDebts 
                        SET IsSubmitted = 1, LastUpdated = GETDATE()
                        WHERE DebtID = @DebtID";
            await connection.ExecuteAsync(sql, new { DebtID = debtId });
        }

        /// <summary>
        /// מחק קורס לגמרי מהמסד נתונים
        /// </summary>
        public async Task RemoveDebtEntirelyAsync(int debtId)
        {
            using var connection = CreateConnection();
            // מחק את ה-Submission קודם (קונסטרייינט זר)
            var deleteSqlSubmission = "DELETE FROM Submissions WHERE DebtID = @DebtID";
            await connection.ExecuteAsync(deleteSqlSubmission, new { DebtID = debtId });
            
            // אחרי כן מחק את ה-StudentDebt
            var deleteSqlDebt = "DELETE FROM StudentDebts WHERE DebtID = @DebtID";
            await connection.ExecuteAsync(deleteSqlDebt, new { DebtID = debtId });
        }

        /// <summary>
        /// פטור תלמידה מקורס מסוים - מסימן את כל התנאים (תשלום + הגשה)
        /// </summary>
        public async Task ExemptDebtCompletelyAsync(int debtId)
        {
            using var connection = CreateConnection();
            var sql = @"UPDATE StudentDebts 
                        SET IsPaid = 1, IsSubmitted = 1, IsExempt = 1, 
                            TransactionId = 'AdminExemption', LastUpdated = GETDATE()
                        WHERE DebtID = @DebtID";
            await connection.ExecuteAsync(sql, new { DebtID = debtId });
        }

        /// <summary>
        /// קבל כל החובות של תלמידה לרבות סטטוס הגשה ותשלום
        /// </summary>
        public async Task<IEnumerable<StudentDebt>> GetAllDebtsByStudentIdAsync(string studentId)
        {
            using var connection = CreateConnection();
            var sql = @"
                SELECT * FROM StudentDebts 
                WHERE StudentID = @StudentID";
            return await connection.QueryAsync<StudentDebt>(sql, new { StudentID = studentId });
        }
    }
}
