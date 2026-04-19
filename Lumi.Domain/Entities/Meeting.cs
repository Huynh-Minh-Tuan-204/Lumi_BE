#nullable enable
namespace Lumi.Domain.Entities
{
    public class Meeting
    {
        public int Id { get; set; }
        public int ConversationId { get; set; }
        public Conversation? Conversation { get; set; }
        public string Title { get; set; } = string.Empty;
        public int CreatedBy { get; set; }
        public User? Creator { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? EndedAt { get; set; }
        public bool IsRecording { get; set; }
        
        // Updated to string to support 8-character alphanumeric codes
        public string? MeetingGuid { get; set; }
        public string? CallType { get; set; } = "video";
        public string? SettingsJson { get; set; } = "{}";
        
        public virtual ICollection<MeetingParticipant> Participants { get; set; } = new List<MeetingParticipant>();
        public virtual ICollection<CallRecording> Recordings { get; set; } = new List<CallRecording>();
    }
}