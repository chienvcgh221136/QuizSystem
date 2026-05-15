using System.ComponentModel.DataAnnotations;

namespace QuizApi.Models
{
    public class QuestionBank
    {
        [Key]
        public int QuestionId { get; set; }
        public string? Category { get; set; }
        public string? Level { get; set; }
        public string? Content { get; set; }
        public string? OptionA { get; set; }
        public string? OptionB { get; set; }
        public string? OptionC { get; set; }
        public string? OptionD { get; set; }
        public string? CorrectOption { get; set; }
        public double ScorePerQuestion { get; set; }
        public bool? IsActive { get; set; }
    }
}
