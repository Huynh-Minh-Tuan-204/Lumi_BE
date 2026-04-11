#nullable disable
using System;
using System.Collections.Generic;

namespace Lumi.Domain.Entities
{
    public class Message
    {
        public int Id { get; set; }
        public int? ConversationId { get; set; }
        public virtual Conversation Conversation { get; set; }

        public int SenderId { get; set; }
        public virtual User Sender { get; set; }

        public string EncryptedContent { get; set; }
        public string IV { get; set; }
        public string MessageType { get; set; } = "Text";

        public int? ParentMessageId { get; set; }
        public virtual Message ParentMessage { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? EditedAt { get; set; }
        public DateTime? DeletedAt { get; set; }
        public bool? IsDeleted { get; set; } = false;
        public bool? IsRead { get; set; } = false;

        public bool? IsPinned { get; set; } = false;
        public DateTime? PinnedAt { get; set; }
        public int? PinnedBy { get; set; }
        public string StickerUrl { get; set; }
        public string Metadata { get; set; } // JSON metadata for categories, titles, etc.

        public virtual ICollection<MessageRead> MessageReads { get; set; }
        public virtual ICollection<Attachment> Attachments { get; set; }
    }
}