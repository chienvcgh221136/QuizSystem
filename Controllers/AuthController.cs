using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using QuizApi.Models;

namespace QuizApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AuthController : ControllerBase
    {
        private readonly QuizDbContext _context;
        private readonly IConfiguration _configuration;

        public AuthController(QuizDbContext context, IConfiguration configuration)
        {
            _context = context;
            _configuration = configuration;
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginRequest request)
        {
            try
            {
                // Tìm user theo Username trước
                var user = await _context.Users.FirstOrDefaultAsync(u => u.Username == request.Username);
                
                if (user == null)
                {
                    Console.WriteLine($"[Login] Thất bại: Không tìm thấy Username '{request.Username}'");
                    return Unauthorized(new { message = "Sai tài khoản, mật khẩu hoặc tài khoản đã bị khóa." });
                }

                bool isPasswordMatch = BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash);
                Console.WriteLine($"[Login] Kiểm tra User: {user.Username}");
                Console.WriteLine($"[Login] Mật khẩu khớp: {isPasswordMatch}");
                Console.WriteLine($"[Login] Trạng thái Active: {user.IsActive}");

                // Kiểm tra mật khẩu và tài khoản đang hoạt động
                if (!isPasswordMatch || user.IsActive == false)
                {
                    return Unauthorized(new { message = "Sai tài khoản, mật khẩu hoặc tài khoản đã bị khóa." });
                }

                // Tạo Token
                var tokenHandler = new JwtSecurityTokenHandler();
                var secretKey = Environment.GetEnvironmentVariable("JWT_SECRET_KEY") ?? _configuration["JwtSettings:SecretKey"]!;
                var key = Encoding.ASCII.GetBytes(secretKey);
                
                var tokenDescriptor = new SecurityTokenDescriptor
                {
                    Subject = new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.NameIdentifier, user.UserId.ToString()),
                        new Claim(ClaimTypes.Name, user.Username!),
                        new Claim(ClaimTypes.Role, user.Role ?? "User") // Cấp role từ Database
                    }),
                    Expires = DateTime.UtcNow.AddMinutes(Convert.ToDouble(Environment.GetEnvironmentVariable("JWT_EXPIRATION") ?? _configuration["JwtSettings:ExpirationInMinutes"] ?? "60")),
                    Issuer = Environment.GetEnvironmentVariable("JWT_ISSUER") ?? _configuration["JwtSettings:Issuer"],
                    Audience = Environment.GetEnvironmentVariable("JWT_AUDIENCE") ?? _configuration["JwtSettings:Audience"],
                    SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
                };

                var token = tokenHandler.CreateToken(tokenDescriptor);
                var tokenString = tokenHandler.WriteToken(token);

                return Ok(new
                {
                    message = "Đăng nhập thành công!",
                    token = tokenString,
                    user = new { user.UserId, user.Username, user.FullName, user.Role }
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = "Lỗi hệ thống khi đăng nhập.", error = ex.Message });
            }
        }
    }
}
