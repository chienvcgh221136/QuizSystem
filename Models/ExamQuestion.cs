namespace QuizApi.Models
{
    public class ExamQuestion
    {
        public int ExamId { get; set; }
        public int QuestionId { get; set; }
        public int? OrderIndex { get; set; }
    }
}
