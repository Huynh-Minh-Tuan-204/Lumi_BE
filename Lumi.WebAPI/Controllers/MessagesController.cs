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
    public class MessagesController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IHubContext<ChatHub> _hubContext;

        public MessagesController(ApplicationDbContext context, IHubContext<ChatHub> hubContext)
        {
            _context = context;
            _hubContext = hubContext;
        }

        // POST: /api/messages
        [HttpPost]
        public async Task<IActionResult> PostMessageAsync([FromBody] CreateMessageDto request)
        {
            var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdStr)) return Unauthorized();
            if (!int.TryParse(userIdStr, out var userId)) return Unauthorized();

            var conversation = await _context.Conversations.FindAsync(request.ConversationId);
            if (conversation == null) return NotFound(new { error = "Conversation not found." });

            var isMember = await _context.ConversationMembers
                .AnyAsync(cm => cm.ConversationId == request.ConversationId && cm.UserId == userId && cm.IsActive);
            if (!isMember) return Forbid();

            try
            {
                var msg = new Message
                {
                    ConversationId = request.ConversationId,
                    SenderId = userId,
                    EncryptedContent = request.EncryptedContent,
                    IV = request.IV,
                    MessageType = request.MessageType ?? "Text",
                    ParentMessageId = request.ParentMessageId,
                    CreatedAt = DateTime.UtcNow
                };

                _context.Messages.Add(msg);

                conversation.LastMessageAt = msg.CreatedAt;
                conversation.UpdatedAt = DateTime.UtcNow;

                await _context.SaveChangesAsync();

                var sender = await _context.Users.FindAsync(userId);
                var senderName = sender?.FullName ?? sender?.Username ?? "Unknown";

                await _hubContext.Clients.Group(request.ConversationId.ToString())
                    .SendAsync("ReceiveMessage", new {
                        id = msg.Id, 
                        conversationId = request.ConversationId, 
                        senderId = userId,
                        senderName = senderName, 
                        content = msg.EncryptedContent, 
                        iv = msg.IV, 
                        messageType = msg.MessageType, 
                        createdAt = DateTime.SpecifyKind(msg.CreatedAt, DateTimeKind.Utc).ToString("o"), 
                        attachments = new string[0] 
                    });

                return Ok(new { msg.Id, msg.ConversationId, msg.SenderId, CreatedAt = DateTime.SpecifyKind(msg.CreatedAt, DateTimeKind.Utc).ToString("o") });
            }
            catch (Exception ex)
            {
                return StatusCode(500, ex.InnerException?.Message ?? ex.Message);
            }
        }

        // GET: /api/conversations/{conversationId}/messages
        [HttpGet("/api/conversations/{conversationId}/messages")]
        public async Task<IActionResult> GetMessagesForConversationAsync(int conversationId)
        {
            var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdStr)) return Unauthorized();
            if (!int.TryParse(userIdStr, out var userId)) return Unauthorized();

            var isMember = await _context.ConversationMembers
                .AnyAsync(cm => cm.ConversationId == conversationId && cm.UserId == userId && cm.IsActive);
            if (!isMember) return Forbid();

            try
            {
                var messagesRaw = await _context.Messages.AsNoTracking()
                    .Include(m => m.Sender)
                    .Where(m => m.ConversationId == conversationId && m.IsDeleted != true)
                    .OrderBy(m => m.CreatedAt)
                    .Select(m => new
                    {
                        m.Id,
                        m.ConversationId,
                        m.SenderId,
                        SenderName = m.Sender.FullName ?? m.Sender.Username,
                        m.EncryptedContent,
                        m.IV,
                        m.MessageType,
                        m.ParentMessageId,
                        m.CreatedAt,
                        m.EditedAt,
                        m.DeletedAt,
                        IsDeleted = m.IsDeleted == true,
                        IsRead = m.IsRead == true,
                        IsPinned = m.IsPinned == true,
                        m.StickerUrl,
                        ReadBy = m.MessageReads.Select(mr => mr.UserId).Distinct().ToList(),
                        Attachments = _context.Attachments
                            .Where(a => a.MessageId == m.Id)
                            .Select(a => new { a.Id, a.FileName, a.EncryptedFilePath, a.FileSize, a.MimeType })
                            .ToList()
                    })
                    .ToListAsync();

                var messages = messagesRaw.Select(m => new
                {
                    m.Id,
                    m.ConversationId,
                    m.SenderId,
                    sender = m.SenderName,
                    message = m.EncryptedContent,
                    m.IV,
                    m.MessageType,
                    m.ParentMessageId,
                    time = DateTime.SpecifyKind(m.CreatedAt, DateTimeKind.Utc).ToString("o"),
                    EditedAt = m.EditedAt != null ? DateTime.SpecifyKind(m.EditedAt.GetValueOrDefault(), DateTimeKind.Utc).ToString("o") : null,
                    DeletedAt = m.DeletedAt != null ? DateTime.SpecifyKind(m.DeletedAt.GetValueOrDefault(), DateTimeKind.Utc).ToString("o") : null,
                    isDeleted = m.IsDeleted,
                    isRead = m.IsRead,
                    isPinned = m.IsPinned,
                    isSystem = false,
                    stickerUrl = m.StickerUrl,
                    readBy = m.ReadBy,
                    attachments = m.Attachments
                });

                return Ok(messages);
            }
            catch (Exception ex)
            {
                return StatusCode(500, ex.InnerException?.Message ?? ex.Message);
            }
        }

        // DELETE: /api/messages/{id}
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteMessageAsync(int id)
        {
            var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdStr)) return Unauthorized();
            if (!int.TryParse(userIdStr, out var userId)) return Unauthorized();

            var msg = await _context.Messages.FindAsync(id);
            if (msg == null) return NotFound();

            // Only sender or conversation creator/admin can delete
            var conversation = await _context.Conversations.FindAsync(msg.ConversationId);
            var isCreator = conversation != null && conversation.CreatedBy == userId;
            var isAdmin = await _context.ConversationMembers
                .AnyAsync(cm => cm.ConversationId == msg.ConversationId && cm.UserId == userId && cm.RoleInConversation == "Admin" && cm.IsActive);

            if (msg.SenderId != userId && !isCreator && !isAdmin) return Forbid();

            msg.IsDeleted = true;
            msg.DeletedAt = DateTime.UtcNow;

            // If this was the last message, update conversation.LastMessageAt to previous message or CreatedAt
            if (conversation != null && conversation.LastMessageAt <= msg.CreatedAt)
            {
                var last = await _context.Messages
                    .Where(m => m.ConversationId == msg.ConversationId && m.IsDeleted != true && m.Id != msg.Id)
                    .OrderByDescending(m => m.CreatedAt)
                    .Select(m => m.CreatedAt)
                    .FirstOrDefaultAsync();

                conversation.LastMessageAt = last == default ? conversation.CreatedAt : last;
                conversation.UpdatedAt = DateTime.UtcNow;
            }

            await _context.SaveChangesAsync();

            await _hubContext.Clients.Group(msg.ConversationId.ToString())
                .SendAsync("MessageDeleted", msg.ConversationId, msg.Id);

            return NoContent();
        }

        // POST: /api/messages/{id}/read
        [HttpPost("{id}/read")]
        public async Task<IActionResult> MarkMessageReadAsync(int id, [FromBody] MarkMessageReadDto request)
        {
            var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdStr) || !int.TryParse(userIdStr, out var currentUserId)) 
                return Unauthorized();

            var message = await _context.Messages.FindAsync(id);
            if (message == null) return NotFound();

            // Avoid duplicate
            var exists = await _context.MessageReads
                .AnyAsync(mr => mr.MessageId == id && mr.UserId == currentUserId && mr.DeviceId == request.DeviceId);
            if (exists) return NoContent();

            var mr = new Domain.Entities.MessageRead
            {
                MessageId = id,
                UserId = currentUserId,
                DeviceId = request.DeviceId,
                ReadAt = DateTime.UtcNow
            };

            _context.MessageReads.Add(mr);
            await _context.SaveChangesAsync();

            return Ok(new { mr.Id, mr.MessageId, mr.UserId, mr.DeviceId, mr.ReadAt });
        }
    }
}
