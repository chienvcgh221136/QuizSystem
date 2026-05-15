using System;
using System.ComponentModel.DataAnnotations;

namespace QuizApi.Models
{
    public class ExamResult
    {
        [Key]
        public int ResultId { get; set; }
        public int ExamId { get; set; }
        public int UserId { get; set; }
        public DateTime? StartTime { get; set; }
        public DateTime? SubmitTime { get; set; }
        public double? Score { get; set; }
        public string? Status { get; set; } // InProgress, Submitted, Expired
    }
}
