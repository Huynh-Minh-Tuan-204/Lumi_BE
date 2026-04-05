#nullable disable
using System;
using System.Collections.Generic;

namespace Lumi.Domain.Entities
{
    public class Conversation
    {
        public int Id { get; set; }
        public string Name { get; set; } // Tên nhóm hoặc tên người chat cùng
        public string Type { get; set; } // "Private" hoặc "Group"
        public string AvatarPath { get; set; }
        public string BackgroundPath { get; set; }

        public int CreatedBy { get; set; }
        public virtual User Creator { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? UpdatedAt { get; set; }
        public DateTime? LastMessageAt { get; set; }

        // Quan hệ 1-N với Messages
        public virtual ICollection<Message> Messages { get; set; }
        public virtual ICollection<ConversationMember> Members { get; set; }
    }
}