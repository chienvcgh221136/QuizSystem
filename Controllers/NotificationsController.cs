using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using QuizApi.Models;

namespace QuizApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class NotificationsController : ControllerBase
    {
        private readonly QuizDbContext _context;

        public NotificationsController(QuizDbContext context)
        {
            _context = context;
        }

        [HttpGet]
        public async Task<IActionResult> GetNotifications()
        {
            try
            {
                var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                int? userId = null;
                if (int.TryParse(userIdStr, out int parsedId))
                {
                    userId = parsedId;
                }

                var notifications = await _context.Notifications
                    .Where(n => n.UserId == null || n.UserId == userId)
                    .OrderByDescending(n => n.CreatedAt)
                    .ToListAsync();

                return Ok(notifications);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Lỗi hệ thống khi lấy danh sách thông báo.", error = ex.Message });
            }
        }

        [HttpPost("mark-all-read")]
        public async Task<IActionResult> MarkAllRead()
        {
            try
            {
                var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                int? userId = null;
                if (int.TryParse(userIdStr, out int parsedId))
                {
                    userId = parsedId;
                }

                var unreadNotifications = await _context.Notifications
                    .Where(n => (n.UserId == null || n.UserId == userId) && !n.IsRead)
                    .ToListAsync();

                if (unreadNotifications.Any())
                {
                    foreach (var noti in unreadNotifications)
                    {
                        noti.IsRead = true;
                    }
                    await _context.SaveChangesAsync();
                }

                return Ok(new { message = "Đã đánh dấu tất cả thông báo là đã đọc." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Lỗi hệ thống khi cập nhật trạng thái thông báo.", error = ex.Message });
            }
        }
    }
}
