namespace Lumi.Domain.Entities
{
    public class Meeting
    {
        public int Id { get; set; }
        public int ConversationId { get; set; }
        public Conversation Conversation { get; set; }
        public string Title { get; set; }
        public int CreatedBy { get; set; }
        public User Creator { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? EndedAt { get; set; }
        public bool IsRecording { get; set; }
    }
}