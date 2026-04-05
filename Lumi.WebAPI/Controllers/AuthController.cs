using Lumi.Application.DTOs;
using Lumi.Application.Exceptions;
using Lumi.Application.Interfaces;
using Lumi.Domain.Entities;
using Lumi.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace Lumi.WebAPI.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AuthController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IAuthService _authService;

        public AuthController(IAuthService authService, ApplicationDbContext context)
        {
            _authService = authService;
            _context = context;
        }

        //////////////////////////////////////////////////////
        /// REGISTER
        //////////////////////////////////////////////////////
        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] RegisterDto request)
        {
            try
            {
                var username = await _authService.RegisterAsync(request);

                return Ok(new
                {
                    message = $"Tài khoản {username} đã được đăng ký thành công."
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        //////////////////////////////////////////////////////
        /// LOGIN
        //////////////////////////////////////////////////////
        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginDto request)
        {
            try
            {
                // Gọi service xử lý
                var result = await _authService.LoginAsync(request);

                // Trả về kết quả, lúc này result đã có IsFirstLogin nhờ bước 1 & 2
                return Ok(new
                {
                    accessToken = result.AccessToken,
                    refreshToken = result.RefreshToken,
                    isFirstLogin = result.IsFirstLogin, // Hết lỗi biên dịch ở đây
                    message = "Đăng nhập thành công"
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        //////////////////////////////////////////////////////
        /// LOGOUT ALL DEVICES
        //////////////////////////////////////////////////////
        [Authorize]
        [HttpPost("logout-all")]
        public async Task<IActionResult> LogoutAll()
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier);

            if (userIdClaim == null)
                return Unauthorized();

            int userId = int.Parse(userIdClaim.Value);

            await _authService.LogoutAllDevicesAsync(userId);

            return Ok(new
            {
                message = "Đã đăng xuất khỏi tất cả thiết bị."
            });
        }

        //////////////////////////////////////////////////////
        /// PROFILE
        //////////////////////////////////////////////////////
        [Authorize]
        [HttpGet("profile")]
        public IActionResult GetProfile()
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            return Ok(new
            {
                message = $"Chào bạn, đây là dữ liệu của User ID: {userId}"
            });
        }

        //////////////////////////////////////////////////////
        /// REFRESH TOKEN
        //////////////////////////////////////////////////////
        [HttpPost("refresh-token")]
        public async Task<IActionResult> RefreshToken([FromBody] RefreshTokenRequestDto request)
        {
            var result = await _authService.RefreshTokenAsync(request);
            return Ok(result);
        }

        //////////////////////////////////////////////////////
        /// CURRENT USER
        //////////////////////////////////////////////////////
        [Authorize]
        [HttpGet("me")]
        public async Task<IActionResult> GetMe()
        {
            var username = User.Identity?.Name;

            var user = await _context.Users
                .Include(u => u.Role)
                .Where(u => u.Username == username)
                .Select(u => new
                {
                    u.Id,
                    u.Username,
                    u.FullName,
                    u.Email,
                    u.EmployeeCode,
                    u.AvatarPath,
                    Role = u.Role != null ? u.Role.Name : "Employee"
                })
                .FirstOrDefaultAsync();

            return Ok(user);
        }

        [Authorize]
        [HttpPost("change-password-first-time")]
        public async Task<IActionResult> ChangePasswordFirstTime([FromBody] ChangePasswordFirstTimeDto request)
        {
            try
            {
                var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdStr)) return Unauthorized();
                int userId = int.Parse(userIdStr);

                await _authService.ChangePasswordFirstTimeAsync(userId, request);
                return Ok(new { message = "Cập nhật mật khẩu thành công. Bây giờ bạn có thể truy cập đầy đủ các tính năng." });
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        //////////////////////////////////////////////////////
        /// CURRENT USER (BY TOKEN)
        //////////////////////////////////////////////////////
        [Authorize]
        [HttpGet("current-user")]
        public async Task<IActionResult> GetCurrentUser()
        {
            var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

            if (string.IsNullOrEmpty(userIdStr))
                return Unauthorized();

            var user = await _context.Users
                .Where(u => u.Id == int.Parse(userIdStr))
                .Select(u => new
                {
                    u.Id,
                    u.Username,
                    u.FullName,
                    u.Email,
                    u.EmployeeCode,
                    u.AvatarPath
                })
                .FirstOrDefaultAsync();

            return Ok(user);
        }
    }
}