using System;

namespace Lumi.Domain.Entities
{
    public class CallRecording
    {
        public int Id { get; set; }
        public int MeetingId { get; set; }
        public virtual Meeting Meeting { get; set; }
        public string FilePath { get; set; }
        public string EncryptedFilePath { get; set; }
        public string IV { get; set; }
        public long FileSize { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
