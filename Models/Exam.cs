using System;
using System.ComponentModel.DataAnnotations;

namespace QuizApi.Models
{
    public class Exam
    {
        [Key]
        public int ExamId { get; set; }
        public string? Title { get; set; }
        public string? Description { get; set; }
        public string? Category { get; set; }
        public string? Level { get; set; }
        public int? TimeLimit { get; set; }
        public double? TotalScore { get; set; }
        public string? Status { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime? CreatedAt { get; set; }
        public int? ApprovedBy { get; set; }
        public DateTime? ApprovedAt { get; set; }
    }
}
