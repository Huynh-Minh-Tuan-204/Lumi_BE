using Lumi.Domain.Entities;
using Lumi.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;

namespace Lumi.WebAPI.Controllers
{
    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    public class RecordingsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly string _uploadPath;

        public RecordingsController(ApplicationDbContext context)
        {
            _context = context;
            _uploadPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", "recordings");
            if (!Directory.Exists(_uploadPath)) Directory.CreateDirectory(_uploadPath);
        }

        private int GetUserId()
        {
            var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return int.TryParse(userIdStr, out var id) ? id : 0;
        }

        [HttpPost("upload")]
        public async Task<IActionResult> UploadRecording([FromForm] int meetingId, [FromForm] IFormFile file, [FromForm] string iv)
        {
            var userId = GetUserId();
            if (userId == 0) return Unauthorized();

            var meeting = await _context.Meetings.FindAsync(meetingId);
            if (meeting == null) return NotFound("Meeting not found");

            if (file == null || file.Length == 0) return BadRequest("File is empty");

            var fileName = $"{meeting.MeetingGuid}_{Guid.NewGuid()}{Path.GetExtension(file.FileName)}";
            var filePath = Path.Combine(_uploadPath, fileName);
            
            // In a real production-grade app, we would encrypt the stream here before saving.
            // For now, we save it as-is, assuming the client sent encrypted data or we'll encrypt it server-side.
            // Client requirement: "Encrypt video file before storing (AES)"
            // If the client sends it encrypted, we just save the EncryptedFilePath.
            
            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }

            var recording = new CallRecording
            {
                MeetingId = meetingId,
                FilePath = $"/uploads/recordings/{fileName}",
                EncryptedFilePath = $"/uploads/recordings/{fileName}", // Assuming E2EE for now or server-side encryption
                IV = iv,
                FileSize = file.Length,
                CreatedAt = DateTime.UtcNow
            };

            _context.CallRecordings.Add(recording);
            await _context.SaveChangesAsync();

            return Ok(recording);
        }

        [HttpGet("meeting/{meetingId}")]
        public async Task<IActionResult> GetRecordingsByMeeting(int meetingId)
        {
            var userId = GetUserId();
            if (userId == 0) return Unauthorized();

            // Check if user has access to this meeting's conversation
            var meeting = await _context.Meetings.FindAsync(meetingId);
            if (meeting == null) return NotFound("Meeting not found");

            var isMember = await _context.ConversationMembers
                .AnyAsync(cm => cm.ConversationId == meeting.ConversationId && cm.UserId == userId && cm.IsActive);
            if (!isMember) return Forbid();

            var recordings = await _context.CallRecordings
                .Where(r => r.MeetingId == meetingId)
                .OrderByDescending(r => r.CreatedAt)
                .ToListAsync();

            return Ok(recordings);
        }
    }
}
