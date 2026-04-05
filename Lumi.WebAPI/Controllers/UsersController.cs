using Lumi.Application.DTOs;
using Lumi.Domain.Entities;
using Lumi.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using System;
using System.IO;
using System.Linq;
using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using Lumi.Infrastructure.Hubs;
using System.Threading.Tasks;


namespace Lumi.WebAPI.Controllers
{
    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    public class UsersController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IPasswordHasher<User> _passwordHasher;
        private readonly IWebHostEnvironment _env;
        private readonly IHubContext<ChatHub> _hubContext;
        public UsersController(ApplicationDbContext context, IWebHostEnvironment env, IHubContext<ChatHub> hubContext)
        {
            _context = context;
            _env = env;
            _hubContext = hubContext;
            _passwordHasher = new PasswordHasher<User>();
        }
        
        // GET: /api/users
        [HttpGet]
        public async Task<IActionResult> GetUsers()
        {
            var users = await _context.Users
                .Select(u => new
                {
                    u.Id,
                    u.Username,
                    u.FullName,
                    u.Email,
                    u.EmployeeCode,
                    u.AvatarPath,
                    u.IsActive
                })
                .ToListAsync();

            return Ok(users);
        }

        // GET: /api/users/{id}
        [HttpGet("{id}")]
        public async Task<IActionResult> GetUser(int id)
        {
            var user = await _context.Users
                .Where(u => u.Id == id)
                .Select(u => new
                {
                    u.Id,
                    u.Username,
                    u.FullName,
                    u.Email,
                    u.EmployeeCode,
                    u.AvatarPath,
                    u.IsActive
                })
                .FirstOrDefaultAsync();

            if (user == null) return NotFound();
            return Ok(user);
        }

        // GET: /api/users/online
        [HttpGet("online")]
        public async Task<IActionResult> GetOnlineUsers()
        {
            var onlineIds = await _context.SignalRConnections
                .Where(sc => sc.IsActive)
                .Select(sc => sc.UserId)
                .Distinct()
                .ToListAsync();

            var users = await _context.Users
                .Where(u => onlineIds.Contains(u.Id))
                .Select(u => new
                {
                    u.Id,
                    u.Username,
                    u.FullName,
                    u.AvatarPath
                })
                .ToListAsync();

            return Ok(users);
        }

        // GET: /api/users/me
        [HttpGet("me")]
        public async Task<IActionResult> GetCurrentUser()
        {
            int currentUserId = int.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier).Value);

            var user = await _context.Users
                .AsNoTracking()
                .Where(u => u.Id == currentUserId)
                .Select(u => new
                {
                    u.Id,
                    u.Username,
                    u.FullName,
                    u.Email,
                    u.Phone,
                    u.AvatarPath,
                    u.RoleId,
                    u.DepartmentId,
                    u.IsActive,
                    u.CreatedAt,
                    u.UpdatedAt
                })
                .FirstOrDefaultAsync();

            if (user == null) return NotFound();
            return Ok(user);
        }

        // PUT: /api/users/{id}
        [HttpPut("{id}")]
        public async Task<IActionResult> UpdateUser(int id, [FromBody] Lumi.Application.DTOs.UpdateUserDto request)
        {
            int currentUserId = int.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier).Value);

            if (currentUserId != id && !User.IsInRole("Admin"))
            {
                return Forbid();
            }

            var user = await _context.Users.FindAsync(id);
            if (user == null) return NotFound();

            user.FullName = request.FullName ?? user.FullName;
            user.Email = request.Email ?? user.Email;
            user.Phone = request.Phone ?? user.Phone;
            user.RoleId = request.RoleId ?? user.RoleId;
            user.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            return Ok(new
            {
                user.Id,
                user.Username,
                user.FullName,
                user.Email,
                user.Phone,
                user.AvatarPath,
                user.RoleId,
                user.DepartmentId,
                user.IsActive,
                user.CreatedAt,
                user.UpdatedAt
            });
        }
        private int GetCurrentUserId()
        {
            return int.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier).Value);
        }

        // PATCH: /api/users/{id}/status
        [HttpPatch("{id}/status")]
        public async Task<IActionResult> ToggleStatus(int id, [FromBody] Lumi.Application.DTOs.UserStatusDto request)
        {
            int currentUserId = int.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier).Value);

            if (currentUserId != id && !User.IsInRole("Admin"))
            {
                return Forbid();
            }

            var user = await _context.Users.FindAsync(id);
            if (user == null) return NotFound();

            user.IsActive = request.IsActive;
            user.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            return NoContent();
        }
        [HttpPut("me/public-key")]
        public async Task<IActionResult> UpdatePublicKey([FromBody] string publicKey)
        {
            var userId = int.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier).Value);
            var user = await _context.Users.FindAsync(userId);
            if (user == null) return NotFound();

            user.PublicKey = publicKey;
            await _context.SaveChangesAsync();
            return NoContent();
        }
        // GET: /api/users/not-in-conversation/{conversationId}
        [HttpGet("not-in-conversation/{conversationId}")]
        public async Task<IActionResult> GetUsersNotInConversation(int conversationId)
        {
            var memberIds = await _context.ConversationMembers
                .Where(cm => cm.ConversationId == conversationId)
                .Select(cm => cm.UserId)
                .ToListAsync();

            var users = await _context.Users
                .Where(u => !memberIds.Contains(u.Id) && u.IsActive)
                .Select(u => new
                {
                    u.Id,
                    u.Username,
                    u.FullName,
                    u.AvatarPath,
                    u.Email
                })
                .ToListAsync();

            return Ok(users);
        }

        [HttpPost("change-password-first-login")]
        public async Task<IActionResult> ChangePasswordFirstLogin([FromBody] ChangePasswordDto model)
        {
            var userId = GetCurrentUserId(); // Lấy ID từ Token
            var user = await _context.Users.FindAsync(userId);

            // Logic hash mật khẩu mới và lưu vào DB
            user.PasswordHash = _passwordHasher.HashPassword(user,model.NewPassword);

            // QUAN TRỌNG: Đánh dấu đã hết lần đầu đăng nhập
            user.IsFirstLogin = false;

            await _context.SaveChangesAsync();
            return Ok();
        }

        [HttpPost("avatar")]
        public async Task<IActionResult> UploadAvatar([FromForm] IFormFile file)
        {
            if (file == null || file.Length == 0) return BadRequest(new { error = "File is required." });

            var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdStr)) return Unauthorized();

            int userId = int.Parse(userIdStr);
            var user = await _context.Users.FindAsync(userId);
            if (user == null) return NotFound("User not found.");

            // Create uploads/avatars directory if not exists
            var uploadsRoot = Path.Combine(_env.WebRootPath ?? "wwwroot", "uploads", "avatars");
            Directory.CreateDirectory(uploadsRoot);

            var ext = Path.GetExtension(file.FileName);
            var fileName = $"avatar_{userId}_{Guid.NewGuid()}{ext}";
            var filePath = Path.Combine(uploadsRoot, fileName);

            await using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }

            var relativePath = $"/uploads/avatars/{fileName}";

            // Delete old avatar if it exists
            if (!string.IsNullOrEmpty(user.AvatarPath))
            {
                try
                {
                    var oldFilePath = Path.Combine(_env.WebRootPath ?? "wwwroot", user.AvatarPath.TrimStart('/'));
                    if (System.IO.File.Exists(oldFilePath))
                    {
                        System.IO.File.Delete(oldFilePath);
                    }
                }
                catch { } // Ignore deletion errors
            }

            user.AvatarPath = relativePath;
            user.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            // Emit UserUpdated for realtime sync
            await _hubContext.Clients.All.SendAsync("UserUpdated", user.Id, user.AvatarPath);

            return Ok(new { avatarPath = relativePath });
        }
    }
}
