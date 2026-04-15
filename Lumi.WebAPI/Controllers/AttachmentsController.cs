using Lumi.Application.DTOs;
using Lumi.Infrastructure.Data;
using Lumi.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using System;
using Microsoft.AspNetCore.SignalR;
using Lumi.Infrastructure.Hubs;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

namespace Lumi.WebAPI.Controllers
{
    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    public class AttachmentsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IWebHostEnvironment _env;
        private readonly IHubContext<ChatHub> _hubContext;

        public AttachmentsController(ApplicationDbContext context, IWebHostEnvironment env, IHubContext<ChatHub> hubContext)
        {
            _context = context;
            _env = env;
            _hubContext = hubContext;
        }

        [HttpPost("upload")]
        public async Task<IActionResult> Upload([FromForm] IFormFile file, [FromForm] int? messageId, [FromForm] int? conversationId)
        {
            if (file == null || file.Length == 0) return BadRequest(new { error = "File is required." });

            var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdStr)) return Unauthorized();
            int currentUserId = int.Parse(userIdStr);

            int targetMessageId = 0;
            if (messageId.HasValue) {
                var msg = await _context.Messages.FindAsync(messageId.Value);
                if (msg == null) return NotFound(new { error = "Message not found." });
                targetMessageId = msg.Id;
            } else {
                if (!conversationId.HasValue) return BadRequest(new { error = "Either messageId or conversationId is required." });
                
                var msg = new Message {
                    ConversationId = conversationId.Value,
                    SenderId = currentUserId,
                    EncryptedContent = "[Attachment]",
                    IV = string.Empty,
                    Signature = string.Empty,
                    MessageType = "Attachment",
                    CreatedAt = DateTime.UtcNow
                };
                _context.Messages.Add(msg);
                await _context.SaveChangesAsync();
                targetMessageId = msg.Id;
            }

            var year = DateTime.UtcNow.Year;
            var month = DateTime.UtcNow.Month;
            var uploadsRoot = Path.Combine(_env.WebRootPath ?? "wwwroot", "uploads", year.ToString(), month.ToString("D2"));
            Directory.CreateDirectory(uploadsRoot);

            var ext = Path.GetExtension(file.FileName);
            var fileName = Guid.NewGuid().ToString() + ext;
            var filePath = Path.Combine(uploadsRoot, fileName);

            await using (var stream = new FileStream(filePath, FileMode.Create)) {
                await file.CopyToAsync(stream);
            }

            var relativePath = Path.Combine("uploads", year.ToString(), month.ToString("D2"), fileName).Replace("\\", "/");

            var attachment = new Attachment {
                MessageId = targetMessageId,
                FileName = file.FileName,
                EncryptedFilePath = relativePath,
                FileSize = file.Length,
                MimeType = file.ContentType,
                UploadedBy = currentUserId,
                UploadedAt = DateTime.UtcNow
            };

            _context.Attachments.Add(attachment);
            await _context.SaveChangesAsync();

            var sender = await _context.Users.FindAsync(currentUserId);
            var msgObj = await _context.Messages.FindAsync(targetMessageId);

            // BROADCAST AS SINGLE OBJECT (Matches SignalR hook expectations)
            if (msgObj != null) {
                await _hubContext.Clients.Group(msgObj.ConversationId.ToString())
                    .SendAsync("ReceiveMessage", new {
                        id = msgObj.Id,
                        conversationId = msgObj.ConversationId,
                        senderId = currentUserId,
                        senderName = sender?.FullName ?? sender?.Username ?? "Unknown",
                        content = msgObj.EncryptedContent,
                        iv = msgObj.IV ?? "",
                        messageType = msgObj.MessageType ?? "Attachment",
                        createdAt = DateTime.SpecifyKind(msgObj.CreatedAt, DateTimeKind.Utc).ToString("o"),
                        attachments = new[] { new { 
                            id = attachment.Id, 
                            fileName = attachment.FileName, 
                            fileSize = attachment.FileSize, 
                            mimeType = attachment.MimeType,
                            filePath = attachment.EncryptedFilePath
                        } }
                    });
            }

            return Ok(new {
                attachment.Id,
                attachment.FileName,
                attachment.EncryptedFilePath,
                attachment.FileSize,
                attachment.MimeType,
                attachment.UploadedBy,
                attachment.UploadedAt
            });
        }

        [HttpGet("{id}/download")]
        public async Task<IActionResult> Download(int id)
        {
            var attachment = await _context.Attachments.FindAsync(id);
            if (attachment == null) return NotFound();

            var relative = attachment.EncryptedFilePath.TrimStart('/', '\\');
            var physPath = Path.Combine(_env.WebRootPath ?? "wwwroot", relative.Replace('/', Path.DirectorySeparatorChar));

            if (!System.IO.File.Exists(physPath)) return NotFound();

            var fileBytes = await System.IO.File.ReadAllBytesAsync(physPath);
            return File(fileBytes, attachment.MimeType, attachment.FileName);
        }
    }
}
