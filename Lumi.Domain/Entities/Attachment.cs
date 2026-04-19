namespace Lumi.Domain.Entities
{
    public class Attachment
    {
        public int Id { get; set; }
        public int MessageId { get; set; }
        public Message Message { get; set; }
        public string FileName { get; set; }
        public string EncryptedFilePath { get; set; }
        public long FileSize { get; set; }
        public string MimeType { get; set; }
        public string FileHash { get; set; }
        public string IV { get; set; }
        public string? Signature { get; set; }
        public int UploadedBy { get; set; }
        public User Uploader { get; set; }
        public DateTime UploadedAt { get; set; }
    }
}