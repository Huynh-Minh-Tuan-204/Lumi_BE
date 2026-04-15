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

                var conversation = new Conversation {
                    Name = meetingTitle,
                    Type = "GlobalMeeting",
                    AvatarPath = "",
                    BackgroundPath = "",
                    CreatedBy = userId,
                    CreatedAt = DateTime.UtcNow,
                    LastMessageAt = DateTime.UtcNow
                };
                _context.Conversations.Add(conversation);
                await _context.SaveChangesAsync();

                _context.ConversationMembers.Add(new ConversationMember {
                    ConversationId = conversation.Id,
                    UserId = userId,
                    RoleInConversation = "Admin",
                    IsActive = true,
                    JoinedAt = DateTime.UtcNow
                });

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

                _context.MeetingParticipants.Add(new MeetingParticipant {
                    MeetingId = meeting.Id,
                    UserId = userId,
                    JoinedAt = DateTime.UtcNow,
                    IsHost = true,
                    IsPresent = true
                });
                await _context.SaveChangesAsync();

                await _hubContext.Clients.All.SendAsync("GlobalMeetingStarted", new {
                    meetingId = meeting.MeetingGuid,
                    title = meeting.Title,
                    hostName = User.Identity?.Name ?? "Admin",
                    hostId = userId,
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "StartGlobalMeeting error");
                return StatusCode(500, new { error = "Lỗi hệ thống.", detail = ex.Message });
            }
        }

        [HttpPost("start/{conversationId}")]
        public async Task<IActionResult> StartMeeting(int conversationId, [FromBody] StartMeetingDto dto)
        {
            try
            {
                var userId = GetUserId();
                var conversation = await _context.Conversations.FindAsync(conversationId);
                if (conversation == null) return NotFound(new { error = "Không tìm thấy cuộc hội thoại." });
                
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
                    Title = string.IsNullOrWhiteSpace(dto?.Title) ? (conversation.Name ?? "Cuộc họp mới") : dto.Title,
                    CreatedBy = userId,
                    StartedAt = DateTime.UtcNow,
                    MeetingGuid = GenerateRoomCode(),
                    CallType = dto?.Type ?? "video",
                    SettingsJson = "{}"
                };

                _context.Meetings.Add(meeting);
                await _context.SaveChangesAsync();

                _context.MeetingParticipants.Add(new MeetingParticipant {
                    MeetingId = meeting.Id,
                    UserId = userId,
                    JoinedAt = DateTime.UtcNow,
                    IsHost = true,
                    IsPresent = true
                });
                await _context.SaveChangesAsync();

                return Ok(new { 
                    id = meeting.Id, 
                    meetingId = meeting.MeetingGuid, 
                    title = meeting.Title,
                    conversationId = conversationId,
                    isHost = true
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpPost("{idOrGuid}/join")]
        public async Task<IActionResult> JoinMeeting(string idOrGuid)
        {
            try
            {
                var userId = GetUserId();
                Meeting? meeting = null;
                if (int.TryParse(idOrGuid, out int id)) meeting = await _context.Meetings.FindAsync(id);
                else meeting = await _context.Meetings.FirstOrDefaultAsync(m => m.MeetingGuid == idOrGuid);

                if (meeting == null) return NotFound(new { error = "Cuộc họp không tồn tại." });
                if (meeting.EndedAt != null) return BadRequest(new { error = "Cuộc họp đã kết thúc." });

                var participant = await _context.MeetingParticipants
                    .FirstOrDefaultAsync(p => p.MeetingId == meeting.Id && p.UserId == userId);

                if (participant == null)
                {
                    _context.MeetingParticipants.Add(new MeetingParticipant {
                        MeetingId = meeting.Id,
                        UserId = userId,
                        JoinedAt = DateTime.UtcNow,
                        IsPresent = true,
                        IsHost = meeting.CreatedBy == userId
                    });
                } else {
                    participant.IsPresent = true;
                    participant.JoinedAt = DateTime.UtcNow;
                }

                await _context.SaveChangesAsync();
                
                await _hubContext.Clients.Group(meeting.ConversationId.ToString()).SendAsync("UserJoinedMeeting", new {
                    meetingId = meeting.MeetingGuid,
                    userId = userId
                });

                return Ok(new { message = "Joined" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpPost("{idOrGuid}/leave")]
        public async Task<IActionResult> LeaveMeeting(string idOrGuid)
        {
            try
            {
                var userId = GetUserId();
                Meeting? meeting = null;
                if (int.TryParse(idOrGuid, out int id)) meeting = await _context.Meetings.FindAsync(id);
                else meeting = await _context.Meetings.FirstOrDefaultAsync(m => m.MeetingGuid == idOrGuid);

                if (meeting == null) return NotFound();

                var participant = await _context.MeetingParticipants
                    .FirstOrDefaultAsync(p => p.MeetingId == meeting.Id && p.UserId == userId);

                if (participant != null)
                {
                    participant.IsPresent = false;
                    participant.LeftAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync();
                }

                await _hubContext.Clients.Group(meeting.ConversationId.ToString()).SendAsync("UserLeftMeeting", new {
                    meetingId = meeting.MeetingGuid,
                    userId = userId
                });

                return Ok();
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpPost("end/{idOrGuid}")]
        public async Task<IActionResult> EndMeeting(string idOrGuid)
        {
            try
            {
                var userId = GetUserId();
                Meeting? meeting = null;
                if (int.TryParse(idOrGuid, out int id)) meeting = await _context.Meetings.FindAsync(id);
                else meeting = await _context.Meetings.FirstOrDefaultAsync(m => m.MeetingGuid == idOrGuid);

                if (meeting == null) return NotFound();
                meeting.EndedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();

                await _hubContext.Clients.Group(meeting.ConversationId.ToString()).SendAsync("MeetingEnded", meeting.MeetingGuid);
                return Ok(new { message = "Ended" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpDelete("{idOrGuid}")]
        public async Task<IActionResult> DeleteMeeting(string idOrGuid)
        {
            try
            {
                Meeting? meeting = null;
                if (int.TryParse(idOrGuid, out int id)) meeting = await _context.Meetings.FindAsync(id);
                else meeting = await _context.Meetings.FirstOrDefaultAsync(m => m.MeetingGuid == idOrGuid);

                if (meeting == null) return NotFound();
                meeting.EndedAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();
                return Ok();
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpGet("{idOrGuid}/participants")]
        public async Task<IActionResult> GetParticipants(string idOrGuid)
        {
            Meeting? meeting = null;
            if (int.TryParse(idOrGuid, out int id)) meeting = await _context.Meetings.FindAsync(id);
            else meeting = await _context.Meetings.FirstOrDefaultAsync(m => m.MeetingGuid == idOrGuid);

            if (meeting == null) return NotFound();

            var participants = await _context.MeetingParticipants
                .Include(p => p.User)
                .Where(p => p.MeetingId == meeting.Id && p.IsPresent == true)
                .Select(p => new {
                    p.UserId,
                    fullName = p.User.FullName,
                    avatarPath = p.User.AvatarPath,
                    p.IsHost
                })
                .ToListAsync();

            return Ok(participants);
        }
    }

    public class StartMeetingDto
    {
        public string? Title { get; set; }
        public string? Type { get; set; }
    }
}