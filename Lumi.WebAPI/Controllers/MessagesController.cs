using Lumi.Application.DTOs;
using Lumi.Infrastructure.Data;
using Lumi.Domain.Entities;
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

        private int GetUserId()
        {
            var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return int.TryParse(userIdStr, out var id) ? id : 0;
        }

        [HttpPost]
        public async Task<IActionResult> PostMessageAsync([FromBody] CreateMessageDto request)
        {
            var userId = GetUserId();
            if (userId == 0) return Unauthorized();

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
                        attachments = new List<object>() 
                    });

                return Ok(new { msg.Id, msg.ConversationId, msg.SenderId, CreatedAt = DateTime.SpecifyKind(msg.CreatedAt, DateTimeKind.Utc).ToString("o") });
            }
            catch (Exception ex)
            {
                return StatusCode(500, ex.Message);
            }
        }

        [HttpGet("/api/conversations/{conversationId}/messages")]
        public async Task<IActionResult> GetMessagesForConversationAsync(int conversationId)
        {
            var userId = GetUserId();
            if (userId == 0) return Unauthorized();

            var isMember = await _context.ConversationMembers
                .AnyAsync(cm => cm.ConversationId == conversationId && cm.UserId == userId && cm.IsActive);
            if (!isMember) return Forbid();

            try
            {
                var messagesData = await _context.Messages.AsNoTracking()
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
                        m.IsPinned,
                        m.StickerUrl,
                        m.Metadata,
                        ReadBy = m.MessageReads.Select(mr => mr.UserId).Distinct().ToList(),
                        Attachments = _context.Attachments
                            .Where(a => a.MessageId == m.Id)
                            .Select(a => new { 
                                id = a.Id, 
                                fileName = a.FileName, 
                                fileSize = a.FileSize, 
                                mimeType = a.MimeType,
                                filePath = a.EncryptedFilePath 
                            })
                            .ToList()
                    })
                    .ToListAsync();

                var messages = messagesData
                    .Where(m => {
                        if (string.IsNullOrEmpty(m.Metadata)) return true;
                        try {
                            if (m.Metadata.Contains("\"hiddenFor\":")) {
                                var meta = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonObject>(m.Metadata);
                                if (meta != null && meta.TryGetPropertyValue("hiddenFor", out var node) && node != null) {
                                    var list = node.AsArray().Select(v => (int)v!).ToList();
                                    if (list.Contains(userId)) return false;
                                }
                            }
                        } catch { }
                        return true;
                    })
                    .Select(m => new
                    {
                        m.Id,
                        m.ConversationId,
                        m.SenderId,
                        senderName = m.SenderName,
                        encryptedContent = m.EncryptedContent,
                        m.IV,
                        messageType = m.MessageType,
                        m.ParentMessageId,
                        createdAt = DateTime.SpecifyKind(m.CreatedAt, DateTimeKind.Utc).ToString("o"),
                        isDeleted = false,
                        isRead = false,
                        isPinned = m.IsPinned == true,
                        isSystem = false,
                        stickerUrl = m.StickerUrl,
                        readBy = m.ReadBy,
                        attachments = m.Attachments // Already lowercased in projection
                    });

                return Ok(messages);
            }
            catch (Exception ex)
            {
                return StatusCode(500, ex.Message);
            }
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteMessageAsync(int id)
        {
            var userIdCode = GetUserId();
            if (userIdCode == 0) return Unauthorized();

            var msg = await _context.Messages.FindAsync(id);
            if (msg == null) return NotFound();

            msg.IsDeleted = true;
            msg.DeletedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();
            await _hubContext.Clients.Group(msg.ConversationId.ToString())
                .SendAsync("MessageDeleted", msg.ConversationId, msg.Id);

            return NoContent();
        }
    }
}
