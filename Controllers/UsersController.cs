using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QuizApi.Models;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BCrypt.Net;
namespace QuizApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize(Roles = "Admin")]
    public class UsersController : ControllerBase
    {
        private readonly QuizDbContext _context;

        public UsersController(QuizDbContext context)
        {
            _context = context;
        }

        [HttpGet]
        public async Task<ActionResult<IEnumerable<object>>> GetUsers()
        {
            return await _context.Users
                .Select(u => new {
                    u.UserId,
                    u.Username,
                    u.FullName,
                    u.Email,
                    u.Role,
                    u.CreatedAt
                })
                .ToListAsync();
        }

        [HttpGet("{id}")]
        public async Task<ActionResult<object>> GetUser(int id)
        {
            var user = await _context.Users
                .Select(u => new {
                    u.UserId,
                    u.Username,
                    u.FullName,
                    u.Email,
                    u.Role,
                    u.CreatedAt
                })
                .FirstOrDefaultAsync(u => u.UserId == id);

            if (user == null) return NotFound();
            return user;
        }

        [HttpPost]
        public async Task<ActionResult<User>> CreateUser(User user)
        {
            if (await _context.Users.AnyAsync(u => u.Username == user.Username))
                return BadRequest(new { message = "Username already exists" });

            string rawPassWord= string.IsNullOrEmpty(user.PasswordHash) ? "123456" : user.PasswordHash;
            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(rawPassWord);
            
            user.CreatedAt = System.DateTime.Now;
            _context.Users.Add(user);
            await _context.SaveChangesAsync();

            return CreatedAtAction(nameof(GetUser), new { id = user.UserId }, user);
        }

        [HttpPut("{id}")]
        public async Task<IActionResult> UpdateUser(int id, User user)
        {
            if (id != user.UserId) return BadRequest();

            var existing = await _context.Users.FindAsync(id);
            if (existing == null) return NotFound();

            existing.FullName = user.FullName;
            existing.Email = user.Email;
            existing.Role = user.Role;
            if(!string.IsNullOrEmpty(user.PasswordHash))
                existing.PasswordHash = BCrypt.Net.BCrypt.HashPassword(user.PasswordHash);
            try { await _context.SaveChangesAsync(); }
            catch (DbUpdateConcurrencyException) { if (!UserExists(id)) return NotFound(); else throw; }

            return NoContent();
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteUser(int id)
        {
            var user = await _context.Users.FindAsync(id);
            if (user == null) return NotFound();

            _context.Users.Remove(user);
            await _context.SaveChangesAsync();

            return NoContent();
        }

        [HttpGet("{id}/history")]
        public async Task<ActionResult<IEnumerable<object>>> GetUserHistory(int id)
        {
            var history = await _context.ExamResults
                .Where(r => r.UserId == id && r.Status == "Submitted")
                .Join(_context.Exams, 
                    r => r.ExamId, 
                    e => e.ExamId, 
                    (r, e) => new {
                        r.ResultId,
                        e.Title,
                        r.Score,
                        e.TotalScore,
                        r.StartTime,
                        r.SubmitTime,
                        Duration = r.SubmitTime != null ? (r.SubmitTime - r.StartTime) : null
                    })
                .OrderByDescending(r => r.SubmitTime)
                .ToListAsync();

            return Ok(history.Select(h => new {
                h.ResultId,
                h.Title,
                h.Score,
                h.TotalScore,
                Duration = h.Duration.HasValue ? $"{(int)h.Duration.Value.TotalMinutes} phút {h.Duration.Value.Seconds} giây" : "N/A",
                SubmitTime = h.SubmitTime?.ToString("dd/MM/yyyy HH:mm")
            }));
        }

        private bool UserExists(int id) => _context.Users.Any(e => e.UserId == id);
    }
}
