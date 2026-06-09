using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.IO;
using ClosedXML.Excel;
using QuizApi.Models;

namespace QuizApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class QuestionsController : ControllerBase
    {
        private readonly QuizDbContext _context;

        public QuestionsController(QuizDbContext context)
        {
            _context = context;
        }

        [HttpGet]
        public async Task<IActionResult> GetAllQuestions()
        {
            try
            {
                var questions = await _context.QuestionBank
                    .Where(q => q.IsActive == true)
                    .AsNoTracking()
                    .ToListAsync();

                // Normalize certain category names for UI consistency
                var categoryMap = new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
                {
                    { "Math", "Toán" }
                };

                var projected = questions.Select(q => new {
                    questionId = q.QuestionId,
                    content = q.Content,
                    category = categoryMap.ContainsKey(q.Category ?? "") ? categoryMap[q.Category ?? ""] : q.Category,
                    level = q.Level,
                    optionA = q.OptionA,
                    optionB = q.OptionB,
                    optionC = q.OptionC,
                    optionD = q.OptionD,
                    correctOption = q.CorrectOption,
                    explanation = q.Explanation,
                    scorePerQuestion = q.ScorePerQuestion,
                    isActive = q.IsActive,
                    imageUrl = q.ImageUrl
                }).ToList();

                return Ok(projected);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Lỗi hệ thống khi lấy danh sách câu hỏi.", error = ex.Message });
            }
        }

        [HttpGet("categories")]
        public async Task<IActionResult> GetCategories()
        {
            try
            {
                var categories = await _context.QuestionBank
                    .Where(q => !string.IsNullOrEmpty(q.Category))
                    .Select(q => q.Category)
                    .Distinct()
                    .ToListAsync();

                // Normalize known category names
                var categoryMap = new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
                {
                    { "Math", "Toán" }
                };

                var normalized = categories.Select(c => categoryMap.ContainsKey(c) ? categoryMap[c] : c).ToList();

                return Ok(normalized);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Lỗi khi lấy danh sách danh mục.", error = ex.Message });
            }
        }

        [HttpGet("random")]
        public async Task<IActionResult> GetRandomQuestions([FromQuery] string category, [FromQuery] string level)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(level))
                {
                    return BadRequest(new { message = "Yêu cầu cung cấp đầy đủ 'category' và 'level'." });
                }

                var randomQuestions = await _context.QuestionBank
                    .Where(q => q.Category == category && q.Level == level && q.IsActive == true)
                    .OrderBy(q => Guid.NewGuid())
                    .Take(10)
                    .AsNoTracking()
                    .ToListAsync();

                if (!randomQuestions.Any())
                {
                    return NotFound(new { message = $"Không tìm thấy câu hỏi phù hợp cho môn {category} ở mức độ {level}." });
                }

                return Ok(randomQuestions);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Lỗi hệ thống khi tạo bộ câu hỏi ngẫu nhiên.", error = ex.Message });
            }
        }

        [HttpPost]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> CreateQuestion([FromBody] QuestionBank newQuestion)
        {
            try
            {
                if (newQuestion == null)
                {
                    return BadRequest(new { message = "Dữ liệu câu hỏi không hợp lệ." });
                }

                newQuestion.CreatedAt = DateTime.Now;
                await _context.QuestionBank.AddAsync(newQuestion);
                await _context.SaveChangesAsync();

                return Ok(new { message = "Thêm mới câu hỏi thành công.", data = newQuestion });
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR CREATE QUESTION: " + ex.ToString());
                var innerMsg = ex.InnerException != null ? ex.InnerException.Message : "";
                return StatusCode(500, new { message = "Lỗi hệ thống khi thêm mới câu hỏi.", error = ex.Message, innerError = innerMsg });
            }
        }
        [HttpPut("{id}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> UpdateQuestion(int id, [FromBody] QuestionBank updatedQuestion)
        {
            try
            {
                if (id != updatedQuestion.QuestionId)
                {
                    return BadRequest(new { message = "ID trên URL và ID trong body không khớp." });
                }

                var existingQuestion = await _context.QuestionBank.FindAsync(id);
                if (existingQuestion == null)
                {
                    return NotFound(new { message = "Không tìm thấy câu hỏi để cập nhật." });
                }

                existingQuestion.Category = updatedQuestion.Category;
                existingQuestion.Level = updatedQuestion.Level;
                existingQuestion.Content = updatedQuestion.Content;
                existingQuestion.OptionA = updatedQuestion.OptionA;
                existingQuestion.OptionB = updatedQuestion.OptionB;
                existingQuestion.OptionC = updatedQuestion.OptionC;
                existingQuestion.OptionD = updatedQuestion.OptionD;
                existingQuestion.CorrectOption = updatedQuestion.CorrectOption;
                existingQuestion.Explanation = updatedQuestion.Explanation;
                existingQuestion.ScorePerQuestion = updatedQuestion.ScorePerQuestion;
                existingQuestion.IsActive = updatedQuestion.IsActive;
                // Chỉ cập nhật ImageUrl nếu được truyền lên (null = giữ nguyên, "" = xóa ảnh)
                if (updatedQuestion.ImageUrl != null)
                    existingQuestion.ImageUrl = string.IsNullOrEmpty(updatedQuestion.ImageUrl) ? null : updatedQuestion.ImageUrl;

                _context.QuestionBank.Update(existingQuestion);
                await _context.SaveChangesAsync();

                return Ok(new { message = "Cập nhật câu hỏi thành công.", data = existingQuestion });
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR UPDATE QUESTION: " + ex.ToString());
                var innerMsg = ex.InnerException != null ? ex.InnerException.Message : "";
                return StatusCode(500, new { message = "Lỗi hệ thống khi cập nhật câu hỏi.", error = ex.Message, innerError = innerMsg });
            }
        }

        [HttpDelete("{id}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> DeleteQuestion(int id)
        {
            try
            {
                var question = await _context.QuestionBank.FindAsync(id);
                if (question == null)
                {
                    return NotFound(new { message = "Không tìm thấy câu hỏi để xóa." });
                }

                _context.QuestionBank.Remove(question);
                await _context.SaveChangesAsync();

                return Ok(new { message = "Xóa câu hỏi thành công." });
            }
            catch (Exception ex)
            {
                var innerMsg = ex.InnerException != null ? ex.InnerException.Message : "";
                return StatusCode(500, new { message = "Lỗi hệ thống khi xóa câu hỏi.", error = ex.Message, innerError = innerMsg });
            }
        }

        [HttpGet("export")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> ExportQuestions([FromQuery] string? category, [FromQuery] string? level, [FromQuery] string? search)
        {
            try
            {
                var query = _context.QuestionBank.Where(q => q.IsActive == true).AsQueryable();

                if (!string.IsNullOrEmpty(search))
                {
                    query = query.Where(q => q.Content.Contains(search) || q.Category.Contains(search));
                }

                if (!string.IsNullOrEmpty(category) && category != "All")
                {
                    query = query.Where(q => q.Category == category);
                }

                if (!string.IsNullOrEmpty(level) && level != "All")
                {
                    query = query.Where(q => q.Level == level);
                }

                var questions = await query.OrderByDescending(q => q.CreatedAt).ToListAsync();

                using (var workbook = new XLWorkbook())
                {
                    var worksheet = workbook.Worksheets.Add("Ngân hàng câu hỏi");

                    // Headers
                    worksheet.Cell(1, 1).Value = "Câu hỏi";
                    worksheet.Cell(1, 2).Value = "Câu chọn";
                    worksheet.Cell(1, 3).Value = "Đáp án";
                    worksheet.Cell(1, 4).Value = "Giải thích";
                    worksheet.Cell(1, 5).Value = "Ảnh";

                    var headerRow = worksheet.Row(1);
                    headerRow.Style.Font.Bold = true;
                    headerRow.Style.Fill.BackgroundColor = XLColor.LightGray;
                    headerRow.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                    int row = 2;
                    foreach (var q in questions)
                    {
                        worksheet.Cell(row, 1).Value = q.Content;
                        
                        // Option format: 1 column, multiple lines
                        var options = new List<string>();
                        if (!string.IsNullOrEmpty(q.OptionA)) options.Add($"A. {q.OptionA}");
                        if (!string.IsNullOrEmpty(q.OptionB)) options.Add($"B. {q.OptionB}");
                        if (!string.IsNullOrEmpty(q.OptionC)) options.Add($"C. {q.OptionC}");
                        if (!string.IsNullOrEmpty(q.OptionD)) options.Add($"D. {q.OptionD}");
                        
                        worksheet.Cell(row, 2).Value = string.Join(Environment.NewLine, options);
                        worksheet.Cell(row, 3).Value = q.CorrectOption;
                        worksheet.Cell(row, 4).Value = q.Explanation;

                        if (!string.IsNullOrEmpty(q.ImageUrl))
                        {
                            try
                            {
                                var relPath = q.ImageUrl.StartsWith("/") ? q.ImageUrl.Substring(1) : q.ImageUrl;
                                var imgPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", relPath.Replace("/", "\\"));
                                
                                if (System.IO.File.Exists(imgPath))
                                {
                                    var pic = worksheet.AddPicture(imgPath)
                                        .MoveTo(worksheet.Cell(row, 5))
                                        .WithSize(100, 100);
                                    
                                    worksheet.Row(row).Height = 80; // Make row tall enough
                                }
                                else
                                {
                                    worksheet.Cell(row, 5).Value = "Ảnh không tồn tại";
                                }
                            }
                            catch
                            {
                                worksheet.Cell(row, 5).Value = "Lỗi tải ảnh";
                            }
                        }

                        // Enable text wrapping
                        worksheet.Row(row).Style.Alignment.WrapText = true;
                        
                        row++;
                    }

                    worksheet.Column(1).Width = 50;
                    worksheet.Column(2).Width = 40;
                    worksheet.Column(3).Width = 15;
                    worksheet.Column(4).Width = 50;
                    worksheet.Column(5).Width = 20;

                    using (var stream = new MemoryStream())
                    {
                        workbook.SaveAs(stream);
                        var content = stream.ToArray();
                        return File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "QuestionBank.xlsx");
                    }
                }
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Lỗi khi xuất dữ liệu ra Excel.", error = ex.Message });
            }
        }

        [HttpPost("upload-image")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> UploadQuestionImage(
            IFormFile file,
            [FromServices] IWebHostEnvironment env)
        {
            if (file == null || file.Length == 0)
                return BadRequest(new { message = "Vui lòng chọn một file ảnh." });

            var allowed = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" };
            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (!allowed.Contains(ext))
                return BadRequest(new { message = "Chỉ hỗ trợ JPG, PNG, GIF, WEBP." });

            if (file.Length > 5 * 1024 * 1024)
                return BadRequest(new { message = "Ảnh tối đa 5MB." });

            try
            {
                var uploadsDir = Path.Combine(
                    env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"),
                    "uploads", "question-images");
                Directory.CreateDirectory(uploadsDir);

                var fileName = $"{Guid.NewGuid()}{ext}";
                var filePath = Path.Combine(uploadsDir, fileName);

                using (var stream = new FileStream(filePath, FileMode.Create))
                    await file.CopyToAsync(stream);

                var imageUrl = $"/uploads/question-images/{fileName}";
                return Ok(new { imageUrl });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Lỗi khi lưu ảnh.", error = ex.Message });
            }
        }
    }
}
