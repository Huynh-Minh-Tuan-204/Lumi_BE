using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Hangfire;
using Lumi.Domain.Entities;
using Lumi.Infrastructure.Data;
using Lumi.Infrastructure.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
namespace Lumi.Infrastructure.Hubs
{
    public class ChatHub : Hub
    {
        private readonly ApplicationDbContext _context;
        private readonly ILogger<ChatHub> _logger;
        private readonly IBackgroundJobClient _backgroundJobs;
        
        public ChatHub(ApplicationDbContext context, ILogger<ChatHub> logger, IBackgroundJobClient backgroundJobs)
        {
            _context = context;
            _logger = logger;
            _backgroundJobs = backgroundJobs;
        }

        public override async Task OnConnectedAsync()
        {
            var userIdClaim = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!string.IsNullOrEmpty(userIdClaim) && int.TryParse(userIdClaim, out int currentUserId))
            {
                var device = await _context.UserDevices
                    .AsNoTracking()
                    .Where(d => d.UserId == currentUserId && d.IsActive)
                    .OrderByDescending(d => d.LastSeen)
                    .FirstOrDefaultAsync();

                if (device == null)
                {
                    device = new UserDevice
                    {
                        UserId = currentUserId,
                        DeviceName = "Auto-Registered Device (SignalR)",
                        DeviceType = "Web",
                        DeviceIdentifier = Guid.NewGuid().ToString(),
                        IsActive = true,
                        CreatedAt = DateTime.UtcNow,
                        LastSeen = DateTime.UtcNow
                    };
                    _context.UserDevices.Add(device);
                    await _context.SaveChangesAsync();
                }

                var conn = await _context.SignalRConnections.FirstOrDefaultAsync(s => s.ConnectionId == Context.ConnectionId);
                if (conn == null)
                {
                    conn = new SignalRConnection
                    {
                        UserId = currentUserId,
                        DeviceId = device.Id,
                        ConnectionId = Context.ConnectionId,
                        ConnectedAt = DateTime.UtcNow,
                        IsActive = true
                    };
                    _context.SignalRConnections.Add(conn);
                }
                else
                {
                    conn.UserId = currentUserId;
                    conn.DeviceId = device.Id;
                    conn.IsActive = true;
                }

                device.LastSeen = DateTime.UtcNow;
                var convIds = await _context.ConversationMembers
                    .Where(cm => cm.UserId == currentUserId && cm.IsActive)
                    .Select(cm => cm.ConversationId)
                    .ToListAsync();

                foreach (var id in convIds) await Groups.AddToGroupAsync(Context.ConnectionId, id.ToString());
                await Groups.AddToGroupAsync(Context.ConnectionId, $"user_{currentUserId}");
                await Groups.AddToGroupAsync(Context.ConnectionId, "all_users");

                if (!await _context.SignalRConnections.AnyAsync(s => s.UserId == currentUserId && s.IsActive && s.ConnectionId != Context.ConnectionId))
                {
                    await Clients.Others.SendAsync("UserStatusChanged", currentUserId, true);
                }
                await _context.SaveChangesAsync();
                var onlineUserIds = await _context.SignalRConnections.Where(s => s.IsActive).Select(s => s.UserId).Distinct().ToListAsync();
                await Clients.Caller.SendAsync("InitialOnlineUsers", onlineUserIds);

                // --- Sync ONLY the LATEST active Global Meeting for newly online user (Exclude Host) ---
                var latestGlobalMeeting = await _context.Meetings
                    .Include(m => m.Conversation)
                    .Where(m => m.EndedAt == null && m.Conversation.Type == "GlobalMeeting" && m.CreatedBy != currentUserId)
                    .OrderByDescending(m => m.StartedAt)
                    .FirstOrDefaultAsync();
                
                if (latestGlobalMeeting != null) {
                    await Clients.Caller.SendAsync("GlobalMeetingStarted", new {
                        meetingId = latestGlobalMeeting.MeetingGuid,
                        title = latestGlobalMeeting.Title,
                        hostName = "Admin", 
                        conversationId = latestGlobalMeeting.ConversationId,
                        type = latestGlobalMeeting.CallType
                    });
                }
            }
            await base.OnConnectedAsync();
        }

        private string GenerateRoomCode()
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
            var random = new Random();
            return new string(Enumerable.Repeat(chars, 8)
                .Select(s => s[random.Next(s.Length)]).ToArray());
        }

        public override async Task OnDisconnectedAsync(Exception exception)
        {
            try
            {
                var conn = await _context.SignalRConnections.FirstOrDefaultAsync(s => s.ConnectionId == Context.ConnectionId);
                if (conn != null)
                {
                    int userId = conn.UserId;
                    conn.IsActive = false;
                    conn.DisconnectedAt = DateTime.UtcNow;
                    var stillConnected = await _context.SignalRConnections.AnyAsync(s => s.UserId == userId && s.IsActive);
                    if (!stillConnected) await Clients.All.SendAsync("UserStatusChanged", userId, false);
                    await _context.SaveChangesAsync();
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "Error in OnDisconnectedAsync"); }
            await base.OnDisconnectedAsync(exception);
        }

        public async Task MarkAsRead(int conversationId, int? messageId = null)
        {
            try
            {
                var userIdStr = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (!int.TryParse(userIdStr, out int userId)) return;

                // Validate message existence and ownership check
                Message message = null;
                if (messageId.HasValue && messageId.Value > 0)
                {
                    message = await _context.Messages.AsNoTracking()
                        .FirstOrDefaultAsync(m => m.Id == messageId.Value && m.ConversationId == conversationId);
                }
                else
                {
                    // If no messageId or messageId <= 0, get latest message in conversation
                    message = await _context.Messages.AsNoTracking()
                        .Where(m => m.ConversationId == conversationId)
                        .OrderByDescending(m => m.CreatedAt)
                        .FirstOrDefaultAsync();
                }
                
                if (message == null) return;

                var conn = await _context.SignalRConnections.AsNoTracking()
                    .FirstOrDefaultAsync(s => s.ConnectionId == Context.ConnectionId);
                
                if (conn == null) return;

                // Check if already read by this specific device (matching the unique index)
                var alreadyRead = await _context.MessageReads.AnyAsync(mr => 
                    mr.MessageId == message.Id && 
                    mr.UserId == userId && 
                    mr.DeviceId == conn.DeviceId);

                if (!alreadyRead)
                {
                    _context.MessageReads.Add(new MessageRead 
                    { 
                        MessageId = message.Id, 
                        UserId = userId, 
                        DeviceId = conn.DeviceId,
                        ReadAt = DateTime.UtcNow 
                    });
                    
                    await _context.SaveChangesAsync();
                    await Clients.Group(conversationId.ToString()).SendAsync("MessageRead", conversationId, message.Id, userId);
                }
            }
            catch (DbUpdateException ex)
            {
                var innerMessage = ex.InnerException?.Message ?? ex.Message;
                _logger.LogError(ex, "Database error in MarkAsRead: {Message}", innerMessage);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "System error in MarkAsRead for message {MessageId} in conversation {ConversationId}", messageId, conversationId);
            }
        }

        public async Task StartMeeting(int conversationId)
        {
            var userIdStr = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdStr)) return;
            int currentUserId = int.Parse(userIdStr);

            var conv = await _context.Conversations.FindAsync(conversationId);
            if (conv == null) return;

            var meeting = await _context.Meetings.FirstOrDefaultAsync(m => m.ConversationId == conversationId && m.EndedAt == null);
            if (meeting != null) return;

            meeting = new Meeting
            {
                ConversationId = conversationId,
                Title = conv.Name,
                CreatedBy = currentUserId,
                StartedAt = DateTime.UtcNow,
                MeetingGuid = GenerateRoomCode(),
                CallType = "video"
            };

            _context.Meetings.Add(meeting);
            
            // Add creator as participant
            var participant = new MeetingParticipant {
                MeetingId = meeting.Id,
                UserId = currentUserId,
                JoinedAt = DateTime.UtcNow,
                IsHost = true,
                IsPresent = true
            };
            _context.MeetingParticipants.Add(participant);
            
            await _context.SaveChangesAsync();

            var caller = await _context.Users.FindAsync(currentUserId);
            var callerName = caller?.FullName ?? caller?.Username ?? "Someone";

            await Clients.Group(conversationId.ToString()).SendAsync("MeetingStarted", new
            {
                meetingId = meeting.MeetingGuid,
                conversationId = meeting.ConversationId,
                title = meeting.Title,
                createdBy = meeting.CreatedBy,
                startedAt = meeting.StartedAt,
                callType = meeting.CallType,
                hostName = callerName
            });
        }

        public async Task EndMeeting(int meetingId)
        {
            var meeting = await _context.Meetings.FindAsync(meetingId);
            if (meeting == null) return;
            meeting.EndedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            await Clients.Group(meeting.ConversationId.ToString()).SendAsync("MeetingEnded", meeting.MeetingGuid);
        }

        public async Task SendMessage(int conversationId, string encryptedMessage, string iv)
        {
            var userIdStr = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdStr)) return;
            int senderId = int.Parse(userIdStr);

            var msg = new Message { ConversationId = conversationId, SenderId = senderId, EncryptedContent = encryptedMessage, IV = iv, MessageType = "Text", CreatedAt = DateTime.UtcNow };
            _context.Messages.Add(msg);
            var conversation = await _context.Conversations.FindAsync(conversationId);
            if (conversation != null) conversation.LastMessageAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            var sender = await _context.Users.FindAsync(senderId);
            await Clients.Group(conversationId.ToString()).SendAsync("ReceiveMessage", new {
                id = msg.Id,
                conversationId = conversationId,
                senderId = senderId,
                senderName = sender?.FullName ?? "User",
                content = encryptedMessage,
                iv = iv,
                messageType = msg.MessageType,
                createdAt = msg.CreatedAt.ToString("o"),
                attachments = new List<object>()
            });
        }
    }
}