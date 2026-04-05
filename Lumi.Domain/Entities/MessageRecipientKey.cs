namespace Lumi.Domain.Entities
{
    public class MessageRecipientKey
    {
        public int Id { get; set; }
        public int MessageId { get; set; }
        public Message Message { get; set; }
        public int UserId { get; set; }
        public User User { get; set; }
        public int DeviceId { get; set; }
        public UserDevice Device { get; set; }
        public string EncryptedSymmetricKey { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}