using System;
using System.ComponentModel.DataAnnotations;

namespace QuizApi.Models
{
    public class ChatMessage
    {
        [Key]
        public int MessageId { get; set; }
        public string Username { get; set; } = string.Empty;
        public string UserMessage { get; set; } = string.Empty;
        public string AiResponse { get; set; } = string.Empty;
        public DateTime SentAt { get; set; } = DateTime.Now;
        public string? Metadata { get; set; } // Để lưu JSON metadata nếu cần (ví dụ: intent, examId)
    }
}
