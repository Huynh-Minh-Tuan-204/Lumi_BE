namespace Lumi.Domain.Entities
{
    public class AttachmentRecipientKey
    {
        public int Id { get; set; }
        public int AttachmentId { get; set; }
        public Attachment Attachment { get; set; }
        public int UserId { get; set; }
        public User User { get; set; }
        public int DeviceId { get; set; }
        public UserDevice Device { get; set; }
        public string EncryptedSymmetricKey { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}