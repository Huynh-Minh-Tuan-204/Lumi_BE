namespace Lumi.Domain.Entities
{
    public class ConversationMember
    {
        public int Id { get; set; }
        public int ConversationId { get; set; }
        public Conversation Conversation { get; set; }
        public int UserId { get; set; }
        public User User { get; set; }
        public string RoleInConversation { get; set; }
        public DateTime JoinedAt { get; set; }
        public DateTime? LeftAt { get; set; }
        public bool IsActive { get; set; }
    }
}