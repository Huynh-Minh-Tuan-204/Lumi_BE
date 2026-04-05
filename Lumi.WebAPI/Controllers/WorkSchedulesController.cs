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

namespace Lumi.WebAPI.Controllers
{
    [Authorize]
    [ApiController]
    [Route("api/[controller]")]
    public class WorkSchedulesController : ControllerBase
    {
        private readonly ApplicationDbContext _context;

        public WorkSchedulesController(ApplicationDbContext context)
        {
            _context = context;
        }

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
            var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdStr)) return Unauthorized();
            int userId = int.Parse(userIdStr);

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
            var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdStr)) return Unauthorized();
            int userId = int.Parse(userIdStr);

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
            }

            return Ok(new { message = "Schedule created", id = schedule.Id });
        }
        
        [HttpPut("{id}/status")]
        public async Task<IActionResult> UpdateStatus(int id, [FromBody] string status) // "Accepted" or "Declined"
        {
            var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdStr)) return Unauthorized();
            int userId = int.Parse(userIdStr);

            var participant = await _context.WorkScheduleParticipants
                .FirstOrDefaultAsync(p => p.WorkScheduleId == id && p.UserId == userId);

            if (participant == null) return NotFound("You are not a participant in this schedule.");

            participant.Status = status;
            participant.RespondedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return Ok(new { message = "Status updated" });
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(int id)
        {
            var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdStr)) return Unauthorized();
            int userId = int.Parse(userIdStr);

            var schedule = await _context.WorkSchedules.FindAsync(id);
            if (schedule == null) return NotFound();

            if (schedule.CreatedBy != userId) return Forbid();

            _context.WorkSchedules.Remove(schedule);
            await _context.SaveChangesAsync();

            return NoContent();
        }
    }
}
