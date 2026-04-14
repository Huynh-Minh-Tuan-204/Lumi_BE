#nullable enable
using Lumi.Application.Interfaces;
using Lumi.Infrastructure.Data;
using Lumi.Infrastructure.Hubs;
using Lumi.Domain.Entities;
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
    public class MeetingsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IHubContext<ChatHub> _hubContext;
        private readonly ILogger<MeetingsController> _logger;

        public MeetingsController(ApplicationDbContext context, IHubContext<ChatHub> hubContext, ILogger<MeetingsController> logger)
        {
            _context = context;
            _hubContext = hubContext;
            _logger = logger;
        }

        private int GetUserId()
        {
            var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return int.TryParse(userIdStr, out int id) ? id : 0;
        }

        private string GenerateRoomCode()
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
            var random = new Random();
            return new string(Enumerable.Repeat(chars, 8)
                .Select(s => s[random.Next(s.Length)]).ToArray());
        }

        [HttpGet]
        public async Task<IActionResult> GetMyMeetings()
        {
            var userId = GetUserId();
            var meetings = await _context.Meetings
                .Include(m => m.Conversation)
                .Where(m => m.CreatedBy == userId || _context.MeetingParticipants.Any(mp => mp.MeetingId == m.Id && mp.UserId == userId))
                .OrderByDescending(m => m.StartedAt)
                .Select(m => new {
                    m.Id,
                    m.MeetingGuid,
                    m.Title,
                    m.StartedAt,
                    m.EndedAt,
                    m.ConversationId,
                    ConversationName = m.Conversation != null ? m.Conversation.Name : null,
                    m.CreatedBy
                })
                .ToListAsync();

            return Ok(meetings);
        }

        [HttpGet("{idOrCode}")]
        public async Task<IActionResult> GetMeeting(string idOrCode)
        {
            var userId = GetUserId();
            Meeting? meeting = null;

            if (int.TryParse(idOrCode, out int id)) {
                meeting = await _context.Meetings.Include(m => m.Conversation).FirstOrDefaultAsync(m => m.Id == id);
            } else {
                meeting = await _context.Meetings.Include(m => m.Conversation).FirstOrDefaultAsync(m => m.MeetingGuid.ToString() == idOrCode);
            }

            if (meeting == null) return NotFound(new { error = "Cuộc họp không tồn tại hoặc mã phòng không đúng." });

            return Ok(new {
                meeting.Id,
                meeting.MeetingGuid,
                meeting.Title,
                meeting.StartedAt,
                meeting.EndedAt,
                meeting.ConversationId,
                ConversationName = meeting.Conversation != null ? meeting.Conversation.Name : null,
                meeting.CreatedBy,
                IsHost = meeting.CreatedBy == userId 
            });
        }

        [HttpPost("start-global")]
        public async Task<IActionResult> StartGlobalMeeting([FromBody] StartMeetingDto dto)
        {
            try
            {
                var userId = GetUserId();
                if (userId <= 0) return Unauthorized();

                var meetingTitle = string.IsNullOrWhiteSpace(dto?.Title) ? "Cuộc họp nhanh" : dto.Title;

                // --- Check for existing active global meeting by this user ---
                var existingActive = await _context.Meetings
                    .Include(m => m.Conversation)
                    .FirstOrDefaultAsync(m => m.CreatedBy == userId && m.EndedAt == null && m.Conversation.Type == "GlobalMeeting");
                
                if (existingActive != null)
                {
                    return Ok(new { 
                        id = existingActive.Id, 
                        meetingId = existingActive.MeetingGuid, 
                        title = existingActive.Title,
                        conversationId = existingActive.ConversationId,
                        isHost = true
                    });
                }

                // 1. Create a temporary conversation for this global meeting
                var conversation = new Conversation {
                    Name = meetingTitle,
                    Type = "GlobalMeeting", // Changed from "Group" to distinguish it
                    AvatarPath = "",
                    BackgroundPath = "",
                    CreatedBy = userId,
                    CreatedAt = DateTime.UtcNow,
                    LastMessageAt = DateTime.UtcNow
                };
                _context.Conversations.Add(conversation);
                await _context.SaveChangesAsync();

                // 2. Add creator as first member
                _context.ConversationMembers.Add(new ConversationMember {
                    ConversationId = conversation.Id,
                    UserId = userId,
                    RoleInConversation = "Admin",
                    IsActive = true,
                    JoinedAt = DateTime.UtcNow
                });

                // 3. Create the meeting
                var meeting = new Meeting {
                    ConversationId = conversation.Id,
                    Title = meetingTitle,
                    CreatedBy = userId,
                    StartedAt = DateTime.UtcNow,
                    MeetingGuid = GenerateRoomCode(),
                    CallType = dto?.Type ?? "video",
                    SettingsJson = "{}"
                };
                _context.Meetings.Add(meeting);
                await _context.SaveChangesAsync();

                // 4. Register the host as participant
                var participant = new MeetingParticipant {
                    MeetingId = meeting.Id,
                    UserId = userId,
                    JoinedAt = DateTime.UtcNow,
                    IsHost = true,
                    IsPresent = true
                };
                _context.MeetingParticipants.Add(participant);
                await _context.SaveChangesAsync();

                // 2. Broadcast to ALL users that a new meeting started (Global Invitation)
                await _hubContext.Clients.All.SendAsync("GlobalMeetingStarted", new {
                    meetingId = meeting.MeetingGuid,
                    title = meeting.Title,
                    hostName = User.Identity?.Name ?? "Admin",
                    hostId = userId, // Added to filter on frontend
                    conversationId = conversation.Id,
                    type = meeting.CallType
                });

                return Ok(new { 
                    id = meeting.Id, 
                    meetingId = meeting.MeetingGuid, 
                    title = meeting.Title,
                    conversationId = conversation.Id,
                    isHost = true
                });
            }
            catch (DbUpdateException ex)
            {
                var innerMessage = ex.InnerException?.Message ?? ex.Message;
                _logger.LogError(ex, "Database error in StartGlobalMeeting: {Message}", innerMessage);
                return StatusCode(500, new { error = "Lỗi cơ sở dữ liệu (Database Error).", detail = innerMessage });
            }
            catch (Exception ex)
            {
                var detailedError = ex.InnerException != null ? $"{ex.Message} | Inner: {ex.InnerException.Message}" : ex.Message;
                _logger.LogError(ex, "System error in StartGlobalMeeting: {DetailedError}", detailedError);
                return StatusCode(500, new { 
                    error = "Lỗi hệ thống.", 
                    detail = detailedError,
                    stack = ex.StackTrace // Only for debugging, remove in production if needed
                });
            }
        }

        [HttpPost("start/{conversationId}")]
        public async Task<IActionResult> StartMeeting(int conversationId, [FromBody] StartMeetingDto dto)
        {
            try
            {
                var userId = GetUserId();
                var conversation = await _context.Conversations.FindAsync(conversationId);
                if (conversation == null) return NotFound(new { error = "Không tìm thấy cuộc hội thoại hoặc cuộc hội thoại đã bị xóa." });
                
                // Optimized check: return existing if it exists, or just handle it simply
                var activeMeeting = await _context.Meetings
                    .FirstOrDefaultAsync(m => m.ConversationId == conversationId && m.EndedAt == null);
                
                if (activeMeeting != null)
                {
                    return Ok(new { 
                        id = activeMeeting.Id, 
                        meetingId = activeMeeting.MeetingGuid, 
                        title = activeMeeting.Title,
                        conversationId = conversationId,
                        isHost = activeMeeting.CreatedBy == userId
                    });
                }

                var meeting = new Meeting
                {
                    ConversationId = conversationId,
                    Title = string.IsNullOrWhiteSpace(dto?.Title) ? (conversation?.Name ?? "Cuộc họp mới") : dto.Title,
                    CreatedBy = userId,
                    StartedAt = DateTime.UtcNow,
                    MeetingGuid = GenerateRoomCode(),
                    CallType = dto?.Type ?? "video",
                    SettingsJson = "{}"
                };

                _context.Meetings.Add(meeting);
                await _context.SaveChangesAsync();

                var participant = new MeetingParticipant {
                    MeetingId = meeting.Id,
                    UserId = userId,
                    JoinedAt = DateTime.UtcNow,
                    IsHost = true,
                    IsPresent = true
                };
                _context.MeetingParticipants.Add(participant);
                await _context.SaveChangesAsync();

                return Ok(new { 
                    id = meeting.Id, 
                    meetingId = meeting.MeetingGuid, 
                    title = meeting.Title,
                    conversationId = conversationId,
                    isHost = true
                });
            }
            catch (DbUpdateException ex)
            {
                var innerMessage = ex.InnerException?.Message ?? ex.Message;
                _logger.LogError(ex, "Database error in StartMeeting: {Message}", innerMessage);
                return StatusCode(500, new { error = "Lỗi cơ sở dữ liệu (Database Error).", detail = innerMessage });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "System error in StartMeeting: {Message}", ex.Message);
                return StatusCode(500, new { error = "Lỗi hệ thống không xác định.", detail = ex.Message + " | " + ex.InnerException?.Message });
            }
        }

        [HttpPost("end/{idOrGuid}")]
        public async Task<IActionResult> EndMeeting(string idOrGuid)
        {
            try
            {
                var userId = GetUserId();
                Meeting? meeting = null;

                if (int.TryParse(idOrGuid, out int id)) {
                    meeting = await _context.Meetings.FindAsync(id);
                } else {
                    // Try parsing or compare via string
                meeting = await _context.Meetings.FirstOrDefaultAsync(m => m.MeetingGuid.ToString() == idOrGuid);
                }

                if (meeting == null) return NotFound(new { error = "Cuộc họp không tồn tại." });
                if (meeting.EndedAt != null) return BadRequest(new { error = "Cuộc họp này đã kết thúc trước đó." });

                meeting.EndedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();

                // If this was a global meeting, deactivate the temporary conversation for the creator
                // so it doesn't clutter their dashboard/room count
                var conversation = await _context.Conversations
                    .Include(c => c.Members)
                    .FirstOrDefaultAsync(c => c.Id == meeting.ConversationId);
                
                if (conversation != null && conversation.Type == "GlobalMeeting")
                {
                    foreach (var member in conversation.Members)
                    {
                        member.IsActive = false;
                        member.LeftAt = DateTime.UtcNow;
                    }
                    await _context.SaveChangesAsync();
                }

                // Notify via SignalR
                await _hubContext.Clients.Group(meeting.ConversationId.ToString()).SendAsync("MeetingEnded", meeting.MeetingGuid);

                return Ok(new { message = "Kết thúc cuộc họp thành công.", meetingId = meeting.MeetingGuid });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error ending meeting: {Message}", ex.Message);
                return StatusCode(500, new { error = "Lỗi khi kết thúc cuộc họp.", detail = ex.Message });
            }
        }

        [HttpDelete("{idOrGuid}")]
        public async Task<IActionResult> DeleteMeeting(string idOrGuid)
        {
            try
            {
                var userId = GetUserId();
                Meeting? meeting = null;

                if (int.TryParse(idOrGuid, out int id))
                {
                    // Try by meeting ID first
                    meeting = await _context.Meetings
                        .Include(m => m.Conversation)
                        .FirstOrDefaultAsync(m => m.Id == id);

                    // If not found by meeting ID, try by conversation ID (for GlobalMeeting rooms)
                    if (meeting == null)
                    {
                        meeting = await _context.Meetings
                            .Include(m => m.Conversation)
                            .Where(m => m.ConversationId == id && m.EndedAt == null)
                            .OrderByDescending(m => m.StartedAt)
                            .FirstOrDefaultAsync();
                    }
                }
                else
                {
                    meeting = await _context.Meetings
                        .Include(m => m.Conversation)
                        .FirstOrDefaultAsync(m => m.MeetingGuid == idOrGuid);
                }

                if (meeting == null) return NotFound(new { error = "Cuộc họp không tồn tại." });

                // End the meeting
                meeting.EndedAt = DateTime.UtcNow;
                
                // Deactivate conversation if it's a global meeting — hide it from sidebar
                if (meeting.Conversation != null && 
                    (meeting.Conversation.Type == "GlobalMeeting" || 
                     meeting.Conversation.Type == "ClosedMeeting" ||
                     meeting.Conversation.Name == "Cuộc họp nhanh"))
                {
                    var members = await _context.ConversationMembers
                        .Where(cm => cm.ConversationId == meeting.Conversation.Id)
                        .ToListAsync();
                    foreach (var member in members) member.IsActive = false;
                    meeting.Conversation.Type = "ClosedMeeting";
                }

                await _context.SaveChangesAsync();
                
                // Notify via SignalR that meeting ended
                await _hubContext.Clients.Group(meeting.ConversationId.ToString())
                    .SendAsync("MeetingEnded", meeting.MeetingGuid);

                return Ok(new { message = "Đã xóa cuộc họp thành công." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting meeting: {Message}", ex.Message);
                return StatusCode(500, new { error = "Lỗi khi xóa cuộc họp.", detail = ex.Message });
            }
        }
    }

    public class StartMeetingDto
    {
        public string? Title { get; set; }
        public string? Type { get; set; }
    }
}