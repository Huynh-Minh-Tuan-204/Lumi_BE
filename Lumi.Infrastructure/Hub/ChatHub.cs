using System;
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
namespace Lumi.Infrastructure.Hubs // Đảm bảo folder là Lumi.Infrastructure/Hubs
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
            if (!string.IsNullOrEmpty(userIdClaim))
            {
                int currentUserId = int.Parse(userIdClaim);

                // Cleanup old connections disabled temporarily to prevent DB locks
                // var oldConnections = _context.SignalRConnections.Where(c => c.UserId == currentUserId);
                // _context.SignalRConnections.RemoveRange(oldConnections);
                // await _context.SaveChangesAsync();

                // 2. Tìm thiết bị (Device) đang hoạt động của người dùng này
                var device = await _context.UserDevices
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
                        IsRevoked = false,
                        CreatedAt = DateTime.UtcNow,
                        LastSeen = DateTime.UtcNow
                    };
                    _context.UserDevices.Add(device);
                    await _context.SaveChangesAsync();
                }

                // 3. Track the new connection
                var existingConn = await _context.SignalRConnections
                    .FirstOrDefaultAsync(s => s.ConnectionId == Context.ConnectionId);

                if (existingConn != null)
                {
                    existingConn.UserId = currentUserId;
                    existingConn.DeviceId = device.Id; // Gán ID thật từ Database
                    existingConn.ConnectedAt = DateTime.UtcNow;
                    existingConn.IsActive = true;
                    existingConn.DisconnectedAt = null;
                }
                else
                {
                    var conn = new SignalRConnection
                    {
                        UserId = currentUserId,
                        DeviceId = device.Id, // KHÔNG ĐỂ = 0 NỮA, dùng device.Id
                        ConnectionId = Context.ConnectionId,
                        ConnectedAt = DateTime.UtcNow,
                        IsActive = true
                    };
                    _context.SignalRConnections.Add(conn);
                }

                // 4. Cập nhật LastSeen cho thiết bị
                device.LastSeen = DateTime.UtcNow;

                // 5. Add user to their conversation groups
                var convIds = await _context.ConversationMembers
                    .Where(cm => cm.UserId == currentUserId && cm.IsActive)
                    .Select(cm => cm.ConversationId)
                    .ToListAsync();

                foreach (var id in convIds)
                {
                    await Groups.AddToGroupAsync(Context.ConnectionId, id.ToString());
                }

                // Add to personal group for direct notifications (e.g. incoming calls)
                await Groups.AddToGroupAsync(Context.ConnectionId, $"user_{currentUserId}");
                
                // Add to all_users group for system announcements
                await Groups.AddToGroupAsync(Context.ConnectionId, "all_users");

                // 6. Thông báo cho người khác biết User này Online nếu đây là connection đầu tiên
                var otherActiveConnections = await _context.SignalRConnections
                    .AnyAsync(s => s.UserId == currentUserId && s.IsActive && s.ConnectionId != Context.ConnectionId);
                
                if (!otherActiveConnections)
                {
                    await Clients.Others.SendAsync("UserStatusChanged", currentUserId, true);
                }

                await _context.SaveChangesAsync();

                // 7. Gửi danh sách online hiện tại cho người mới kết nối (SAU KHI LƯU DB để bao gồm cả bản thân)
                var onlineUserIds = await _context.SignalRConnections
                    .Where(s => s.IsActive)
                    .Select(s => s.UserId)
                    .Distinct()
                    .ToListAsync();
                
                await Clients.Caller.SendAsync("InitialOnlineUsers", onlineUserIds);

                _logger.LogInformation($"SignalR: User {currentUserId} connected (Device: {device.Id}) and joined {convIds.Count} groups.");
            }
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception exception)
        {
            try
            {
                var conn = await _context.SignalRConnections
                    .FirstOrDefaultAsync(s => s.ConnectionId == Context.ConnectionId);
                if (conn != null)
                {
                    int userId = conn.UserId;
                    conn.IsActive = false;
                    conn.DisconnectedAt = DateTime.UtcNow;
                    // await _context.SaveChangesAsync(); // Moved to after meeting cleanup

                    // Chỉ báo offline nếu không còn connection nào khác active
                    var stillConnected = await _context.SignalRConnections
                        .AnyAsync(s => s.UserId == userId && s.IsActive);
                    
                    // Clean up active meeting participants
                    var activeParticipants = await _context.MeetingParticipants
                        .Where(mp => mp.UserId == userId && mp.IsPresent)
                        .ToListAsync();

                    foreach (var p in activeParticipants)
                    {
                        p.IsPresent = false;
                        p.LeftAt = DateTime.UtcNow;
                    }

                    if (!stillConnected)
                    {
                        await Clients.All.SendAsync("UserStatusChanged", userId, false);
                    }
                    await _context.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in OnDisconnectedAsync meeting cleanup");
            }

            await base.OnDisconnectedAsync(exception);
        }
        public async Task SendNotification(string message)
        {
            // Lấy ID người gửi từ Claim thay vì UserIdentifier nếu nó bị null
            var userIdStr = Context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

            if (string.IsNullOrEmpty(userIdStr)) return;

            int senderId = int.Parse(userIdStr);

            // Find an existing conversation or create a system one to attach messages to
            var generalConv = await _context.Conversations.FirstOrDefaultAsync(c => c.Name == "System Announcements" && c.Type == "Group");
            if (generalConv == null) {
                generalConv = new Conversation { Name = "System Announcements", Type = "Group", CreatedBy = senderId, CreatedAt = DateTime.UtcNow, LastMessageAt = DateTime.UtcNow };
                _context.Conversations.Add(generalConv);
                await _context.SaveChangesAsync();
            }

            var msg = new Message
            {
                ConversationId = generalConv.Id,
                SenderId = senderId,
                EncryptedContent = message,
                IV = "SYSTEM_MSG",
                MessageType = "Announcement",
                CreatedAt = DateTime.UtcNow
            };

            _context.Messages.Add(msg);

            generalConv.LastMessageAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            await Clients.Group("all_users").SendAsync("ReceiveNotification", new {
                id = msg.Id,
                sender = "📢 HỆ THỐNG", 
                message = message, 
                isSystem = true, 
                createdAt = msg.CreatedAt.ToString("o"), 
                senderId = 0 
            });
        }
        public async Task SendMessage(int conversationId, string encryptedMessage, string iv)
        {
            var userIdStr = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdStr)) return;

            int senderId = int.Parse(userIdStr);

            // CHECK USER MEMBERSHIP
            bool isMember = await _context.ConversationMembers
                .AnyAsync(cm =>
                    cm.ConversationId == conversationId &&
                    cm.UserId == senderId &&
                    cm.IsActive);

            if (!isMember)
                throw new HubException("User not in conversation");

            var msg = new Message
            {
                ConversationId = conversationId,
                SenderId = senderId,
                EncryptedContent = encryptedMessage,
                IV = iv,
                MessageType = "Text",
                CreatedAt = DateTime.UtcNow
            };

            _context.Messages.Add(msg);

            // UPDATE LAST MESSAGE TIME
            var conversation = await _context.Conversations.FindAsync(conversationId);

            if (conversation != null)
            {
                conversation.LastMessageAt = DateTime.UtcNow;
            }

            await _context.SaveChangesAsync();

            // GET SENDER INFO
            var sender = await _context.Users.FindAsync(senderId);
            var senderName = sender?.FullName ?? sender?.Username ?? "User";

            // BROADCAST MESSAGE
            await Clients.Group(conversationId.ToString())
                .SendAsync("ReceiveMessage", new {
                    id = msg.Id,
                    conversationId = conversationId,
                    senderId = senderId,
                    senderName = senderName,
                    content = encryptedMessage,
                    iv = iv,
                    messageType = msg.MessageType,
                    stickerUrl = msg.StickerUrl,
                    isPinned = msg.IsPinned,
                    createdAt = msg.CreatedAt.ToString("o"),
                    avatarPath = sender?.AvatarPath,
                    attachments = new string[0]
                });
        }

        public async Task SendSticker(int conversationId, string stickerUrl)
        {
            var userIdStr = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdStr)) return;
            int senderId = int.Parse(userIdStr);

            var msg = new Message
            {
                ConversationId = conversationId,
                SenderId = senderId,
                EncryptedContent = "[Sticker]",
                IV = "STICKER",
                MessageType = "Sticker",
                StickerUrl = stickerUrl,
                CreatedAt = DateTime.UtcNow
            };

            _context.Messages.Add(msg);
            var conversation = await _context.Conversations.FindAsync(conversationId);
            if (conversation != null) conversation.LastMessageAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            var sender = await _context.Users.FindAsync(senderId);
            await Clients.Group(conversationId.ToString()).SendAsync("ReceiveMessage", new {
                id = msg.Id,
                conversationId = conversationId,
                senderId = senderId,
                senderName = sender?.FullName ?? sender?.Username ?? "User",
                content = msg.EncryptedContent,
                iv = msg.IV,
                messageType = msg.MessageType,
                stickerUrl = msg.StickerUrl,
                createdAt = msg.CreatedAt.ToString("o"),
                avatarPath = sender?.AvatarPath
            });
        }

        public async Task TogglePinMessage(int messageId)
        {
            var userIdStr = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdStr)) return;
            int currentUserId = int.Parse(userIdStr);

            var message = await _context.Messages.FindAsync(messageId);
            if (message == null) return;

            message.IsPinned = (message.IsPinned ?? false) == false;
            bool isNewPinned = message.IsPinned.Value;
            message.PinnedAt = isNewPinned ? DateTime.UtcNow : null;
            message.PinnedBy = isNewPinned ? (int?)currentUserId : null;

            await _context.SaveChangesAsync();

            // 1. Gửi event cập nhật trạng thái Ghim
            await Clients.Group(message.ConversationId.ToString()).SendAsync("MessagePinned", new {
                messageId = message.Id,
                isPinned = message.IsPinned,
                pinnedBy = message.PinnedBy,
                conversationId = message.ConversationId
            });

            // 2. Tạo tin nhắn hệ thống thông báo việc Ghim/Bỏ ghim
            var user = await _context.Users.FindAsync(currentUserId);
            var dMessage = await _context.Messages.Include(m => m.Attachments).FirstOrDefaultAsync(m => m.Id == messageId);
            
            string extraInfo = "";
            if (dMessage != null && dMessage.Attachments != null && dMessage.Attachments.Any()) {
                extraInfo = $" file {dMessage.Attachments.First().FileName}";
            } else if (dMessage != null && !string.IsNullOrEmpty(dMessage.EncryptedContent) && dMessage.EncryptedContent.Contains("http")) {
                var url = dMessage.EncryptedContent.Split(new[] { ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(s => s.StartsWith("http"));
                if (url != null) {
                    if (url.Length > 25) url = url.Substring(0, 25) + "...";
                    extraInfo = $" link {url}";
                }
            }

            string userNameDisplay = user?.Id == currentUserId ? "Bạn" : (user?.FullName ?? user?.Username ?? "Ai đó");
            string actionText = isNewPinned ? $"ghim 1 tin nhắn{extraInfo}" : $"bỏ ghim 1 tin nhắn{extraInfo}";
            
            var systemMsg = new Message
            {
                ConversationId = message.ConversationId,
                SenderId = currentUserId,
                EncryptedContent = $"{userNameDisplay} {actionText}",
                IV = "SYSTEM_MSG",
                MessageType = "Announcement", // Dùng type này để hiển thị ở giữa
                ParentMessageId = message.Id,
                CreatedAt = DateTime.UtcNow
            };

            _context.Messages.Add(systemMsg);
            await _context.SaveChangesAsync();

            // 3. Broadcast tin nhắn hệ thống này cho mọi người thấy trong feed
            await Clients.Group(message.ConversationId.ToString()).SendAsync("ReceiveMessage", new {
                id = systemMsg.Id,
                conversationId = systemMsg.ConversationId,
                senderId = systemMsg.SenderId,
                senderName = "Hệ thống",
                content = systemMsg.EncryptedContent,
                iv = systemMsg.IV,
                messageType = systemMsg.MessageType,
                parentMessageId = systemMsg.ParentMessageId,
                createdAt = systemMsg.CreatedAt.ToString("o")
            });
        }

        public async Task SendReminder(int conversationId, string content, string remindAtIso)
        {
            var userIdStr = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdStr)) return;
            int senderId = int.Parse(userIdStr);

            var msg = new Message
            {
                ConversationId = conversationId,
                SenderId = senderId,
                EncryptedContent = content, // Should be encrypted but simplified for reminder logic
                IV = "REMINDER",
                MessageType = "Reminder",
                CreatedAt = DateTime.UtcNow
            };

            _context.Messages.Add(msg);
            await _context.SaveChangesAsync();

            await Clients.Group(conversationId.ToString()).SendAsync("ReceiveMessage", new {
                id = msg.Id,
                conversationId = conversationId,
                senderId = senderId,
                content = content,
                messageType = "Reminder",
                remindAt = remindAtIso,
                createdAt = msg.CreatedAt.ToString("o")
            });
            if (DateTime.TryParse(remindAtIso, out var remindAt))
            {
                var delay = remindAt - DateTime.UtcNow;
                if (delay.TotalSeconds > 0)
                {
                    _backgroundJobs.Schedule<IReminderService>(
                        s => s.TriggerReminder(senderId, conversationId, content), 
                        delay);
                }
            }
        }

        public async Task SendTyping(int conversationId)
        {
            var userIdStr = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdStr)) return;
            
            int senderId = int.Parse(userIdStr);
            var sender = await _context.Users.FindAsync(senderId);
            var senderName = sender?.FullName ?? sender?.Username ?? "User";

            await Clients.Group(conversationId.ToString()).SendAsync("UserTyping", conversationId, senderId, senderName);
        }

        public async Task MarkAsRead(int conversationId)
        {
            var userIdStr = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdStr)) return;
            int userId = int.Parse(userIdStr);

            // Find unread messages for this user in this conversation 
            // where they are NOT the sender
            var unreadMessageIds = await _context.Messages
                .Where(m => m.ConversationId == conversationId && 
                            m.SenderId != userId &&
                            (m.IsDeleted ?? false) == false &&
                            !m.MessageReads.Any(mr => mr.UserId == userId))
                .Select(m => m.Id)
                .ToListAsync();

            if (!unreadMessageIds.Any()) return;

            // Robust device check (mirroring Controller logic)
            var device = await _context.UserDevices
                .Where(ud => ud.UserId == userId && ud.IsActive)
                .OrderByDescending(ud => ud.LastSeen)
                .FirstOrDefaultAsync();

            if (device == null)
            {
                device = new UserDevice
                {
                    UserId = userId,
                    DeviceName = "Auto-Registered Device (SignalR/Read)",
                    DeviceType = "Web",
                    DeviceIdentifier = Guid.NewGuid().ToString(),
                    IsActive = true,
                    IsRevoked = false,
                    CreatedAt = DateTime.UtcNow,
                    LastSeen = DateTime.UtcNow
                };
                _context.UserDevices.Add(device);
                await _context.SaveChangesAsync();
            }
            int deviceId = device.Id;

            foreach (var msgId in unreadMessageIds)
            {
                _context.MessageReads.Add(new MessageRead
                {
                    MessageId = msgId,
                    UserId = userId,
                    DeviceId = deviceId,
                    ReadAt = DateTime.UtcNow
                });
            }

            await _context.SaveChangesAsync();

            // Notify others that this user has read the messages
            await Clients.OthersInGroup(conversationId.ToString()).SendAsync("UserReadConversation", conversationId, userId);
        }

        // WebRTC signaling: caller -> callee (offer)
        public async Task CallUser(int targetUserId, string offer)
        {
            var userIdStr = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdStr)) return;

            int currentUserId = int.Parse(userIdStr);

            var connectionIds = await _context.SignalRConnections
                .Where(s => s.UserId == targetUserId && s.IsActive)
                .Select(s => s.ConnectionId)
                .ToListAsync();

            if (connectionIds.Any())
            {
                await Clients.Clients(connectionIds).SendAsync("IncomingCall", offer, currentUserId);
            }
        }

        // WebRTC signaling: answer from callee -> caller
        public async Task AnswerCall(int targetUserId, string answer)
        {
            var userIdStr = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdStr)) return;

            int currentUserId = int.Parse(userIdStr);

            var connectionIds = await _context.SignalRConnections
                .Where(s => s.UserId == targetUserId && s.IsActive)
                .Select(s => s.ConnectionId)
                .ToListAsync();

            if (connectionIds.Any())
            {
                await Clients.Clients(connectionIds).SendAsync("CallAnswered", answer, currentUserId);
            }
        }

        // Relay ICE candidate
        public async Task SendIceCandidate(int targetUserId, string candidate)
        {
            var userIdStr = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdStr)) return;

            int currentUserId = int.Parse(userIdStr);

            var connectionIds = await _context.SignalRConnections
                .Where(s => s.UserId == targetUserId && s.IsActive)
                .Select(s => s.ConnectionId)
                .ToListAsync();

            if (connectionIds.Any())
            {
                await Clients.Clients(connectionIds).SendAsync("ReceiveIceCandidate", candidate, currentUserId);
            }
        }

        // Start a meeting for a conversation
        public async Task StartMeeting(int conversationId)
        {
            var userIdStr = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdStr)) return;

            int currentUserId = int.Parse(userIdStr);

            var conv = await _context.Conversations.FindAsync(conversationId);
            if (conv == null)
            {
                _logger.LogWarning("StartMeeting: conversation {ConversationId} not found", conversationId);
                return;
            }

            var existing = await _context.Meetings
    .Where(m => m.ConversationId == conversationId && m.EndedAt == null)
    .FirstOrDefaultAsync();

            if (existing != null)
            {
                return;
            }

            var meeting = new Meeting
            {
                ConversationId = conversationId,
                Title = conv.Name,
                CreatedBy = currentUserId,
                StartedAt = DateTime.UtcNow,
                IsRecording = false
            };

            _context.Meetings.Add(meeting);
            await _context.SaveChangesAsync();

            // Add participants from conversation members
            var members = await _context.ConversationMembers
                .Where(cm => cm.ConversationId == conversationId && cm.IsActive)
                .Select(cm => cm.UserId)
                .ToListAsync();

            var participants = members.Select(uId => new MeetingParticipant
            {
                MeetingId = meeting.Id,
                UserId = uId,
                JoinedAt = DateTime.UtcNow,
                IsPresent = false
            }).ToList();

            if (participants.Any())
            {
                _context.MeetingParticipants.AddRange(participants);
                await _context.SaveChangesAsync();
            }

            // Broadcast meeting started to the conversation group
            await Clients.Group(conversationId.ToString()).SendAsync("MeetingStarted", new
            {
                meeting.Id,
                meeting.ConversationId,
                meeting.Title,
                meeting.CreatedBy,
                meeting.StartedAt
            });
        }

        // User joins a meeting
        public async Task JoinMeeting(int meetingId)
        {
            var userIdStr = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdStr)) return;

            int currentUserId = int.Parse(userIdStr);

            var meeting = await _context.Meetings.FindAsync(meetingId);
            if (meeting == null) return;

            var participant = await _context.MeetingParticipants
                .FirstOrDefaultAsync(mp => mp.MeetingId == meetingId && mp.UserId == currentUserId);

            if (participant == null)
            {
                participant = new MeetingParticipant
                {
                    MeetingId = meetingId,
                    UserId = currentUserId,
                    JoinedAt = DateTime.UtcNow,
                    IsPresent = true
                };
                _context.MeetingParticipants.Add(participant);
            }
            else
            {
                participant.JoinedAt = DateTime.UtcNow;
                participant.LeftAt = null;
                participant.IsPresent = true;
            }

            await _context.SaveChangesAsync();

            // Broadcast join event to conversation group
            await Clients.Group(meeting.ConversationId.ToString()).SendAsync("UserJoinedMeeting", new
            {
                MeetingId = meetingId,
                UserId = participant.UserId,
                JoinedAt = participant.JoinedAt
            });
        }

        // User leaves a meeting
        public async Task LeaveMeeting(int meetingId)
        {
            var userIdStr = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdStr)) return;

            int currentUserId = int.Parse(userIdStr);

            var meeting = await _context.Meetings.FindAsync(meetingId);
            if (meeting == null) return;

            var participant = await _context.MeetingParticipants
                .FirstOrDefaultAsync(mp => mp.MeetingId == meetingId && mp.UserId == currentUserId && mp.LeftAt == null);
            if (participant == null) return;

            participant.LeftAt = DateTime.UtcNow;
            participant.IsPresent = false;
            participant.DurationSeconds = (int)(participant.LeftAt.Value - participant.JoinedAt).TotalSeconds;

            await _context.SaveChangesAsync();

            await Clients.Group(meeting.ConversationId.ToString()).SendAsync("UserLeftMeeting", new
            {
                MeetingId = meetingId,
                UserId = participant.UserId,
                LeftAt = participant.LeftAt,
                DurationSeconds = participant.DurationSeconds
            });
        }

        // End a meeting
        public async Task EndMeeting(int meetingId)
        {
            var userIdStr = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdStr)) return;

            int currentUserId = int.Parse(userIdStr);

            var meeting = await _context.Meetings.FindAsync(meetingId);
            if (meeting == null) return;

            meeting.EndedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            await Clients.Group(meeting.ConversationId.ToString()).SendAsync("MeetingEnded", new
            {
                MeetingId = meeting.Id,
                ConversationId = meeting.ConversationId,
                EndedAt = meeting.EndedAt
            });
        }
    }
}