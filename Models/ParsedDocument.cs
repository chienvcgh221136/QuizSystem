namespace QuizApi.Models
{
    /// <summary>
    /// Kết quả trích xuất nội dung từ tệp đề thi (PDF/Word).
    /// Cách 2: Mỗi trang được chuyển thành một bức ảnh JPEG Base64 chất lượng cao.
    /// </summary>
    public class ParsedDocument
    {
        /// <summary>
        /// Danh sách chuỗi Base64 JPEG thuần (không có data URI prefix) của từng trang,
        /// theo đúng thứ tự trang để gửi lên Vision API.
        /// </summary>
        public List<string> PageImages { get; set; } = new();

        /// <summary>
        /// Tổng số trang đã xử lý.
        /// </summary>
        public int TotalPages => PageImages.Count;
    }
}
