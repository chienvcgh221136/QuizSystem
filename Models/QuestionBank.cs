using System.ComponentModel.DataAnnotations;

namespace QuizApi.Models
{
    public class QuestionBank
    {
        [Key]
        public int QuestionId { get; set; }
        public string Category { get; set; } = "";
        public string Level { get; set; } = "Sơ cấp";
        public string Content { get; set; } = "";
        public string OptionA { get; set; } = "";
        public string OptionB { get; set; } = "";
        public string? OptionC { get; set; }
        public string? OptionD { get; set; }
        public string CorrectOption { get; set; } = "A";
        public string? Explanation { get; set; }
        /// <summary>
        /// Đường dẫn tương đối đến ảnh đính kèm câu hỏi (nếu có).
        /// Ví dụ: /uploads/question-images/q_img_20240528_001.jpg
        /// </summary>
        public string? ImageUrl { get; set; }
        public double ScorePerQuestion { get; set; } = 1.0;
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
}
