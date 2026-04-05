namespace Lumi.Domain.Entities
{
    public class User
    {
        public User()
        {
            Devices = new HashSet<UserDevice>();
            Keys = new HashSet<UserKey>();
            SentMessages = new HashSet<Message>();
            Conversations = new HashSet<ConversationMember>();
            AuditLogs = new HashSet<AuditLog>();
        }
        public string PublicKey { get; set; }
        public int Id { get; set; }

        public string EmployeeCode { get; set; }

        public string Username { get; set; }

        public string PasswordHash { get; set; }

        public string PasswordSalt { get; set; }

        public string FullName { get; set; }

        public string Email { get; set; }

        public string Phone { get; set; }

        public string AvatarPath { get; set; }

        public int? RoleId { get; set; }
        public Role Role { get; set; }

        public int? DepartmentId { get; set; }
        public Department Department { get; set; }

        public bool IsActive { get; set; }

        public bool MustChangePassword { get; set; }

        public DateTime? PasswordChangedAt { get; set; }

        public DateTime? PasswordExpiresAt { get; set; }

        public int FailedLoginAttempts { get; set; }

        public DateTime? LockedUntil { get; set; }

        public DateTime? LastLogin { get; set; }

        public DateTime CreatedAt { get; set; }

        public DateTime? UpdatedAt { get; set; }

        public ICollection<UserDevice> Devices { get; set; }

        public ICollection<UserKey> Keys { get; set; }

        public ICollection<Message> SentMessages { get; set; }

        public ICollection<ConversationMember> Conversations { get; set; }

        public ICollection<AuditLog> AuditLogs { get; set; }
        public bool IsFirstLogin { get; set; } = true;
    }
}