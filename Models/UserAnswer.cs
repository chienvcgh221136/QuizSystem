using System;
using System.ComponentModel.DataAnnotations;

namespace QuizApi.Models
{
    public class UserAnswer
    {
        [Key]
        public int AnswerId { get; set; }
        public int ResultId { get; set; }
        public int QuestionId { get; set; }
        public string? SelectedOption { get; set; } // A, B, C, D
        public bool? IsCorrect { get; set; }
        public DateTime? AnsweredAt { get; set; }
    }
}
