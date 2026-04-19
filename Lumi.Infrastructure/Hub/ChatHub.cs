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
using System.Security.Cryptography;
using System.Text;
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

        // ==========================================
        // E2EE HANDSHAKE METHODS
        // ==========================================

        public async Task SendSecureIdentity(int conversationId, string idPubKeyBase64, string rsaPubKeyBase64, string signature)
        {
            var userIdStr = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdStr)) return;
            int senderId = int.Parse(userIdStr);
            
            // SECURITY UPGRADE: Persist Identity Public Key to DB
            var existingKey = await _context.UserKeys.FirstOrDefaultAsync(uk => uk.UserId == senderId && uk.IsActive);
            if (existingKey == null)
            {
                _context.UserKeys.Add(new UserKey
                {
                    UserId = senderId,
                    PublicKey = idPubKeyBase64,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                    KeyVersion = 1
                });
            }
            else if (existingKey.PublicKey != idPubKeyBase64)
            {
                // Key rotation or potentially MITM? For now, update it (Production should verify first)
                existingKey.IsActive = false;
                _context.UserKeys.Add(new UserKey
                {
                    UserId = senderId,
                    PublicKey = idPubKeyBase64,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow,
                    KeyVersion = existingKey.KeyVersion + 1
                });
            }
            await _context.SaveChangesAsync();

            // Broadcast cho các thành viên khác để họ biết mình vừa tham gia (Chào sân)
            await Clients.OthersInGroup(conversationId.ToString())
                         .SendAsync("ReceiveSecureIdentity", senderId, idPubKeyBase64, rsaPubKeyBase64, signature, conversationId, false);
        }

        public async Task SendSecureIdentityToUser(int conversationId, int targetUserId, string idPubKeyBase64, string rsaPubKeyBase64, string signature)
        {
            var senderIdStr = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(senderIdStr)) return;
            int senderId = int.Parse(senderIdStr);

            // Gửi đích danh phản hồi (Đáp lễ) để tránh làm phiền toàn nhóm
            await Clients.Group($"user_{targetUserId}")
                         .SendAsync("ReceiveSecureIdentity", senderId, idPubKeyBase64, rsaPubKeyBase64, signature, conversationId, true);
        }

        public async Task SendSecureSenderKey(int conversationId, int targetUserId, string encryptedKeyBase64)
        {
            var userIdStr = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdStr)) return;
            int senderId = int.Parse(userIdStr);

            // Chá»‰ gá»­i Key Ä‘Ă£ mĂ£ hĂ³a báº±ng RSA cho Ä‘Ăºng ngÆ°á»i nháº­n (targetUserId)
            await Clients.Group($"user_{targetUserId}")
                         .SendAsync("ReceiveSecureSenderKey", senderId, encryptedKeyBase64, conversationId);
        }

        // ==========================================
        // SECURE MESSAGING
        // ==========================================

        public async Task SendMessageSecure(int conversationId, string encryptedContent, string iv, string signature, string messageType, int parentMessageId)
        {
            try 
            {
                var userIdStr = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdStr)) return;
                int senderId = int.Parse(userIdStr);
                
                // PRODUCTION UPGRADE: Verify Message Signature
                var senderKey = await _context.UserKeys.FirstOrDefaultAsync(uk => uk.UserId == senderId && uk.IsActive);
                if (senderKey != null)
                {
                    bool isSigValid = VerifyMessageSignature(encryptedContent, signature, senderKey.PublicKey);
                    if (!isSigValid)
                    {
                        _logger.LogWarning("[Security] Invalid signature from user {UserId} in conversation {ConversationId}", senderId, conversationId);
                        throw new HubException("Invalid message signature. Security check failed.");
                    }
                }

                // Ensure non-null values for DB safety
                var msg = new Message 
                { 
                    ConversationId = conversationId, 
                    SenderId = senderId, 
                    EncryptedContent = encryptedContent ?? "", 
                    IV = iv ?? "", 
                    Signature = signature ?? "",
                    MessageType = string.IsNullOrEmpty(messageType) ? "PLAIN" : messageType, 
                    ParentMessageId = parentMessageId > 0 ? parentMessageId : null,
                    CreatedAt = DateTime.UtcNow 
                };
                
                _context.Messages.Add(msg);
                
                var conversation = await _context.Conversations.FindAsync(conversationId);
                if (conversation != null) conversation.LastMessageAt = DateTime.UtcNow;
                
                await _context.SaveChangesAsync();

                var sender = await _context.Users.FindAsync(senderId);
                var avatarPath = sender?.AvatarPath;

                await Clients.Group(conversationId.ToString()).SendAsync("ReceiveMessage", new 
                {
                    id = msg.Id,
                    conversationId = conversationId,
                    senderId = senderId,
                    senderName = sender?.FullName ?? "User",
                    content = msg.EncryptedContent,
                    iv = msg.IV,
                    sig = msg.Signature,
                    messageType = msg.MessageType,
                    createdAt = msg.CreatedAt.ToString("o"),
                    parentMessageId = msg.ParentMessageId,
                    avatarPath = avatarPath,
                    attachments = new List<object>()
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SendMessageSecure] Error saving or broadcasting message in conversation {ConversationId}", conversationId);
                throw new HubException("Failed to save or broadcast secure message. Check server logs.");
            }
        }

        // ==========================================
        // STANDARD CHAT FEATURES (MISSING)
        // ==========================================

        public async Task SendTyping(int conversationId)
        {
            var userIdStr = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdStr)) return;
            int userId = int.Parse(userIdStr);
            var user = await _context.Users.FindAsync(userId);

            await Clients.OthersInGroup(conversationId.ToString()).SendAsync("UserTyping", new {
                conversationId,
                userId,
                userName = user?.FullName ?? user?.Username ?? "User"
            });
        }

        public async Task SendNotification(string message)
        {
            var userIdStr = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var senderName = "Há»‡ thá»‘ng";
            if (!string.IsNullOrEmpty(userIdStr)) {
                var sender = await _context.Users.FindAsync(int.Parse(userIdStr));
                senderName = sender?.FullName ?? sender?.Username ?? "Admin";
            }

            await Clients.All.SendAsync("ReceiveNotification", new {
                id = Guid.NewGuid().ToString(),
                message,
                sender = senderName,
                time = DateTime.UtcNow
            });
        }

        public async Task SendSticker(int conversationId, string stickerUrl)
        {
            var userIdStr = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdStr)) return;
            int senderId = int.Parse(userIdStr);

            var msg = new Message {
                ConversationId = conversationId,
                SenderId = senderId,
                StickerUrl = stickerUrl,
                MessageType = "Sticker",
                EncryptedContent = "[Sticker]",
                IV = "",
                Signature = "",
                CreatedAt = DateTime.UtcNow
            };
            _context.Messages.Add(msg);
            await _context.SaveChangesAsync();

            await Clients.Group(conversationId.ToString()).SendAsync("ReceiveMessage", new {
                id = msg.Id,
                conversationId,
                senderId,
                stickerUrl,
                messageType = "Sticker",
                createdAt = msg.CreatedAt.ToString("o")
            });
        }

        public async Task HideMessageForMe(int messageId)
        {
            // Logic to track hidden messages per user could be implemented here
            await Clients.Caller.SendAsync("MessageHidden", messageId);
        }

        public async Task TogglePinMessage(int messageId)
        {
            var msg = await _context.Messages.FindAsync(messageId);
            if (msg == null) return;
            
            msg.IsPinned = !(msg.IsPinned ?? false);
            msg.PinnedAt = msg.IsPinned.Value ? DateTime.UtcNow : (DateTime?)null;
            
            await _context.SaveChangesAsync();

            await Clients.Group(msg.ConversationId.ToString()).SendAsync("MessagePinned", new {
                conversationId = msg.ConversationId,
                messageId = msg.Id,
                isPinned = msg.IsPinned
            });
        }

        private bool VerifyMessageSignature(string data, string signatureBase64, string publicKeyBase64)
        {
            try
            {
                var pubKeyBytes = Convert.FromBase64String(publicKeyBase64);
                var sigBytes = Convert.FromBase64String(signatureBase64);
                var dataBytes = Encoding.UTF8.GetBytes(data);

                using var ecdsa = ECDsa.Create();
                
                // WebCrypto "raw" for P-256 is usually 64 bytes (X | Y)
                // If 65 bytes, it has the 0x04 format byte
                byte[] x = new byte[32];
                byte[] y = new byte[32];
                
                if (pubKeyBytes.Length == 65 && pubKeyBytes[0] == 0x04)
                {
                    Buffer.BlockCopy(pubKeyBytes, 1, x, 0, 32);
                    Buffer.BlockCopy(pubKeyBytes, 33, y, 0, 32);
                }
                else if (pubKeyBytes.Length == 64)
                {
                    Buffer.BlockCopy(pubKeyBytes, 0, x, 0, 32);
                    Buffer.BlockCopy(pubKeyBytes, 32, y, 0, 32);
                }
                else return false;

                ecdsa.ImportParameters(new ECParameters
                {
                    Curve = ECCurve.NamedCurves.nistP256,
                    Q = new ECPoint { X = x, Y = y }
                });

                return ecdsa.VerifyData(dataBytes, sigBytes, HashAlgorithmName.SHA256);
            }
            catch { return false; }
        }
    }
}
