using Lumi.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System;
using Lumi.Domain.Entities;

namespace Lumi.Infrastructure.Hubs
{
    [Authorize]
    public class CallHub : Hub
    {
        private readonly ApplicationDbContext _context;
        private readonly IHubContext<ChatHub> _chatHubContext;
        
        // Tracking: meetingGuid -> list of {ConnectionId, UserId, DisplayName}
        private static readonly ConcurrentDictionary<string, List<ParticipantInfo>> _meetingParticipants = new();

        public CallHub(ApplicationDbContext context, IHubContext<ChatHub> chatHubContext)
        {
            _context = context;
            _chatHubContext = chatHubContext;
        }

        private int GetUserId()
        {
            var claim = Context.User?.FindFirst(ClaimTypes.NameIdentifier);
            return claim != null ? int.Parse(claim.Value) : 0;
        }

        private string GetUserDisplayName()
        {
            return Context.User?.FindFirst("FullName")?.Value 
                ?? Context.User?.Identity?.Name 
                ?? "User";
        }

        public override async Task OnDisconnectedAsync(Exception exception)
        {
            // Find all meetings this connection was in
            foreach (var meetingGuid in _meetingParticipants.Keys)
            {
                if (_meetingParticipants.TryGetValue(meetingGuid, out var participants))
                {
                    var p = participants.FirstOrDefault(x => x.ConnectionId == Context.ConnectionId);
                    if (p != null)
                    {
                        participants.Remove(p);
                        await Clients.OthersInGroup(meetingGuid).SendAsync("UserLeft", Context.ConnectionId, p.UserId, p.DisplayName);
                        
                        // Check if meeting is empty
                        if (!participants.Any())
                        {
                            await CleanUpMeeting(meetingGuid);
                        }
                    }
                }
            }
            await base.OnDisconnectedAsync(exception);
        }

        private async Task CleanUpMeeting(string meetingGuid)
        {
            if (_meetingParticipants.TryRemove(meetingGuid, out _))
            {
                var meeting = await _context.Meetings.FirstOrDefaultAsync(m => m.MeetingGuid.ToString() == meetingGuid);
                if (meeting != null && meeting.EndedAt == null)
                {
                    meeting.EndedAt = DateTime.UtcNow;
                    await _context.SaveChangesAsync();
                    
                    // Notify ChatHub group about meeting end to clear banners
                    await _chatHubContext.Clients.Group(meeting.ConversationId.ToString()).SendAsync("MeetingEnded", meetingGuid);
                }
            }
        }

        [HubMethodName("JoinCall")]
        public async Task JoinCall(string idOrCode)
        {
            var userId = GetUserId();
            var dbUser = await _context.Users.FindAsync(userId);
            var displayName = dbUser?.FullName ?? dbUser?.Username ?? "User";

            // Resolve the actual meeting and its GUID
            Meeting? meeting = null;
            if (int.TryParse(idOrCode, out int id)) {
                meeting = await _context.Meetings.Include(m => m.Conversation).FirstOrDefaultAsync(m => m.Id == id);
            } else {
                meeting = await _context.Meetings.Include(m => m.Conversation).FirstOrDefaultAsync(m => m.MeetingGuid == idOrCode);
            }

            if (meeting == null) return;
            string meetingGuid = meeting.MeetingGuid ?? idOrCode;

            // Ensure the user has access to the conversation if it's a global meeting
            if (meeting.Conversation != null && meeting.Conversation.Type == "GlobalMeeting")
            {
                var isMember = await _context.ConversationMembers.AnyAsync(cm => cm.ConversationId == meeting.ConversationId && cm.UserId == userId);
                if (!isMember)
                {
                    _context.ConversationMembers.Add(new ConversationMember {
                        ConversationId = meeting.ConversationId,
                        UserId = userId,
                        RoleInConversation = "Member",
                        IsActive = true,
                        JoinedAt = DateTime.UtcNow
                    });
                    await _context.SaveChangesAsync();
                }
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, meetingGuid);

            var participant = new ParticipantInfo {
                ConnectionId = Context.ConnectionId,
                UserId = userId,
                DisplayName = displayName
            };

            _meetingParticipants.AddOrUpdate(meetingGuid, 
                new List<ParticipantInfo> { participant }, 
                (k, list) => { 
                    list.RemoveAll(x => x.UserId == userId); // Prevent duplicates from same user
                    list.Add(participant); 
                    return list; 
                });

            // 1. Notify others
            await Clients.OthersInGroup(meetingGuid).SendAsync("UserJoined", Context.ConnectionId, userId, displayName);
            
            // 2. Send current list to ALL users in the meeting to ensure sync
            var currentList = _meetingParticipants[meetingGuid].Select(x => new {
                userId = x.UserId,
                connectionId = x.ConnectionId,
                displayName = x.DisplayName
            }).ToList();
            
            await Clients.Group(meetingGuid).SendAsync("MeetingMemberList", currentList);
        }

        public async Task LeaveCall(string meetingGuid)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, meetingGuid);
            
            if (_meetingParticipants.TryGetValue(meetingGuid, out var participants))
            {
                var p = participants.FirstOrDefault(x => x.ConnectionId == Context.ConnectionId);
                if (p != null)
                {
                    participants.Remove(p);
                    await Clients.OthersInGroup(meetingGuid).SendAsync("UserLeft", Context.ConnectionId, p.UserId, p.DisplayName);
                    
                    if (!participants.Any())
                    {
                        await CleanUpMeeting(meetingGuid);
                    }
                }
            }
        }

        // Signaling for WebRTC (Mesh)
        public async Task SendOffer(string meetingGuid, int targetUserId, object offer)
        {
            var senderUserId = GetUserId();
            await Clients.User(targetUserId.ToString()).SendAsync("ReceiveOffer", offer, senderUserId);
        }

        public async Task SendAnswer(string meetingGuid, int targetUserId, object answer)
        {
            var senderUserId = GetUserId();
            await Clients.User(targetUserId.ToString()).SendAsync("ReceiveAnswer", answer, senderUserId);
        }

        public async Task SendIceCandidate(string meetingGuid, int targetUserId, object candidate)
        {
            var senderUserId = GetUserId();
            await Clients.User(targetUserId.ToString()).SendAsync("ReceiveIceCandidate", candidate, senderUserId);
        }

        public async Task RequestJoin(string meetingGuid)
        {
            var userId = GetUserId();
            var meeting = await _context.Meetings.FirstOrDefaultAsync(m => m.MeetingGuid.ToString() == meetingGuid);
            if (meeting == null) return;

            var requester = await _context.Users.FindAsync(userId);
            var requesterName = requester?.FullName ?? "Member";

            await Clients.User(meeting.CreatedBy.ToString()).SendAsync("IncomingJoinRequest", new
            {
                MeetingId = meeting.Id,
                MeetingGuid = meetingGuid,
                UserId = userId,
                FullName = requesterName,
                AvatarPath = requester?.AvatarPath
            });
        }

        public async Task AcceptJoinRequest(string meetingGuid, int attendeeId)
        {
            var userId = GetUserId();
            var meeting = await _context.Meetings.Include(m => m.Conversation).FirstOrDefaultAsync(m => m.MeetingGuid.ToString() == meetingGuid);
            if (meeting == null || meeting.CreatedBy != userId) return;

            // Grant permission to chat if it's a global meeting
            if (meeting.Conversation != null && meeting.Conversation.Type == "GlobalMeeting")
            {
                var isMember = await _context.ConversationMembers.AnyAsync(cm => cm.ConversationId == meeting.ConversationId && cm.UserId == attendeeId);
                if (!isMember)
                {
                    _context.ConversationMembers.Add(new ConversationMember {
                        ConversationId = meeting.ConversationId,
                        UserId = attendeeId,
                        RoleInConversation = "Member",
                        IsActive = true,
                        JoinedAt = DateTime.UtcNow
                    });
                    await _context.SaveChangesAsync();
                }
            }

            await Clients.User(attendeeId.ToString()).SendAsync("JoinRequestAccepted", meetingGuid);
        }

        public async Task DeclineJoinRequest(string meetingGuid, int attendeeId)
        {
            var userId = GetUserId();
            var meeting = await _context.Meetings.FirstOrDefaultAsync(m => m.MeetingGuid.ToString() == meetingGuid);
            if (meeting == null || meeting.CreatedBy != userId) return;

            await Clients.User(attendeeId.ToString()).SendAsync("JoinRequestDeclined", meetingGuid, "Người tổ chức đã từ chối yêu cầu tham gia.");
        }
    }

    public class ParticipantInfo
    {
        public string ConnectionId { get; set; }
        public int UserId { get; set; }
        public string DisplayName { get; set; }
    }
}