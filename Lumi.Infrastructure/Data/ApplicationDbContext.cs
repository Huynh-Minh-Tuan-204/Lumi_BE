using Lumi.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Lumi.Infrastructure.Data
{
    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options)
        {
        }

        // 1. ROLES & DEPARTMENTS
        public DbSet<Role> Roles { get; set; }
        public DbSet<Department> Departments { get; set; }

        // 2. USERS & PASSWORD LIFECYCLE
        public DbSet<User> Users { get; set; }

        // 3. DEVICE CONTROL & AUTHENTICATION
        public DbSet<UserDevice> UserDevices { get; set; }
        public DbSet<RefreshToken> RefreshTokens { get; set; }

        // 4. ENCRYPTION KEYS (MASTER & SESSION)
        public DbSet<UserKey> UserKeys { get; set; }
        public DbSet<UserSessionKey> UserSessionKeys { get; set; }

        // 5. CONVERSATIONS & CHAT
        public DbSet<Conversation> Conversations { get; set; }
        public DbSet<ConversationMember> ConversationMembers { get; set; }

        // 6. MESSAGES & MULTI-DEVICE E2EE KEYS
        public DbSet<Message> Messages { get; set; }
        public DbSet<MessageRecipientKey> MessageRecipientKeys { get; set; }
        public DbSet<MessageRead> MessageReads { get; set; }
        public DbSet<Announcement> Announcements { get; set; }
        // 7. ATTACHMENTS (ENCRYPTED FILES)
        public DbSet<Attachment> Attachments { get; set; }
        public DbSet<AttachmentRecipientKey> AttachmentRecipientKeys { get; set; }

        // 8. MEETINGS & WEBRTC
        public DbSet<Meeting> Meetings { get; set; }
        public DbSet<MeetingParticipant> MeetingParticipants { get; set; }
        public DbSet<CallRecording> CallRecordings { get; set; }
        
        // 9. SCHEDULES
        public DbSet<WorkSchedule> WorkSchedules { get; set; }
        public DbSet<WorkScheduleParticipant> WorkScheduleParticipants { get; set; }
        
        public DbSet<SignalRConnection> SignalRConnections { get; set; }
        public DbSet<AuditLog> AuditLogs { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // ==========================================
            // CẤU HÌNH FLUENT API - DATABASE HARDENING
            // ==========================================

            // --- UNIQUE INDEXES (Chống trùng lặp dữ liệu) ---
            modelBuilder.Entity<Role>().HasIndex(r => r.Name).IsUnique();
            modelBuilder.Entity<User>().HasIndex(u => u.EmployeeCode).IsUnique();
            modelBuilder.Entity<User>().HasIndex(u => u.Username).IsUnique();
            modelBuilder.Entity<UserDevice>().HasIndex(ud => ud.DeviceIdentifier).IsUnique();
            modelBuilder.Entity<RefreshToken>().HasIndex(rt => rt.TokenHash).IsUnique();
            modelBuilder.Entity<UserKey>().HasIndex(uk => new { uk.UserId, uk.KeyVersion }).IsUnique();

            // Composite Unique Indexes cho các bảng N-N
            modelBuilder.Entity<ConversationMember>().HasIndex(cm => new { cm.ConversationId, cm.UserId }).IsUnique();
            modelBuilder.Entity<MessageRecipientKey>().HasIndex(mrk => new { mrk.MessageId, mrk.UserId, mrk.DeviceId }).IsUnique();
            modelBuilder.Entity<MessageRead>().HasIndex(mr => new { mr.MessageId, mr.UserId, mr.DeviceId }).IsUnique();
            modelBuilder.Entity<AttachmentRecipientKey>().HasIndex(ark => new { ark.AttachmentId, ark.UserId, ark.DeviceId }).IsUnique();

            // Performance Index
            modelBuilder.Entity<AuditLog>().HasIndex(al => al.UserId);

            // --- CẤU HÌNH QUAN HỆ ĐẶC BIỆT & NGĂN LỖI CASCADE ---

            // 1. User -> Messages (Chống xóa dây chuyền để không mất lịch sử chat)
            modelBuilder.Entity<Message>()
                .HasOne(m => m.Sender)
                .WithMany(u => u.SentMessages)
                .HasForeignKey(m => m.SenderId)
                .OnDelete(DeleteBehavior.Restrict);

            // 2. Message -> ParentMessage (Self-Reference cho tính năng Reply)
            modelBuilder.Entity<Message>()
                .HasOne(m => m.ParentMessage)
                .WithMany()
                .HasForeignKey(m => m.ParentMessageId)
                .OnDelete(DeleteBehavior.NoAction);

            // 3. RefreshToken -> ReplacedToken (Self-Reference Token Chain)
            modelBuilder.Entity<RefreshToken>()
                .HasOne(rt => rt.ReplacedByToken)
                .WithOne(rt => rt.ReplacedToken)
                .HasForeignKey<RefreshToken>(rt => rt.ReplacedByTokenId)
                .OnDelete(DeleteBehavior.NoAction);

            // 4. Các cấu hình Restrict để tránh lỗi Multiple Cascade Paths của SQL Server
            modelBuilder.Entity<Department>()
                .HasOne(d => d.CreatedByUser)
                .WithMany()
                .HasForeignKey(d => d.CreatedBy)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<Conversation>()
                .HasOne(c => c.Creator)
                .WithMany()
                .HasForeignKey(c => c.CreatedBy)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<Meeting>()
                .HasOne(m => m.Creator)
                .WithMany()
                .HasForeignKey(m => m.CreatedBy)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<CallRecording>()
                .HasOne(cr => cr.Meeting)
                .WithMany(m => m.Recordings)
                .HasForeignKey(cr => cr.MeetingId)
                .OnDelete(DeleteBehavior.Cascade);

            
            // Start-up sync for performance indexes
            modelBuilder.Entity<SignalRConnection>().HasIndex(s => s.UserId);
            modelBuilder.Entity<SignalRConnection>().HasIndex(s => s.IsActive);
            modelBuilder.Entity<Message>().HasIndex(m => m.ConversationId);
            modelBuilder.Entity<Message>().HasIndex(m => m.CreatedAt);
            modelBuilder.Entity<ConversationMember>().HasIndex(cm => cm.UserId);
            modelBuilder.Entity<ConversationMember>().HasIndex(cm => cm.IsActive);

            // Meeting performance indexes
            modelBuilder.Entity<Meeting>().HasIndex(m => m.ConversationId);
            modelBuilder.Entity<MeetingParticipant>().HasIndex(mp => mp.MeetingId);
            modelBuilder.Entity<MeetingParticipant>().HasIndex(mp => mp.UserId);

            // Chat and Messaging performance
            modelBuilder.Entity<Conversation>().HasIndex(c => c.LastMessageAt);
            modelBuilder.Entity<Message>().HasIndex(m => new { m.ConversationId, m.CreatedAt });
            modelBuilder.Entity<ConversationMember>().HasIndex(cm => new { cm.UserId, cm.IsActive });

            // Schedule Config
            modelBuilder.Entity<WorkSchedule>()
                .HasOne(ws => ws.Creator)
                .WithMany()
                .HasForeignKey(ws => ws.CreatedBy)
                .OnDelete(DeleteBehavior.Restrict);

            // ==========================================
            // FIX LỖI CAST GUID TO STRING (DỨT ĐIỂM)
            // ==========================================
            // Sử dụng Converter để ánh xạ giữa Guid (Code) và String/Guid (DB) một cách linh hoạt
            modelBuilder.Entity<Meeting>()
                .Property(m => m.MeetingGuid)
                .HasMaxLength(100);

            modelBuilder.Entity<UserDevice>()
                .Property(ud => ud.DeviceIdentifier)
                .HasConversion(
                    v => v != null ? v.ToString() : null,
                    v => v != null ? v.ToString() : null
                );
            
            // Đảm bảo DeviceIdentifier trong C# vẫn là string để không làm gãy AuthService
            // Nhưng chuyển MeetingGuid về Guid? để chuẩn hóa MeetingsController logic

            var cascadeFKs = modelBuilder.Model.GetEntityTypes()
                .SelectMany(t => t.GetForeignKeys())
                .Where(fk => !fk.IsOwnership && fk.DeleteBehavior == DeleteBehavior.Cascade);

            foreach (var fk in cascadeFKs)
            {
                fk.DeleteBehavior = DeleteBehavior.Restrict;
            }
        }
    }
}