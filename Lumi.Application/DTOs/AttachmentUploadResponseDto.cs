using System;

namespace Lumi.Application.DTOs
{
    public class AttachmentUploadResponseDto
    {
        public int Id { get; set; }
        public string FileName { get; set; }
        public string EncryptedFilePath { get; set; }
        public long FileSize { get; set; }
        public string MimeType { get; set; }
        public int UploadedBy { get; set; }
        public DateTime UploadedAt { get; set; }
    }
}
