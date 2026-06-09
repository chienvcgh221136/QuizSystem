using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QuizApi.Models;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace QuizApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize(Roles = "Admin")]
    public class AdminController : ControllerBase
    {
        private readonly QuizDbContext _context;

        public AdminController(QuizDbContext context)
        {
            _context = context;
        }

        [HttpGet("stats")]
        public async Task<IActionResult> GetStats()
        {
            var totalUsers = await _context.Users.CountAsync();
            var totalExams = await _context.Exams.Where(e => e.Status == "Published").CountAsync();
            var totalQuestions = await _context.QuestionBank.CountAsync();
            
            var allResults = await _context.ExamResults
                .Where(r => r.Status == "Submitted" && r.Score != null)
                .ToListAsync();

            double avgScore = 0;
            if (allResults.Any())
            {
                // Tính % trung bình: (Score / MaxScore) * 100
                var examIds = allResults.Select(r => r.ExamId).Distinct().ToList();
                var exams = await _context.Exams.Where(e => examIds.Contains(e.ExamId)).ToDictionaryAsync(e => e.ExamId, e => e.TotalScore);
                
                avgScore = allResults.Average(r => {
                    var max = (exams.ContainsKey(r.ExamId) ? exams[r.ExamId] : 10) ?? 10.0;
                    if (max == 0) max = 10.0;
                    return (r.Score ?? 0.0) / max * 100.0;
                });
            }

            return Ok(new
            {
                TotalUsers = totalUsers,
                TotalExams = totalExams,
                TotalQuestions = totalQuestions,
                AvgScore = Math.Round(avgScore, 1)
            });
        }

        [HttpGet("recent-activity")]
        public async Task<IActionResult> GetRecentActivity()
        {
            var activity = await (from r in _context.ExamResults
                                  join u in _context.Users on r.UserId equals u.UserId
                                  join e in _context.Exams on r.ExamId equals e.ExamId
                                  orderby r.StartTime descending
                                  select new
                                  {
                                      UserName = u.FullName ?? u.Username ?? "Unknown",
                                      Initials = (u.FullName ?? u.Username ?? "U").Length > 0 
                                          ? (u.FullName ?? u.Username ?? "U").Substring(0, 1).ToUpper() 
                                          : "U",
                                      ExamTitle = e.Title,
                                      Status = r.Status,
                                      Time = r.SubmitTime ?? r.StartTime,
                                      Score = r.Score,
                                      MaxScore = e.TotalScore
                                  })
                                  .Take(10)
                                  .ToListAsync();

            return Ok(activity);
        }
    }
}
