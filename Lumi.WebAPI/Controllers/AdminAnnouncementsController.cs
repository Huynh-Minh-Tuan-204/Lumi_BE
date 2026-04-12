using Lumi.Domain.Entities;
using Lumi.Application.DTOs;
using Lumi.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Lumi.WebAPI.Controllers
{
    [ApiController]
    [Route("api/admin/announcements")]
    [Authorize(Roles = "Admin,Manager")]
    public class AdminAnnouncementsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;

        public AdminAnnouncementsController(ApplicationDbContext context)
        {
            _context = context;
        }

        [HttpPost]
        public async Task<IActionResult> SendAnnouncementAsync([FromBody] CreateAnnouncementDto dto)
        {
            try 
            {
                var userIdStr = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdStr) || !int.TryParse(userIdStr, out var userId)) 
                    return Unauthorized();

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

                var msg = new Message
                {
                    ConversationId = systemConv.Id,
                    SenderId = userId,
                    EncryptedContent = dto.Message,
                    IV = "SYSTEM_MSG",
                    MessageType = "Announcement",
                    CreatedAt = DateTime.UtcNow
                };

                _context.Messages.Add(msg);
                await _context.SaveChangesAsync();

                return Ok(new { message = "Thông báo đã được gửi qua hệ thống tin nhắn." });
            }
            catch (Exception ex)
            {
                return StatusCode(500, ex.InnerException?.Message ?? ex.Message);
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetAnnouncementsAsync()
        {
            try 
            {
                var data = await _context.Messages
                    .Where(m => m.MessageType == "Announcement" && (m.IsDeleted == false))
                    .OrderByDescending(m => m.CreatedAt)
                    .Select(m => new
                    {
                        sender = "System",
                        message = m.EncryptedContent ?? "",
                        isSystem = true,
                        time = m.CreatedAt
                    })
                    .ToListAsync();

                return Ok(data);
            }
            catch (Exception ex)
            {
                return StatusCode(500, ex.InnerException?.Message ?? ex.Message);
            }
        }
    }
}