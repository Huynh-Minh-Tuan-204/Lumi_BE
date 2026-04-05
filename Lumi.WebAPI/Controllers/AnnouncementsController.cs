using Lumi.Application.DTOs;
using Lumi.Infrastructure.Data;
using Lumi.Domain.Entities;
using Lumi.Infrastructure.Hubs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

namespace Lumi.WebAPI.Controllers
{
    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    public class AnnouncementsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IHubContext<ChatHub> _hubContext;
        private readonly ILogger<AnnouncementsController> _logger;

        public AnnouncementsController(ApplicationDbContext context, IHubContext<ChatHub> hubContext, ILogger<AnnouncementsController> logger)
        {
            _context = context;
            _hubContext = hubContext;
            _logger = logger;
        }

        // GET: /api/announcements
        [HttpGet]
        public async Task<IActionResult> GetAnnouncementsAsync()
        {
            try 
            {
                var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdStr) || !int.TryParse(userIdStr, out var userId)) 
                    return Unauthorized();

                var itemsRaw = await _context.Messages
                    .Where(m => m.MessageType == "Announcement" && m.IsDeleted != true)
                    .Include(m => m.Sender)
                    .OrderByDescending(m => m.CreatedAt)
                    .ToListAsync();

                var userReadMessageIds = await _context.MessageReads
                    .Where(mr => mr.UserId == userId)
                    .Select(mr => mr.MessageId)
                    .ToListAsync();

                // Lọc các tin nhắn theo đối tượng nhận
                var filteredItems = itemsRaw
                    .Where(m => {
                        if (string.IsNullOrEmpty(m.IV) || m.IV == "SYSTEM_MSG") return true; 
                        var targetIds = m.IV.Split(',', StringSplitOptions.RemoveEmptyEntries);
                        return targetIds.Any(id => id == userId.ToString());
                    })
                    .Select(m => new
                    {
                        Id = m.Id,
                        SenderName = (m.Sender != null) ? (m.Sender.FullName ?? m.Sender.Username) : "System",
                        Message = m.EncryptedContent,
                        Timestamp = DateTime.SpecifyKind(m.CreatedAt, DateTimeKind.Utc).ToString("o"),
                        IsSystem = true,
                        IsRead = userReadMessageIds.Contains(m.Id)
                    })
                    .ToList();

                return Ok(filteredItems);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi xảy ra tại endpoint /api/Announcements: {Message}", ex.Message);
                return StatusCode(500, ex.InnerException?.Message ?? ex.Message);
            }
        }

        // POST: /api/announcements/read
        [HttpPost("read")]
        public async Task<IActionResult> MarkAllAsReadAsync()
        {
            try 
            {
                var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdStr) || !int.TryParse(userIdStr, out var userId)) 
                    return Unauthorized();

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

                var announcements = await _context.Messages
                    .Where(m => m.MessageType == "Announcement" && m.IsDeleted != true)
                    .ToListAsync();
                
                var targetMessageIds = announcements
                    .Where(m => string.IsNullOrEmpty(m.IV) || m.IV == "SYSTEM_MSG" || m.IV.Split(',').Contains(userId.ToString()))
                    .Select(m => m.Id)
                    .ToList();

                var readMessageIds = await _context.MessageReads
                    .Where(mr => mr.UserId == userId && targetMessageIds.Contains(mr.MessageId))
                    .Select(mr => mr.MessageId)
                    .ToListAsync();

                var unreadMessageIds = targetMessageIds.Except(readMessageIds).ToList();

                if (!unreadMessageIds.Any()) return NoContent();

                foreach(var msgId in unreadMessageIds)
                {
                    _context.MessageReads.Add(new MessageRead
                    {
                        MessageId = msgId,
                        UserId = userId,
                        DeviceId = deviceId,
                        ReadAt = DateTime.UtcNow
                    });
                }
                
                await _context.SaveChangesAsync();
                return Ok(new { message = "All announcements marked as read" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi xảy ra tại endpoint /api/Announcements/read: {Message}", ex.Message);
                return StatusCode(500, ex.InnerException?.Message ?? ex.Message);
            }
        }

        // POST: /api/announcements
        [HttpPost]
        public async Task<IActionResult> CreateAnnouncementAsync([FromBody] CreateAnnouncementDto request)
        {
            try 
            {
                if (request == null) return BadRequest("Dữ liệu không hợp lệ");

                var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdStr) || !int.TryParse(userIdStr, out var userId)) 
                    return Unauthorized("Không tìm thấy người dùng");

                var systemConv = await _context.Conversations.FirstOrDefaultAsync(c => c.Type == "System");
                if (systemConv == null)
                {
                    systemConv = new Conversation 
                    { 
                        Name = "Hệ thống thông báo", 
                        Type = "System", 
                        CreatedBy = userId,
                        CreatedAt = DateTime.UtcNow,
                        LastMessageAt = DateTime.UtcNow
                    };
                    _context.Conversations.Add(systemConv);
                    await _context.SaveChangesAsync();
                }

                // Lưu danh sách User nhận vào IV để lọc lịch sử
                string targetIdsStr = "SYSTEM_MSG";
                if (request.UserIds != null && request.UserIds.Any())
                {
                    targetIdsStr = string.Join(",", request.UserIds);
                }

                var msg = new Message
                {
                    ConversationId = systemConv.Id,
                    SenderId = userId,
                    EncryptedContent = request.Message,
                    IV = targetIdsStr,
                    MessageType = "Announcement",
                    CreatedAt = DateTime.UtcNow
                };

                _context.Messages.Add(msg);
                systemConv.LastMessageAt = msg.CreatedAt;
                systemConv.UpdatedAt = DateTime.UtcNow;

                await _context.SaveChangesAsync();

                var sender = await _context.Users.FindAsync(userId);
                var senderName = sender?.FullName ?? sender?.Username ?? "System";

                // Data for SignalR
                var payload = new {
                    id = msg.Id,
                    sender = senderName, 
                    message = request.Message, 
                    isSystem = true, 
                    createdAt = DateTime.SpecifyKind(msg.CreatedAt, DateTimeKind.Utc).ToString("o"), 
                    senderId = userId 
                };

                // Broadcast using SignalR
                if (request.UserIds != null && request.UserIds.Any())
                {
                    foreach (var targetId in request.UserIds)
                    {
                        await _hubContext.Clients.User(targetId.ToString()).SendAsync("ReceiveNotification", payload);
                    }
                }
                else
                {
                    await _hubContext.Clients.All.SendAsync("ReceiveNotification", payload);
                }

                return Ok(new { success = true, senderName, message = request.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Lỗi khi gửi thông báo");
                var innerMessage = ex.InnerException?.Message ?? "";
                return StatusCode(500, $"Lỗi hệ thống: {ex.Message}. Chi tiết DB: {innerMessage}");
            }
        }
    }
}
