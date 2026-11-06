namespace QuizApi.Models
{

    public class ParsedDocument
    {

        public List<string> PageImages { get; set; } = new();

        public int TotalPages => PageImages.Count;
    }
}
