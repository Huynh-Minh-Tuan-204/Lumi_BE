using Lumi.Application.DTOs;
using Lumi.Infrastructure.Data;
using Lumi.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.SignalR;
using Lumi.Infrastructure.Hubs;
using System.Security.Claims;
using System.Threading.Tasks;

namespace Lumi.WebAPI.Controllers
{
    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    public class ConversationsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IHubContext<ChatHub> _hubContext;
        private readonly ILogger<ConversationsController> _logger;

        public ConversationsController(ApplicationDbContext context, IHubContext<ChatHub> hubContext, ILogger<ConversationsController> logger)
        {
            _context = context;
            _hubContext = hubContext;
            _logger = logger;
        }

        // POST: /api/conversations/{id}/members
        [HttpPost("{id}/members")]
        public async Task<IActionResult> AddMemberAsync(int id, [FromBody] AddMemberDto request)
        {
            var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdStr) || !int.TryParse(userIdStr, out var currentUserId)) 
                return Unauthorized();

            if (request == null || request.UserId <= 0) return BadRequest(new { error = "UserId is required." });

            var conv = await _context.Conversations.FindAsync(id);
            if (conv == null) return NotFound(new { error = "Conversation not found." });

            var isCreator = conv.CreatedBy == currentUserId;
            var isAdmin = await _context.ConversationMembers
                .AnyAsync(cm => cm.ConversationId == id && cm.UserId == currentUserId && cm.RoleInConversation == "Admin" && cm.IsActive);

            if (!isCreator && !isAdmin) return Forbid();

            var existing = await _context.ConversationMembers
                .FirstOrDefaultAsync(cm => cm.ConversationId == id && cm.UserId == request.UserId);

            if (existing != null)
            {
                if (existing.IsActive)
                    return Conflict(new { error = "User is already a member." });

                existing.IsActive = true;
                existing.JoinedAt = DateTime.UtcNow;
                existing.LeftAt = null;
                existing.RoleInConversation = request.RoleInConversation ?? existing.RoleInConversation ?? "Member";

                await _context.SaveChangesAsync();

                return Ok(new
                {
                    existing.Id,
                    existing.UserId,
                    existing.RoleInConversation,
                    existing.JoinedAt
                });
            }

            var member = new ConversationMember
            {
                ConversationId = id,
                UserId = request.UserId,
                RoleInConversation = request.RoleInConversation ?? "Member",
                JoinedAt = DateTime.UtcNow,
                IsActive = true
            };

            _context.ConversationMembers.Add(member);
            await _context.SaveChangesAsync();

            return Ok(new
            {
                member.Id,
                member.UserId,
                member.RoleInConversation,
                member.JoinedAt
            });
        }

        // GET: /api/conversations/my
        [HttpGet("my")]
        public async Task<IActionResult> GetMyConversationsAsync()
        {
            var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdStr) || !int.TryParse(userIdStr, out var userId)) 
                return Unauthorized();

            try
            {

            var conversationsRaw = await _context.ConversationMembers.AsNoTracking()
                .Where(cm => cm.UserId == userId && cm.IsActive)
                .Select(cm => cm.Conversation)
                .Select(c => new
                {
                    c.Id,
                    c.Name,
                    c.Type,
                    c.AvatarPath,
                    c.BackgroundPath,
                    c.LastMessageAt,
                    c.CreatedBy,
                    LastMessage = _context.Messages
                        .Where(m => m.ConversationId == c.Id && m.IsDeleted != true)
                        .OrderByDescending(m => m.CreatedAt)
                        .Select(m => new { 
                            m.EncryptedContent, 
                            CreatedAt = m.CreatedAt, 
                            m.MessageType, 
                            m.SenderId, 
                            IsRead = m.IsRead == true 
                        })
                        .FirstOrDefault(),
                    UnreadCount = _context.Messages
                        .Where(m => m.ConversationId == c.Id && m.SenderId != userId && m.IsDeleted != true && !m.MessageReads.Any(mr => mr.UserId == userId))
                        .Count(),
                    OtherUserId = c.Type == "Private" 
                        ? (int?)_context.ConversationMembers
                            .Where(cm2 => cm2.ConversationId == c.Id && cm2.UserId != userId)
                            .Select(cm2 => cm2.UserId)
                            .FirstOrDefault()
                        : null
                })
                .OrderByDescending(c => c.LastMessageAt)
                .ToListAsync();

            var conversations = conversationsRaw.Select(c => new
            {
                c.Id,
                c.Name,
                c.Type,
                c.AvatarPath,
                c.BackgroundPath,
                LastMessageAt = c.LastMessageAt != null ? DateTime.SpecifyKind(c.LastMessageAt.GetValueOrDefault(), DateTimeKind.Utc).ToString("o") : null,
                c.CreatedBy,
                LastMessage = c.LastMessage == null ? null : new {
                    EncryptedContent = c.LastMessage.EncryptedContent ?? "",
                    CreatedAt = DateTime.SpecifyKind(c.LastMessage.CreatedAt, DateTimeKind.Utc).ToString("o"),
                    MessageType = c.LastMessage.MessageType ?? "Text",
                    SenderId = c.LastMessage.SenderId,
                    isRead = c.LastMessage.IsRead
                },
                unreadCount = c.UnreadCount,
                otherUserId = c.OtherUserId
            });

            return Ok(conversations);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi xảy ra tại endpoint /api/Conversations/my: {Message}", ex.Message);
                return StatusCode(500, ex.InnerException?.Message ?? ex.Message);
            }
        }





        // POST: /api/conversations/private
        [HttpPost("private")]
        public async Task<IActionResult> CreatePrivateAsync([FromBody] CreatePrivateConversationDto request)
        {
            var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdStr)) return Unauthorized();
            if (!int.TryParse(userIdStr, out var userId)) return Unauthorized();

            if (request == null || request.OtherUserId <= 0) return BadRequest(new { error = "OtherUserId is required." });
            if (request.OtherUserId == userId) return BadRequest(new { error = "Cannot create private conversation with yourself." });

            // Find existing PRIVATE conversation between these two users (exactly these two)
            var existingConversation = await _context.Conversations
                .Where(c => c.Type == "Private")
                .Where(c => c.Members.Count(m => m.IsActive) == 2 &&
                            c.Members.Any(m => m.UserId == userId && m.IsActive) && 
                            c.Members.Any(m => m.UserId == request.OtherUserId && m.IsActive))
                .Select(c => new { c.Id, c.Name, c.Type })
                .FirstOrDefaultAsync();

            if (existingConversation != null)
            {
                return Ok(existingConversation);
            }

            var otherUser = await _context.Users.FindAsync(request.OtherUserId);
            var name = otherUser != null ? otherUser.FullName ?? otherUser.Username : "";

            var conversation = new Conversation
            {
                Name = name,
                Type = "Private",
                CreatedBy = userId,
                CreatedAt = DateTime.UtcNow,
                LastMessageAt = DateTime.UtcNow
            };

            _context.Conversations.Add(conversation);
            await _context.SaveChangesAsync();

            var members = new[]
            {
                new ConversationMember
                {
                    ConversationId = conversation.Id,
                    UserId = userId,
                    RoleInConversation = "Member",
                    JoinedAt = DateTime.UtcNow,
                    IsActive = true
                },
                new ConversationMember
                {
                    ConversationId = conversation.Id,
                    UserId = request.OtherUserId,
                    RoleInConversation = "Member",
                    JoinedAt = DateTime.UtcNow,
                    IsActive = true
                }
            };

            _context.ConversationMembers.AddRange(members);
            await _context.SaveChangesAsync();

            return Ok(new { conversation.Id, conversation.Name, conversation.Type });
        }

        // GET: /api/conversations/{id}
        [HttpGet("{id}")]
        public async Task<IActionResult> GetConversationAsync(int id)
        {
            var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdStr) || !int.TryParse(userIdStr, out var currentUserId)) 
                return Unauthorized();

            var isMember = await _context.ConversationMembers
                .AnyAsync(cm => cm.ConversationId == id && cm.UserId == currentUserId && cm.IsActive);
            if (!isMember) return Forbid();

            var convRaw = await _context.Conversations
                .Where(c => c.Id == id)
                .Select(c => new
                {
                    c.Id,
                    c.Name,
                    c.Type,
                    c.CreatedBy,
                    c.CreatedAt,
                    c.UpdatedAt,
                    c.LastMessageAt
                })
                .FirstOrDefaultAsync();

            if (convRaw == null) return NotFound();

            var conv = new
            {
                convRaw.Id,
                convRaw.Name,
                convRaw.Type,
                convRaw.CreatedBy,
                CreatedAt = DateTime.SpecifyKind(convRaw.CreatedAt, DateTimeKind.Utc).ToString("o"),
                UpdatedAt = convRaw.UpdatedAt != null ? DateTime.SpecifyKind(convRaw.UpdatedAt.GetValueOrDefault(), DateTimeKind.Utc).ToString("o") : null,
                LastMessageAt = convRaw.LastMessageAt != null ? DateTime.SpecifyKind(convRaw.LastMessageAt.GetValueOrDefault(), DateTimeKind.Utc).ToString("o") : null
            };

            return Ok(conv);
        }

        // GET: /api/conversations/{id}/members
        [HttpGet("{id}/members")]
        public async Task<IActionResult> GetMembersAsync(int id)
        {
            var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdStr) || !int.TryParse(userIdStr, out var currentUserId)) 
                return Unauthorized();

            var isMember = await _context.ConversationMembers
                .AnyAsync(cm => cm.ConversationId == id && cm.UserId == currentUserId && cm.IsActive);
            if (!isMember) return Forbid();

            var members = await _context.ConversationMembers
                .Where(cm => cm.ConversationId == id)
                .Include(cm => cm.User)
                .Select(cm => new
                {
                    cm.Id,
                    cm.UserId,
                    FullName = cm.User.FullName,
                    cm.RoleInConversation,
                    cm.JoinedAt,
                    cm.LeftAt,
                    cm.IsActive
                })
                .ToListAsync();

            return Ok(members);
        }

        // PUT: /api/conversations/{id}/name
        [HttpPut("{id}/name")]
        public async Task<IActionResult> RenameConversationAsync(int id, [FromBody] RenameConversationDto request)
        {
            var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdStr) || !int.TryParse(userIdStr, out var currentUserId)) 
                return Unauthorized();

            var conv = await _context.Conversations.FindAsync(id);
            if (conv == null) return NotFound();

            var isCreator = conv.CreatedBy == currentUserId;
            var isAdmin = await _context.ConversationMembers
                .AnyAsync(cm => cm.ConversationId == id && cm.UserId == currentUserId && cm.RoleInConversation == "Admin" && cm.IsActive);

            if (!isCreator && !isAdmin) return Forbid();

            conv.Name = request.Name ?? conv.Name;
            conv.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            return Ok(new { conv.Id, conv.Name });
        }

        // POST: /api/conversations/{id}/leave
        [HttpPost("{id}/leave")]
        public async Task<IActionResult> LeaveConversationAsync(int id)
        {
            var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdStr) || !int.TryParse(userIdStr, out var currentUserId)) 
                return Unauthorized();

            var member = await _context.ConversationMembers
                .FirstOrDefaultAsync(cm => cm.ConversationId == id && cm.UserId == currentUserId && cm.IsActive);
            if (member == null) return NotFound();

            member.IsActive = false;
            member.LeftAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            return NoContent();
        }

        // POST: /api/conversations/{id}/read
        [HttpPost("{id}/read")]
        public async Task<IActionResult> MarkConversationReadAsync(int id)
        {
            try 
            {
                var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdStr)) return Unauthorized();
                int userId = int.Parse(userIdStr);

                // Cập nhập IsRead = true cho tất cả tin nhắn của đối phương trong cuộc hội thoại đó
                var unreadMessages = await _context.Messages
                    .Where(m => m.ConversationId == id && m.SenderId != userId && m.IsDeleted != true && !m.MessageReads.Any(mr => mr.UserId == userId))
                    .ToListAsync();

                if (!unreadMessages.Any()) return NoContent();

                var device = await _context.UserDevices
                    .Where(ud => ud.UserId == userId && ud.IsActive)
                    .OrderByDescending(ud => ud.LastSeen)
                    .FirstOrDefaultAsync();

                if (device == null)
                {
                    device = new UserDevice
                    {
                        UserId = userId,
                        DeviceName = "Auto-Registered Device (REST)",
                        DeviceType = "Web",
                        DeviceIdentifier = Guid.NewGuid().ToString(),
                        IsActive = true,
                        IsRevoked = false,
                        CreatedAt = DateTime.UtcNow,
                        LastSeen = DateTime.UtcNow
                    };
                    _context.UserDevices.Add(device);
                    await _context.SaveChangesAsync();
                }
                int deviceId = device.Id;

                foreach(var msg in unreadMessages)
                {
                    _context.MessageReads.Add(new MessageRead
                    {
                        MessageId = msg.Id,
                        UserId = userId,
                        DeviceId = deviceId,
                        ReadAt = DateTime.UtcNow
                    });
                }
                
                await _context.SaveChangesAsync();
                return Ok(new { message = "Tất cả tin nhắn đã được đánh dấu là đã đọc" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, ex.InnerException?.Message ?? ex.Message);
            }
        }

        // Alias for MarkConversationRead as requested by user
        [HttpPost("{id}/MarkAsRead")]
        public async Task<IActionResult> MarkAsReadAsync(int id)
        {
            return await MarkConversationReadAsync(id);
        }

        // DELETE: /api/conversations/{id}/members/{userId}
        [HttpDelete("{id}/members/{userId}")]
        public async Task<IActionResult> RemoveMemberAsync(int id, int userId)
        {
            var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdStr) || !int.TryParse(userIdStr, out var currentUserId)) 
                return Unauthorized();

            var isAdmin = await _context.ConversationMembers
                .AnyAsync(cm => cm.ConversationId == id && cm.UserId == currentUserId && cm.RoleInConversation == "Admin" && cm.IsActive);
            var conv = await _context.Conversations.FindAsync(id);
            var isCreator = conv != null && conv.CreatedBy == currentUserId;

            if (!isAdmin && !isCreator && currentUserId != userId) return Forbid();

            var member = await _context.ConversationMembers
                .FirstOrDefaultAsync(cm => cm.ConversationId == id && cm.UserId == userId && cm.IsActive);
            if (member == null) return NotFound();

            member.IsActive = false;
            member.LeftAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            return NoContent();
        }

        // POST: /api/conversations/{id}/disband
        [HttpPost("{id}/disband")]
        public async Task<IActionResult> DisbandConversationAsync(int id)
        {
            var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdStr) || !int.TryParse(userIdStr, out var currentUserId)) 
                return Unauthorized();

            var conv = await _context.Conversations.FindAsync(id);
            if (conv == null) return NotFound();

            var isCreator = conv.CreatedBy == currentUserId;
            var isAdmin = await _context.ConversationMembers
                .AnyAsync(cm => cm.ConversationId == id && cm.UserId == currentUserId && cm.RoleInConversation == "Admin" && cm.IsActive);

            if (!isCreator && !isAdmin) return Forbid();

            var members = await _context.ConversationMembers
                .Where(cm => cm.ConversationId == id && cm.IsActive)
                .ToListAsync();

            foreach (var m in members)
            {
                m.IsActive = false;
                m.LeftAt = DateTime.UtcNow;
            }

            await _context.SaveChangesAsync();

            return NoContent();
        }

        [HttpPost("{id}/avatar")]
        public async Task<IActionResult> UploadAvatarAsync(int id, IFormFile file)
        {
            if (file == null || file.Length == 0) return BadRequest(new { error = "File empty" });
            
            var userIdStr = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdStr)) return Unauthorized();
            int currentUserId = int.Parse(userIdStr);

            var isMember = await _context.ConversationMembers
                .AnyAsync(cm => cm.ConversationId == id && cm.UserId == currentUserId && cm.IsActive);
            if (!isMember) return Forbid();

            var conv = await _context.Conversations.FindAsync(id);
            if (conv == null) return NotFound();

            var ext = System.IO.Path.GetExtension(file.FileName);
            var fileName = $"group_avatar_{id}_{Guid.NewGuid()}{ext}";
            var uploadsRoot = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "wwwroot", "uploads", "avatars");
            System.IO.Directory.CreateDirectory(uploadsRoot);
            var filePath = System.IO.Path.Combine(uploadsRoot, fileName);

            using (var stream = new System.IO.FileStream(filePath, System.IO.FileMode.Create)) { await file.CopyToAsync(stream); }

            var relativePath = $"/uploads/avatars/{fileName}";
            conv.AvatarPath = relativePath;
            await _context.SaveChangesAsync();
            await _hubContext.Clients.Group(id.ToString()).SendAsync("ReceiveGroupUpdate", id, conv.AvatarPath, conv.BackgroundPath);
            return Ok(new { avatarPath = relativePath });
        }

        [HttpPost("{id}/background")]
        public async Task<IActionResult> UploadBackgroundAsync(int id, IFormFile file)
        {
            if (file == null || file.Length == 0) return BadRequest(new { error = "File empty" });

            var userIdStr = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdStr)) return Unauthorized();
            int currentUserId = int.Parse(userIdStr);

            var isMember = await _context.ConversationMembers
                .AnyAsync(cm => cm.ConversationId == id && cm.UserId == currentUserId && cm.IsActive);
            if (!isMember) return Forbid();

            var conv = await _context.Conversations.FindAsync(id);
            if (conv == null) return NotFound();

            var ext = System.IO.Path.GetExtension(file.FileName);
            var fileName = $"group_bg_{id}_{Guid.NewGuid()}{ext}";
            var uploadsRoot = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "wwwroot", "uploads", "backgrounds");
            System.IO.Directory.CreateDirectory(uploadsRoot);
            var filePath = System.IO.Path.Combine(uploadsRoot, fileName);

            using (var stream = new System.IO.FileStream(filePath, System.IO.FileMode.Create)) { await file.CopyToAsync(stream); }

            var relativePath = $"/uploads/backgrounds/{fileName}";
            conv.BackgroundPath = relativePath;
            await _context.SaveChangesAsync();
            await _hubContext.Clients.Group(id.ToString()).SendAsync("ReceiveGroupUpdate", id, conv.AvatarPath, conv.BackgroundPath);
            return Ok(new { backgroundPath = relativePath });
        }
    }
}
