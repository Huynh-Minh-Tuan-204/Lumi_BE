using Lumi.Domain.Entities;
using Lumi.Infrastructure.Data;
using Lumi.Infrastructure.Hubs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

namespace Lumi.WebAPI.Controllers
{
    [Authorize]
    [Route("api/[controller]")]
    [ApiController]
    public class AdminController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IHubContext<ChatHub> _hubContext;
        private readonly ILogger<AdminController> _logger;

        public AdminController(ApplicationDbContext context, IHubContext<ChatHub> hubContext, ILogger<AdminController> _logger)
        {
            _context = context;
            _hubContext = hubContext;
            this._logger = _logger;
        }

        [HttpGet("get-all-users")]
        public async Task<IActionResult> GetAllUsers()
        {
            var activeConnections = await _context.SignalRConnections
                .Where(c => c.IsActive)
                .Select(c => c.UserId)
                .Distinct()
                .ToListAsync();

            var activeSet = new HashSet<int>(activeConnections);

            var users = await _context.Users
                .Include(u => u.Role)
                .ToListAsync();

            var result = users.Select(u => new {
                u.Id,
                u.Username,
                u.FullName,
                u.Email,
                u.EmployeeCode,
                u.Phone,
                u.IsActive,
                IsOnline = activeSet.Contains(u.Id),
                u.AvatarPath,
                RoleName = u.Role != null ? u.Role.Name : "Employee"
            });
            return Ok(result);
        }

        [HttpGet("get-announcements")]
        public async Task<IActionResult> GetAnnouncements()
        {
            var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdStr) || !int.TryParse(userIdStr, out var userId)) 
                return Unauthorized();

            var announcements = await _context.Messages
                .Where(m => m.MessageType == "Announcement" && (m.IsDeleted ?? false) == false)
                .OrderByDescending(m => m.CreatedAt)
                .ToListAsync();

            // Filter for target users (Private announcements logic)
            var filtered = announcements
                .Where(m => {
                    if (string.IsNullOrEmpty(m.IV) || m.IV == "SYSTEM_MSG") return true; 
                    var targetIds = m.IV.Split(',', StringSplitOptions.RemoveEmptyEntries);
                    return targetIds.Any(id => id == userId.ToString());
                })
                .Select(m => new {
                    sender = "📢 HỆ THỐNG",
                    message = m.EncryptedContent,
                    isSystem = true,
                    time = m.CreatedAt
                }).ToList();

            return Ok(filtered);
        }

        [HttpPost("send-announcement")]
        public async Task<IActionResult> SendAnnouncement([FromBody] AnnouncementRequest request)
        {
            var adminIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(adminIdStr)) return Unauthorized();
            int adminId = int.Parse(adminIdStr);

            if (string.IsNullOrEmpty(request.Message))
                return BadRequest(new { error = "Nội dung thông báo không được để trống" });

            try
            {
                // 1. Lưu vào bảng Messages để làm History
                // Đảm bảo có ít nhất 1 Conversation hợp lệ (ID=1) để gán cho Announcement
                var systemConv = await _context.Conversations.FirstOrDefaultAsync(c => c.Id == 1);
                if (systemConv == null)
                {
                    // Tạo conversation ID=1 nếu chưa có (Thường dùng cho Public/System Announcements)
                    systemConv = new Conversation 
                    { 
                        Name = "Hệ thống thông báo", 
                        Type = "System", 
                        CreatedBy = adminId,
                        CreatedAt = DateTime.UtcNow,
                        LastMessageAt = DateTime.UtcNow
                    };
                    _context.Conversations.Add(systemConv);
                    await _context.SaveChangesAsync();
                }

                var announcement = new Message
                {
                    SenderId = adminId,
                    EncryptedContent = request.Message,
                    MessageType = "Announcement",
                    CreatedAt = DateTime.UtcNow,
                    ConversationId = systemConv.Id, // FIX: Gán ID cụ thể, không để NULL
                    IV = "SYSTEM_MSG"
                };

                _context.Messages.Add(announcement);
                await _context.SaveChangesAsync();

                // 2. Broadcast SignalR tới tất cả mọi người (Realtime)
                await _hubContext.Clients.All.SendAsync("ReceiveNotification", new {
                    id = announcement.Id,
                    sender = "📢 HỆ THỐNG", 
                    message = announcement.EncryptedContent, 
                    isSystem = true, 
                    createdAt = announcement.CreatedAt, 
                    senderId = adminId 
                });

                return Ok(new { message = "Gửi thông báo thành công!" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending announcement");
                return BadRequest(new { error = "Lỗi khi gửi thông báo: " + ex.Message });
            }
        }

        [HttpGet("my-conversations")]
        public async Task<IActionResult> GetMyConversations()
        {
            var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdStr)) return Unauthorized();

            int userId = int.Parse(userIdStr);
            var conversations = await _context.ConversationMembers
                .Where(cm => cm.UserId == userId && cm.IsActive == true)
                .Select(cm => new {
                    cm.Conversation.Id,
                    cm.Conversation.Name,
                    cm.Conversation.Type,
                    LastMessage = _context.Messages
                        .Where(m => m.ConversationId == cm.ConversationId && (m.IsDeleted ?? false) == false)
                        .OrderByDescending(m => m.CreatedAt)
                        .Select(m => new { m.EncryptedContent, m.CreatedAt })
                        .FirstOrDefault(),
                    OtherUserId = cm.Conversation.Type == "Private" 
                        ? (int?)_context.ConversationMembers
                            .Where(x => x.ConversationId == cm.ConversationId && x.UserId != userId)
                            .Select(x => x.UserId)
                            .FirstOrDefault()
                        : null
                }).ToListAsync();
            return Ok(conversations);
        }

        [HttpGet("group-members/{convId}")]
        public async Task<IActionResult> GetGroupMembers(int convId)
        {
            var members = await _context.ConversationMembers
                .Where(cm => cm.ConversationId == convId)
                .Select(cm => new { Id = cm.User.Id, cm.User.FullName, cm.User.AvatarPath, cm.User.IsActive })
                .ToListAsync();
            return Ok(members);
        }

        [HttpGet("chat-history/{convId}")]
        public async Task<IActionResult> GetChatHistory(int convId)
        {
            var messages = await _context.Messages
                .Where(m => m.ConversationId == convId && (m.IsDeleted ?? false) == false)
                .OrderBy(m => m.CreatedAt)
                .Select(m => new {
                    sender = m.Sender.FullName ?? m.Sender.Username,
                    message = m.EncryptedContent,
                    time = m.CreatedAt,
                    isSystem = m.MessageType == "Announcement"
                }).ToListAsync();
            return Ok(messages);
        }

        [HttpPost("create-group")]
        public async Task<IActionResult> CreateGroup([FromBody] string groupName)
        {
            var adminIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(adminIdStr)) return Unauthorized();
            int adminId = int.Parse(adminIdStr);

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var conversation = new Conversation
                {
                    Name = groupName,
                    Type = "Group",
                    CreatedBy = adminId,
                    CreatedAt = DateTime.UtcNow,
                    LastMessageAt = DateTime.UtcNow
                };

                _context.Conversations.Add(conversation);
                await _context.SaveChangesAsync();

                var membership = new ConversationMember
                {
                    ConversationId = conversation.Id,
                    UserId = adminId,
                    RoleInConversation = "Owner",
                    JoinedAt = DateTime.UtcNow,
                    IsActive = true
                };

                _context.ConversationMembers.Add(membership);
                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                return Ok(new { id = conversation.Id, message = $"Đã tạo nhóm '{groupName}' thành công!" });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return BadRequest(new { error = "Lỗi khi tạo nhóm: " + ex.Message });
            }
        }

        [HttpPost("add-member-to-group")]
        public async Task<IActionResult> AddMemberToGroup(int conversationId, int userId)
        {
            var existingMember = await _context.ConversationMembers
                .FirstOrDefaultAsync(cm => cm.ConversationId == conversationId && cm.UserId == userId);

            if (existingMember != null)
            {
                if (existingMember.IsActive)
                    return BadRequest(new { error = "Nhân viên này đã có trong nhóm rồi!" });

                existingMember.IsActive = true;
                existingMember.JoinedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();
                return Ok(new { message = "Đã thêm nhân viên vào nhóm thành công!" });
            }

            var newMember = new ConversationMember
            {
                ConversationId = conversationId,
                UserId = userId,
                RoleInConversation = "Member",
                JoinedAt = DateTime.UtcNow,
                IsActive = true
            };

            _context.ConversationMembers.Add(newMember);
            await _context.SaveChangesAsync();

            return Ok(new { message = "Đã thêm nhân viên vào nhóm thành công!" });
        }

        [HttpPut("update-user/{id}")]
        public async Task<IActionResult> UpdateUser(int id, [FromBody] UpdateUserDto request)
        {
            var user = await _context.Users.FindAsync(id);
            if (user == null) return NotFound(new { error = "Không tìm thấy người dùng." });

            user.FullName = request.FullName ?? user.FullName;
            user.Email = request.Email ?? user.Email;
            user.Phone = request.Phone ?? user.Phone;
            if (request.IsActive.HasValue) user.IsActive = request.IsActive.Value;

            await _context.SaveChangesAsync();
            return Ok(new { message = "Cập nhật thành công!" });
        }

        [HttpPut("change-role/{id}")]
        public async Task<IActionResult> ChangeRole(int id, [FromBody] ChangeRoleDto request)
        {
            var user = await _context.Users.FindAsync(id);
            if (user == null) return NotFound(new { error = "Không tìm thấy người dùng." });

            user.RoleId = request.RoleId;
            await _context.SaveChangesAsync();
            return Ok(new { message = "Đổi quyền thành công!" });
        }

        [HttpDelete("delete-user/{id}")]
        public async Task<IActionResult> DeleteUserAdmin(int id)
        {
            var user = await _context.Users.FindAsync(id);
            if (user == null) return NotFound(new { error = "Không tìm thấy người dùng." });

            user.IsActive = false;
            await _context.SaveChangesAsync();
            return Ok(new { message = "Vô hiệu hóa người dùng thành công!" });
        }
    }

    public class AnnouncementRequest
    {
        public string Message { get; set; }
    }

    public class UpdateUserDto
    {
        public string FullName { get; set; }
        public string Email { get; set; }
        public string Phone { get; set; }
        public bool? IsActive { get; set; }
    }

    public class ChangeRoleDto
    {
        public int RoleId { get; set; }
    }
}