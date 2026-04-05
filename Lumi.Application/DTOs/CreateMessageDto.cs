namespace Lumi.Application.DTOs
{
    public class CreateMessageDto
    {
        public int ConversationId { get; set; }
        public string EncryptedContent { get; set; }
        public string IV { get; set; }
        public string MessageType { get; set; } = "Text";
        public int? ParentMessageId { get; set; }
    }
}
