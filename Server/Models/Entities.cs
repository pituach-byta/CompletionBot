namespace CompletionBot.Server.Models
{
    public class Student
    {
        public string StudentID { get; set; } = ""; 
        public string? FirstName { get; set; }      
        public string? LastName { get; set; }
        public string? YearGroup { get; set; }
        public string? StudentGroup { get; set; }
    }

    public class StudentDebt
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
        public bool IsActive { get; set; } 
        public DateTime LastUpdated { get; set; }
    }

    public class Submission
    {
        public int SubmissionID { get; set; }
        public int DebtID { get; set; }
        public DateTime UploadDate { get; set; }
        public string? FilePath { get; set; } 
    }

    // --- המודלים שהיו חסרים לך וגרמו לשגיאה ---

    public class ChatRequest
    {
        public string StudentId { get; set; } = "";
        public string UserMessage { get; set; } = "";
    }

    public class BotResponse
    {
        public string Reply { get; set; } = ""; // הטקסט שהבוט אומר
        public string ActionType { get; set; } = "None"; // סוג הפעולה: None, ShowDebts, UploadFile
        public object? Data { get; set; } // המידע הרלוונטי (למשל רשימת החובות)
        public string? StudentId { get; set; } // כדי שהלקוח ישמור את הזיהוי
    }
}