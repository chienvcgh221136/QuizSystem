using System;
using System.ComponentModel.DataAnnotations;

namespace QuizApi.Models
{
    public class Notification
    {
        [Key]
        public int NotificationId { get; set; }

        [Required]
        public string Title { get; set; } = string.Empty;

        [Required]
        public string Message { get; set; } = string.Empty;

        public string Type { get; set; } = "Exam";

        public int? TargetId { get; set; }

        public bool IsRead { get; set; } = false;

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public int? UserId { get; set; }
    }
}
