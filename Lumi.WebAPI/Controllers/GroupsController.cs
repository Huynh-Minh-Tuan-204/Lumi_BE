using Lumi.Application.DTOs;
using Lumi.Infrastructure.Data;
using Lumi.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
    public class GroupsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;

        public GroupsController(ApplicationDbContext context)
        {
            _context = context;
        }

        // POST: /api/groups
        [HttpPost]
        public async Task<IActionResult> CreateGroup([FromBody] CreateGroupConversationDto request)
        {
            var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdStr)) return Unauthorized();
            if (!int.TryParse(userIdStr, out var userId)) return Unauthorized();

            if (request == null || string.IsNullOrWhiteSpace(request.Name))
                return BadRequest(new { error = "Group name is required." });

            var conversation = new Conversation
            {
                Name = request.Name.Trim(),
                Type = "Group",
                CreatedBy = userId,
                CreatedAt = DateTime.UtcNow,
                LastMessageAt = DateTime.UtcNow
            };

            _context.Conversations.Add(conversation);
            await _context.SaveChangesAsync();

            var memberIds = request.MemberIds ?? new List<int>();
            if (!memberIds.Contains(userId)) memberIds.Add(userId);

            var members = memberIds.Distinct().Select(id => new ConversationMember
            {
                ConversationId = conversation.Id,
                UserId = id,
                RoleInConversation = id == userId ? "Admin" : "Member",
                JoinedAt = DateTime.UtcNow,
                IsActive = true
            }).ToList();

            _context.ConversationMembers.AddRange(members);
            await _context.SaveChangesAsync();

            return Ok(new { conversation.Id, conversation.Name, conversation.Type });
        }

        // DELETE: /api/groups/{id}
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteGroup(int id)
        {
            var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdStr)) return Unauthorized();
            if (!int.TryParse(userIdStr, out var userId)) return Unauthorized();

            var conversation = await _context.Conversations.FindAsync(id);
            if (conversation == null || !string.Equals(conversation.Type, "Group", StringComparison.OrdinalIgnoreCase)) return NotFound();

            // Only creator or admin can delete
            var isCreator = conversation.CreatedBy == userId;
            var isAdmin = await _context.ConversationMembers
                .AnyAsync(cm => cm.ConversationId == id && cm.UserId == userId && cm.RoleInConversation == "Admin" && cm.IsActive);

            if (!isCreator && !isAdmin) return Forbid();

            // Soft-delete: mark members inactive and append Deleted to name
            var members = await _context.ConversationMembers.Where(cm => cm.ConversationId == id).ToListAsync();
            foreach (var m in members)
            {
                m.IsActive = false;
                m.LeftAt = DateTime.UtcNow;
            }

            conversation.Name = conversation.Name + " (Deleted)";
            conversation.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            return NoContent();
        }

        // POST: /api/groups/{id}/members
        [HttpPost("{id}/members")]
        public async Task<IActionResult> AddMember(int id, [FromBody] int userIdToAdd)
        {
            var userIdStr = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdStr)) return Unauthorized();
            if (!int.TryParse(userIdStr, out var userId)) return Unauthorized();

            var conversation = await _context.Conversations.FindAsync(id);
            if (conversation == null || !string.Equals(conversation.Type, "Group", StringComparison.OrdinalIgnoreCase)) return NotFound();

            var isCreator = conversation.CreatedBy == userId;
            var isAdmin = await _context.ConversationMembers
                .AnyAsync(cm => cm.ConversationId == id && cm.UserId == userId && cm.RoleInConversation == "Admin" && cm.IsActive);

            if (!isCreator && !isAdmin) return Forbid();

            var exists = await _context.ConversationMembers.AnyAsync(cm => cm.ConversationId == id && cm.UserId == userIdToAdd);
            if (exists) return BadRequest(new { error = "User is already a member." });

            var member = new ConversationMember
            {
                ConversationId = id,
                UserId = userIdToAdd,
                RoleInConversation = "Member",
                JoinedAt = DateTime.UtcNow,
                IsActive = true
            };

            _context.ConversationMembers.Add(member);
            await _context.SaveChangesAsync();

            return Ok(member);
        }

        // GET: /api/groups/{id}/members
        [HttpGet("{id}/members")]
        public async Task<IActionResult> GetMembers(int id)
        {
            var conversation = await _context.Conversations.FindAsync(id);
            if (conversation == null || !string.Equals(conversation.Type, "Group", StringComparison.OrdinalIgnoreCase)) return NotFound();

            var members = await _context.ConversationMembers
                .Where(cm => cm.ConversationId == id && cm.IsActive)
                .Include(cm => cm.User)
                .Select(cm => new
                {
                    cm.User.Id,
                    cm.User.Username,
                    cm.User.FullName,
                    cm.User.AvatarPath,
                    cm.RoleInConversation,
                    cm.JoinedAt
                })
                .ToListAsync();

            return Ok(members);
        }
    }
}
