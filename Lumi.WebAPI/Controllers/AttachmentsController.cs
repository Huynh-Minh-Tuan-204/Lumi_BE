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

        // POST: /api/attachments/upload
        [HttpPost("upload")]
        public async Task<IActionResult> Upload([FromForm] IFormFile file, [FromForm] int? messageId, [FromForm] int? conversationId)
        {
            if (file == null || file.Length == 0) return BadRequest(new { error = "File is required." });

            var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdStr)) return Unauthorized();

            int currentUserId = int.Parse(userIdStr);

            int targetMessageId = 0;

            if (messageId.HasValue)
            {
                var msg = await _context.Messages.FindAsync(messageId.Value);
                if (msg == null) return NotFound(new { error = "Message not found." });
                targetMessageId = msg.Id;
            }
            else
            {
                if (!conversationId.HasValue) return BadRequest(new { error = "Either messageId or conversationId is required." });

                // Check membership
                var isMember = await _context.ConversationMembers
                    .AnyAsync(cm => cm.ConversationId == conversationId.Value && cm.UserId == currentUserId && cm.IsActive);
                if (!isMember) return Forbid();

                // create placeholder message to attach file to
                var msg = new Message
                {
                    ConversationId = conversationId.Value,
                    SenderId = currentUserId,
                    EncryptedContent = "[Attachment]",
                    IV = string.Empty,
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

            await using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }

            var relativePath = Path.Combine("uploads", year.ToString(), month.ToString("D2"), fileName).Replace("\\", "/");

            var attachment = new Attachment
            {
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

            var dto = new AttachmentUploadResponseDto
            {
                Id = attachment.Id,
                FileName = attachment.FileName,
                EncryptedFilePath = attachment.EncryptedFilePath,
                FileSize = attachment.FileSize,
                MimeType = attachment.MimeType,
                UploadedBy = attachment.UploadedBy,
                UploadedAt = attachment.UploadedAt
            };

            // Broadcast message if it has a conversation ID
            var msgObj = await _context.Messages.Include(m => m.Sender).FirstOrDefaultAsync(m => m.Id == targetMessageId);
            if (msgObj != null && msgObj.ConversationId > 0)
            {
                var senderName = msgObj.Sender?.FullName ?? msgObj.Sender?.Username ?? "Unknown";
                var jsonOptions = new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };
                var attachmentsJson = System.Text.Json.JsonSerializer.Serialize(new[] { dto }, jsonOptions);
                await _hubContext.Clients.Group(msgObj.ConversationId.ToString())
                    .SendAsync("ReceiveMessage", 
                        msgObj.Id,
                        msgObj.ConversationId, 
                        senderName, 
                        msgObj.EncryptedContent, 
                        msgObj.IV ?? "", 
                        msgObj.MessageType ?? "Attachment",
                        msgObj.CreatedAt,
                        attachmentsJson);
            }

            return Ok(dto);
        }

        // GET: /api/attachments/{id}
        [HttpGet("{id}")]
        public async Task<IActionResult> Get(int id)
        {
            var attachment = await _context.Attachments
                .Include(a => a.Uploader)
                .FirstOrDefaultAsync(a => a.Id == id);
            if (attachment == null) return NotFound();

            return Ok(new
            {
                attachment.Id,
                attachment.FileName,
                attachment.EncryptedFilePath,
                attachment.FileSize,
                attachment.MimeType,
                attachment.UploadedBy,
                UploadedByName = attachment.Uploader != null ? (attachment.Uploader.FullName ?? attachment.Uploader.Username) : null,
                attachment.UploadedAt
            });
        }

        // DELETE: /api/attachments/{id}
        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(int id)
        {
            var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdStr)) return Unauthorized();

            int currentUserId = int.Parse(userIdStr);

            var attachment = await _context.Attachments.FindAsync(id);
            if (attachment == null) return NotFound();

            // Only uploader or admin can delete
            if (attachment.UploadedBy != currentUserId && !User.IsInRole("Admin"))
            {
                return Forbid();
            }

            // Remove file from disk if exists
            try
            {
                if (!string.IsNullOrEmpty(attachment.EncryptedFilePath))
                {
                    var physPath = Path.Combine(_env.WebRootPath ?? "wwwroot", attachment.EncryptedFilePath.Replace("/", Path.DirectorySeparatorChar.ToString()));
                    if (System.IO.File.Exists(physPath)) System.IO.File.Delete(physPath);
                }
            }
            catch { }

            // Soft-delete by clearing path and metadata (do not hard-delete record to preserve keys)
            attachment.EncryptedFilePath = null;
            attachment.FileName = null;
            attachment.FileSize = 0;
            attachment.MimeType = null;

            await _context.SaveChangesAsync();

            return NoContent();
        }

        // GET: /api/attachments/{id}/download
        [HttpGet("{id}/download")]
        public async Task<IActionResult> Download(int id)
        {
            var attachment = await _context.Attachments.FindAsync(id);
            if (attachment == null) return NotFound(new { error = "Không tìm thấy đính kèm" });

            if (string.IsNullOrEmpty(attachment.EncryptedFilePath)) return NotFound(new { error = "Đường dẫn trống" });

            var relative = attachment.EncryptedFilePath.TrimStart('/', '\\');
            var physPath = Path.Combine(_env.WebRootPath ?? "wwwroot", relative.Replace('/', Path.DirectorySeparatorChar));

            if (!System.IO.File.Exists(physPath)) 
            {
               // Thử fallback ContentRoot (nếu có trường hợp khác)
               physPath = Path.Combine(_env.ContentRootPath, relative.Replace('/', Path.DirectorySeparatorChar));
               if (!System.IO.File.Exists(physPath))
                  return NotFound(new { error = "Tệp tin không tồn tại trên hệ thống", path = attachment.EncryptedFilePath });
            }

            var fileBytes = await System.IO.File.ReadAllBytesAsync(physPath);
            var mime = string.IsNullOrEmpty(attachment.MimeType) ? "application/octet-stream" : attachment.MimeType;
            var fileName = string.IsNullOrEmpty(attachment.FileName) ? Path.GetFileName(physPath) : attachment.FileName;

            if (mime.StartsWith("image/"))
            {
                return File(fileBytes, mime);
            }

            return File(fileBytes, mime, fileName);
        }
    }
}
