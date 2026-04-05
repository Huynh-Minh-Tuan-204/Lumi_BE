namespace Lumi.Domain.Entities
{
    public class MeetingRecording
    {
        public int Id { get; set; }
        public int MeetingId { get; set; }
        public Meeting Meeting { get; set; }
        public string EncryptedFilePath { get; set; }
        public long FileSize { get; set; }
        public string FileHash { get; set; }
        public string IV { get; set; }
        public int CreatedBy { get; set; }
        public User Creator { get; set; } 
        public DateTime CreatedAt { get; set; }
    }
}