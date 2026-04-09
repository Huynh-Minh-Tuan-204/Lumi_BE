using Lumi.Infrastructure.Data;
using Lumi.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.AspNetCore.SignalR;
using Lumi.Infrastructure.Hubs;

namespace Lumi.WebAPI.Controllers
{
    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    public class WorkSchedulesController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IHubContext<ChatHub> _hubContext;

        public WorkSchedulesController(ApplicationDbContext context, IHubContext<ChatHub> hubContext)
        {
            _context = context;
            _hubContext = hubContext;
        }

        private int GetUserId() => int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

        public class CreateScheduleDto
        {
            public string Title { get; set; }
            public string Description { get; set; }
            public DateTime StartTime { get; set; }
            public DateTime EndTime { get; set; }
            public string Location { get; set; }
            public List<int> ParticipantIds { get; set; }
        }

        [HttpGet]
        public async Task<IActionResult> GetMySchedules()
        {
            var userId = GetUserId();

            var schedules = await _context.WorkSchedules
                .Where(s => s.CreatedBy == userId || s.Participants.Any(p => p.UserId == userId))
                .Select(s => new
                {
                    s.Id,
                    s.Title,
                    s.Description,
                    s.StartTime,
                    s.EndTime,
                    s.Location,
                    UserRole = s.CreatedBy == userId ? "Creator" : "Participant",
                    Participants = s.Participants.Select(p => new
                    {
                        p.UserId,
                        p.User.FullName,
                        p.User.AvatarPath,
                        p.Status
                    })
                })
                .OrderBy(s => s.StartTime)
                .ToListAsync();

            return Ok(schedules);
        }

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] CreateScheduleDto dto)
        {
            var userId = GetUserId();

            var schedule = new WorkSchedule
            {
                Title = dto.Title,
                Description = dto.Description,
                StartTime = dto.StartTime,
                EndTime = dto.EndTime,
                Location = dto.Location,
                CreatedBy = userId,
                CreatedAt = DateTime.UtcNow
            };

            _context.WorkSchedules.Add(schedule);
            await _context.SaveChangesAsync();

            if (dto.ParticipantIds != null && dto.ParticipantIds.Any())
            {
                foreach (var pId in dto.ParticipantIds)
                {
                    _context.WorkScheduleParticipants.Add(new WorkScheduleParticipant
                    {
                        WorkScheduleId = schedule.Id,
                        UserId = pId,
                        Status = "Pending",
                        InvitedAt = DateTime.UtcNow
                    });
                }
                await _context.SaveChangesAsync();
                
                // Notify participants via SignalR
                var creator = await _context.Users.FindAsync(userId);
                foreach (var pId in dto.ParticipantIds)
                {
                    await _hubContext.Clients.User(pId.ToString()).SendAsync("ScheduleCreated", new {
                        id = schedule.Id,
                        title = schedule.Title,
                        startTime = schedule.StartTime,
                        createdBy = creator?.FullName ?? "Someone"
                    });
                }
            }

            return Ok(new { message = "Schedule created", id = schedule.Id });
        }
        
        [HttpPut("{id}/status")]
        public async Task<IActionResult> UpdateStatus(int id, [FromBody] string status) // "Accepted" or "Declined"
        {
            var userId = GetUserId();

            var participant = await _context.WorkScheduleParticipants
                .Include(p => p.WorkSchedule)
                .FirstOrDefaultAsync(p => p.WorkScheduleId == id && p.UserId == userId);

            if (participant == null) return NotFound("You are not a participant in this schedule.");

            participant.Status = status;
            participant.RespondedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            // Notify the creator that someone responded
            var responder = await _context.Users.FindAsync(userId);
            await _hubContext.Clients.User(participant.WorkSchedule.CreatedBy.ToString()).SendAsync("ScheduleStatusUpdated", new {
                scheduleId = id,
                userId = userId,
                fullName = responder?.FullName,
                status = status
            });

            return Ok(new { message = "Status updated" });
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(int id)
        {
            var userId = GetUserId();

            var schedule = await _context.WorkSchedules
                .Include(s => s.Participants)
                .FirstOrDefaultAsync(s => s.Id == id);
                
            if (schedule == null) return NotFound();

            if (schedule.CreatedBy != userId) return Forbid();

            var participantIds = schedule.Participants.Select(p => p.UserId).ToList();

            // Manually remove participants first due to Restrict delete behavior
            _context.WorkScheduleParticipants.RemoveRange(schedule.Participants);
            _context.WorkSchedules.Remove(schedule);
            await _context.SaveChangesAsync();

            // Notify participants that it was deleted
            foreach (var pId in participantIds)
            {
                await _hubContext.Clients.User(pId.ToString()).SendAsync("ScheduleDeleted", id);
            }

            return NoContent();
        }
    }
}
